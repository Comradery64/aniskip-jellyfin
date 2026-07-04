using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AniSkip.AniSkip;
using Jellyfin.Plugin.AniSkip.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniSkip.Providers;

/// <summary>
/// A Jellyfin media segment provider that fetches anime opening/ending/recap skip
/// times from AniSkip and publishes them as Intro/Outro/Recap segments.
/// </summary>
public sealed class AniSkipSegmentProvider : IMediaSegmentProvider
{
    private static readonly string[] _malKeys = { "MyAnimeList", "Mal" };
    private static readonly string[] _aniListKeys = { "AniList" };
    private static readonly string[] _aniDbKeys = { "AniDB" };
    private static readonly string[] _tvdbKeys = { "Tvdb" };

    private readonly ILibraryManager _libraryManager;
    private readonly ArmIdMapper _armIdMapper;
    private readonly AniSkipApiClient _apiClient;
    private readonly FribbMappingService _fribb;
    private readonly ILogger<AniSkipSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniSkipSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="armIdMapper">The AniDB/AniList to MAL id mapper.</param>
    /// <param name="apiClient">The AniSkip API client.</param>
    /// <param name="fribb">The TVDB/season to MAL mapping service.</param>
    /// <param name="logger">The logger.</param>
    public AniSkipSegmentProvider(
        ILibraryManager libraryManager,
        ArmIdMapper armIdMapper,
        AniSkipApiClient apiClient,
        FribbMappingService fribb,
        ILogger<AniSkipSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _armIdMapper = armIdMapper;
        _apiClient = apiClient;
        _fribb = fribb;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AniSkip";

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public async ValueTask<bool> Supports(BaseItem item)
    {
        // Only episodes, never specials (season 0). Runs for every scanned item.
        if (item is not Episode episode
            || episode.IndexNumber is null
            || (episode.ParentIndexNumber ?? 1) == 0)
        {
            return false;
        }

        // An explicit anime id anywhere on the season/series -> supported (cheap, in-memory).
        if (FindAnimeId(episode) is not null)
        {
            return true;
        }

        // Otherwise: is this an anime known to the Fribb TVDB dataset? This is what
        // makes multi-season shows (which carry only a series-level TVDB id) work.
        if (Config.EnableTvdbMapping && GetSeriesTvdbId(episode) is int tvdbId)
        {
            await _fribb.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
            return _fribb.HasSeries(tvdbId);
        }

        return false;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var config = Config;

        if (_libraryManager.GetItemById(request.ItemId) is not Episode episode
            || episode.IndexNumber is not int episodeNumber
            || (episode.ParentIndexNumber ?? 1) == 0)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var runtimeSeconds = episode.RunTimeTicks.HasValue
            ? episode.RunTimeTicks.Value / (double)TimeSpan.TicksPerSecond
            : 0d;
        if (runtimeSeconds < config.MinEpisodeSeconds)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var resolved = await ResolveMalAndEpisode(episode, episodeNumber, config, cancellationToken).ConfigureAwait(false);
        if (resolved is not (int malId, int aniskipEpisode))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var types = new List<string>(5);
        if (config.EnableIntro)
        {
            types.Add("op");
        }

        if (config.EnableOutro)
        {
            types.Add("ed");
        }

        if (config.EnableRecap)
        {
            types.Add("recap");
        }

        if (config.EnableMixed)
        {
            types.Add("mixed-op");
            types.Add("mixed-ed");
        }

        if (types.Count == 0)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var intervals = await _apiClient
            .GetSkipTimesAsync(malId, aniskipEpisode, runtimeSeconds, types, cancellationToken)
            .ConfigureAwait(false);
        if (intervals.Count == 0)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var segments = new List<MediaSegmentDto>(intervals.Count);
        foreach (var interval in intervals)
        {
            if (MapType(interval.SkipType) is not MediaSegmentType type)
            {
                continue;
            }

            var startTicks = (long)(interval.StartSeconds * TimeSpan.TicksPerSecond);
            var endTicks = (long)(interval.EndSeconds * TimeSpan.TicksPerSecond);
            if (episode.RunTimeTicks is long runtimeTicks && endTicks > runtimeTicks)
            {
                endTicks = runtimeTicks;
            }

            if (startTicks < 0 || endTicks <= startTicks)
            {
                continue;
            }

            segments.Add(new MediaSegmentDto
            {
                ItemId = request.ItemId,
                Type = type,
                StartTicks = startTicks,
                EndTicks = endTicks,
            });
        }

        if (segments.Count > 0)
        {
            _logger.LogDebug(
                "AniSkip: {Count} segment(s) for {Series} S{Season}E{Episode} -> MAL {MalId} ep {AniEp}",
                segments.Count,
                episode.SeriesName,
                episode.ParentIndexNumber,
                episodeNumber,
                malId,
                aniskipEpisode);
        }

        return segments;
    }

    /// <summary>
    /// Resolves the (MAL id, AniSkip episode number) for an episode, trying, in order:
    /// a season-level anime id, the TVDB+season Fribb mapping (multi-cour), and finally
    /// a series-level anime id (confined to season 1).
    /// </summary>
    private async Task<(int MalId, int AniSkipEpisode)?> ResolveMalAndEpisode(
        Episode episode,
        int episodeNumber,
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var season = episode.Season
            ?? (episode.SeasonId != Guid.Empty ? _libraryManager.GetItemById(episode.SeasonId) as Season : null);
        var series = episode.Series
            ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);
        var seasonNumber = episode.ParentIndexNumber ?? 1;

        // 1. Season-level anime id: most precise, already the right cour.
        if (season is not null && GetAnimeId(season) is (string src, string id))
        {
            var mal = await ToMal(src, id, cancellationToken).ConfigureAwait(false);
            if (mal is int m)
            {
                return (m, episodeNumber);
            }
        }

        // 2. TVDB series id + season -> Fribb -> correct MAL entry (+ per-cour episode).
        if (config.EnableTvdbMapping
            && series is not null
            && GetProviderIntId(series, _tvdbKeys) is int tvdbId)
        {
            await _fribb.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (_fribb.TryResolve(tvdbId, seasonNumber, episodeNumber, out var mal, out var aniEp))
            {
                return (mal, aniEp);
            }
        }

        // 3. Series-level anime id: only describes cour 1, so confine to season 1
        //    (unless the operator opts in for single-cour libraries).
        if (series is not null
            && (seasonNumber <= 1 || config.AssumeSingleCour)
            && GetAnimeId(series) is (string ssrc, string sid))
        {
            var mal = await ToMal(ssrc, sid, cancellationToken).ConfigureAwait(false);
            if (mal is int m)
            {
                return (m, episodeNumber);
            }
        }

        return null;
    }

