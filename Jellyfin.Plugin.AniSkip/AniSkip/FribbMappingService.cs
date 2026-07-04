using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniSkip.AniSkip;

/// <summary>
/// Resolves a TheTVDB series id + season number to the correct MyAnimeList entry
/// using the Fribb "anime-lists" dataset (https://github.com/Fribb/anime-lists).
///
/// This is the multi-cour solution: TVDB groups a whole franchise under one series
/// with seasons, while MAL (and therefore AniSkip) splits each cour into a separate
/// entry that numbers episodes from 1. Fribb maps each MAL entry to its
/// (tvdb_id, season, episode_offset), so a Jellyfin episode's per-season number can
/// be translated into the right (MAL id, episode) pair — including seasons that TVDB
/// merges from multiple cours (e.g. "New World" Part 1 + 2 share TVDB season 3).
/// The dataset is downloaded once and cached, refreshed weekly.
/// </summary>
public sealed class FribbMappingService
{
    private const string DataUrl = "https://raw.githubusercontent.com/Fribb/anime-lists/master/anime-list-full.json";
    private static readonly TimeSpan _maxCacheAge = TimeSpan.FromDays(7);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<FribbMappingService> _logger;
    private readonly SemaphoreSlim _loadLock = new(1, 1);

    // (tvdbId, season) -> cours in that season, ordered by episode offset ascending.
    private Dictionary<(int TvdbId, int Season), List<Cour>>? _index;
    private HashSet<int> _tvdbIds = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="FribbMappingService"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="applicationPaths">The application paths (for the cache location).</param>
    /// <param name="logger">The logger.</param>
    public FribbMappingService(
        IHttpClientFactory httpClientFactory,
        IApplicationPaths applicationPaths,
        ILogger<FribbMappingService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    private readonly record struct Cour(int MalId, int EpisodeOffset);

    private string CacheFile => Path.Combine(_applicationPaths.CachePath, "aniskip", "anime-list-full.json");

    /// <summary>
    /// Loads the dataset once (from cache or by download) and builds the lookup index.
    /// Cheap no-op on subsequent calls.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            return;
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_index is not null)
            {
                return;
            }

            var json = await GetJsonAsync(cancellationToken).ConfigureAwait(false);
            var (index, tvdbIds) = BuildIndex(json);
            _tvdbIds = tvdbIds;
            _index = index; // publish last so readers never see a half-built index
            _logger.LogInformation("AniSkip: Fribb mapping loaded ({SeriesCount} anime series, {KeyCount} tvdb/season keys)", tvdbIds.Count, index.Count);
        }
        catch (Exception ex)
        {
            // Degrade gracefully: an empty index disables the TVDB path (the arm/AniDB
            // fallback still works) and stops us re-hammering the download every item.
            _logger.LogWarning(ex, "AniSkip: failed to load Fribb mapping; TVDB->MAL resolution disabled this run");
            _index ??= new Dictionary<(int, int), List<Cour>>();
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Whether the dataset knows this TVDB series at all (i.e. it is anime).
    /// </summary>
    /// <param name="tvdbId">The TheTVDB series id.</param>
    /// <returns>True if the series is present in the anime dataset.</returns>
    public bool HasSeries(int tvdbId) => _tvdbIds.Contains(tvdbId);

    /// <summary>
    /// Resolves the MAL id and per-cour episode number for a Jellyfin episode.
    /// </summary>
    /// <param name="tvdbId">The TheTVDB series id.</param>
    /// <param name="season">The Jellyfin/TVDB season number.</param>
    /// <param name="episodeNumber">The episode number within the season (1-based).</param>
    /// <param name="malId">The resolved MAL id.</param>
    /// <param name="aniskipEpisode">The episode number to query AniSkip with.</param>
    /// <returns>True if a mapping was found.</returns>
    public bool TryResolve(int tvdbId, int season, int episodeNumber, out int malId, out int aniskipEpisode)
    {
        malId = 0;
        aniskipEpisode = 0;
        if (_index is null || !_index.TryGetValue((tvdbId, season), out var cours) || cours.Count == 0)
        {
            return false;
        }

        // Pick the cour whose offset is the greatest one strictly below the episode
        // number: episode 1..11 -> offset 0 cour, episode 12.. -> offset 11 cour, etc.
        Cour? chosen = null;
        foreach (var cour in cours)
        {
            if (cour.EpisodeOffset < episodeNumber && (chosen is null || cour.EpisodeOffset > chosen.Value.EpisodeOffset))
            {
                chosen = cour;
            }
        }

        if (chosen is null)
        {
            return false;
        }

        malId = chosen.Value.MalId;
        aniskipEpisode = episodeNumber - chosen.Value.EpisodeOffset;
        return aniskipEpisode > 0;
    }

    private async Task<string> GetJsonAsync(CancellationToken cancellationToken)
    {
        var cacheFile = CacheFile;
        if (File.Exists(cacheFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < _maxCacheAge)
        {
            return await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var json = await client.GetStringAsync(DataUrl, cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            var tmp = cacheFile + ".tmp";
            await File.WriteAllTextAsync(tmp, json, cancellationToken).ConfigureAwait(false);
            File.Move(tmp, cacheFile, overwrite: true);
            return json;
        }
        catch (Exception ex) when (File.Exists(cacheFile))
        {
            // Download failed but a stale cache exists — use it rather than nothing.
            _logger.LogWarning(ex, "AniSkip: Fribb download failed, falling back to stale cache");
            return await File.ReadAllTextAsync(cacheFile, cancellationToken).ConfigureAwait(false);
        }
    }

    private static (Dictionary<(int, int), List<Cour>> Index, HashSet<int> TvdbIds) BuildIndex(string json)
    {
        var index = new Dictionary<(int, int), List<Cour>>();
        var tvdbIds = new HashSet<int>();

        using var doc = JsonDocument.Parse(json);
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (!TryGetInt(entry, "tvdb_id", out var tvdbId)
                || !TryGetInt(entry, "mal_id", out var malId)
                || !TryGetSeason(entry, out var season))
            {
                continue;
            }

            var offset = GetEpisodeOffset(entry);
            tvdbIds.Add(tvdbId);
            var key = (tvdbId, season);
            if (!index.TryGetValue(key, out var list))
            {
                list = new List<Cour>();
                index[key] = list;
            }

            list.Add(new Cour(malId, offset));
        }

        return (index, tvdbIds);
    }

    private static bool TryGetInt(JsonElement entry, string name, out int value)
    {
        value = 0;
        return entry.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out value);
    }

    // "season" is either a number or an object like {"tvdb":1,"tmdb":1}.
    private static bool TryGetSeason(JsonElement entry, out int season)
    {
        season = 0;
        if (!entry.TryGetProperty("season", out var el))
        {
            return false;
        }

        if (el.ValueKind == JsonValueKind.Number)
        {
            return el.TryGetInt32(out season);
        }

        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("tvdb", out var tv)
            && tv.ValueKind == JsonValueKind.Number
            && tv.TryGetInt32(out season);
    }

    // "episode_offset" is 0 (number) or an object like {"tvdb":11,"tmdb":11}.
    private static int GetEpisodeOffset(JsonElement entry)
    {
        if (!entry.TryGetProperty("episode_offset", out var el))
        {
            return 0;
        }

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n))
        {
            return n;
        }

        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("tvdb", out var tv) && tv.ValueKind == JsonValueKind.Number && tv.TryGetInt32(out var t))
        {
            return t;
        }

        return 0;
    }
}
