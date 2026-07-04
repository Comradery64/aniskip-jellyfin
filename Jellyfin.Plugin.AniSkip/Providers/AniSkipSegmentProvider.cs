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

    private readonly ILibraryManager _libraryManager;
    private readonly ArmIdMapper _armIdMapper;
    private readonly AniSkipApiClient _apiClient;
    private readonly ILogger<AniSkipSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AniSkipSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="armIdMapper">The AniDB/AniList to MAL id mapper.</param>
    /// <param name="apiClient">The AniSkip API client.</param>
    /// <param name="logger">The logger.</param>
    public AniSkipSegmentProvider(
        ILibraryManager libraryManager,
        ArmIdMapper armIdMapper,
        AniSkipApiClient apiClient,
        ILogger<AniSkipSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _armIdMapper = armIdMapper;
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AniSkip";

    private static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item)
    {
        // Only episodes, never specials (season 0), and only when the season or series
        // carries an anime id we can act on. This runs for every scanned item, so it
        // stays in-memory (no network).
        if (item is not Episode episode
            || episode.IndexNumber is null
            || (episode.ParentIndexNumber ?? 1) == 0)
        {
            return ValueTask.FromResult(false);
        }

        var (source, _, _) = ResolveAnimeSource(episode);
        return ValueTask.FromResult(source is not null);
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

        var (source, sourceId, fromSeason) = ResolveAnimeSource(episode);
        if (source is null || sourceId is null)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        // A series-level MAL id maps to the first cour only. Applying it to later
        // seasons would place skips at the wrong times, so skip unless the operator
        // opts in.
        if (!fromSeason && (episode.ParentIndexNumber ?? 1) > 1 && !config.AssumeSingleCour)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        int? malId;
        if (string.Equals(source, "mal", StringComparison.Ordinal))
        {
            malId = int.TryParse(sourceId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        else
        {
            malId = await _armIdMapper.ToMalAsync(source, sourceId, cancellationToken).ConfigureAwait(false);
        }

        if (malId is null)
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
            .GetSkipTimesAsync(malId.Value, episodeNumber, runtimeSeconds, types, cancellationToken)
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
                "AniSkip: {Count} segment(s) for {Series} S{Season}E{Episode} (MAL {MalId})",
                segments.Count,
                episode.SeriesName,
                episode.ParentIndexNumber,
                episodeNumber,
                malId);
        }

        return segments;
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

    /// <summary>
    /// Finds the best anime id for an episode. Season-level ids win over series-level
    /// ids because AniDB/AniList/MAL split multi-cour shows per season, and each season's
    /// id lines up with AniSkip's per-cour episode numbering.
    /// </summary>
    private (string? Source, string? Id, bool FromSeason) ResolveAnimeSource(Episode episode)
    {
        var season = episode.Season
            ?? (episode.SeasonId != Guid.Empty ? _libraryManager.GetItemById(episode.SeasonId) as Season : null);
        var series = episode.Series
            ?? (episode.SeriesId != Guid.Empty ? _libraryManager.GetItemById(episode.SeriesId) as Series : null);

        foreach (var (item, fromSeason) in new[] { ((BaseItem?)season, true), (series, false) })
        {
            if (item is null)
            {
                continue;
            }

            var mal = GetProviderId(item, _malKeys);
            if (mal is not null && int.TryParse(mal, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                return ("mal", mal, fromSeason);
            }

            var aniList = GetProviderId(item, _aniListKeys);
            if (aniList is not null)
            {
                return ("anilist", aniList, fromSeason);
            }

            var aniDb = GetProviderId(item, _aniDbKeys);
            if (aniDb is not null)
            {
                return ("anidb", aniDb, fromSeason);
            }
        }

        return (null, null, false);
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