    private async Task<int?> ToMal(string source, string id, CancellationToken cancellationToken)
    {
        if (string.Equals(source, "mal", StringComparison.Ordinal))
        {
            return int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }

        return await _armIdMapper.ToMalAsync(source, id, cancellationToken).ConfigureAwait(false);
    }

    private static MediaSegmentType? MapType(string skipType) => skipType switch
    {
        "op" => MediaSegmentType.Intro,
        "mixed-op" => MediaSegmentType.Intro,
        "ed" => MediaSegmentType.Outro,
        "mixed-ed" => MediaSegmentType.Outro,
        "recap" => MediaSegmentType.Recap,
        _ => null,
    };

    /// <summary>Whether the season or series carries any explicit anime id.</summary>
    private string? FindAnimeId(Episode episode)
    {
        var season = episode.Season
            ?? (episode.SeasonId != Guid.Empty ? _libraryManager.GetItemById(episode.SeasonId) as Season : null);
        var series = episode.Series
            ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);
        return (season is not null ? GetAnimeId(season)?.Source : null)
            ?? (series is not null ? GetAnimeId(series)?.Source : null);
    }

    private int? GetSeriesTvdbId(Episode episode)
    {
        var series = episode.Series
            ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);
        return series is not null ? GetProviderIntId(series, _tvdbKeys) : null;
    }

    private static (string Source, string Id)? GetAnimeId(BaseItem item)
    {
        var mal = GetProviderId(item, _malKeys);
        if (mal is not null && int.TryParse(mal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return ("mal", mal);
        }

        var aniList = GetProviderId(item, _aniListKeys);
        if (aniList is not null)
        {
            return ("anilist", aniList);
        }

        var aniDb = GetProviderId(item, _aniDbKeys);
        if (aniDb is not null)
        {
            return ("anidb", aniDb);
        }

        return null;
    }

    private static int? GetProviderIntId(BaseItem item, string[] keys)
    {
        var raw = GetProviderId(item, keys);
        return raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? GetProviderId(BaseItem item, string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in item.ProviderIds)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(pair.Value))
                {
                    return pair.Value;
                }
            }
        }

        return null;
    }
}
