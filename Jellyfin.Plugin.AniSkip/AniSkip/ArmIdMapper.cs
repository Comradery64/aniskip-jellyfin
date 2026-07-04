using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniSkip.AniSkip;

/// <summary>
/// Maps AniDB / AniList ids to MyAnimeList ids via the arm (anime-relations-mapper)
/// service at arm.haglund.dev. AniSkip is keyed on MAL ids, but Jellyfin anime
/// libraries usually carry AniDB (and sometimes AniList) ids instead. Results are
/// cached in-process, including negatives, so a full-library scan hits arm at most
/// once per distinct source id.
/// </summary>
public sealed class ArmIdMapper
{
    private const string ArmBase = "https://arm.haglund.dev/api/v2/ids";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArmIdMapper> _logger;
    private readonly ConcurrentDictionary<string, int> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="ArmIdMapper"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ArmIdMapper(IHttpClientFactory httpClientFactory, ILogger<ArmIdMapper> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Resolves a MyAnimeList id for the given source id.
    /// </summary>
    /// <param name="source">The source database: "anidb" or "anilist".</param>
    /// <param name="id">The source id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The MAL id, or null if it cannot be resolved.</returns>
    public async Task<int?> ToMalAsync(string source, string id, CancellationToken cancellationToken)
    {
        var key = source + ":" + id;
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached == 0 ? null : cached;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var url = $"{ArmBase}?source={source}&id={Uri.EscapeDataString(id)}";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("myanimelist", out var mal)
                    && mal.ValueKind == JsonValueKind.Number
                    && mal.TryGetInt32(out var malId))
                {
                    _cache[key] = malId;
                    return malId;
                }
            }
        }
        catch (Exception ex)
        {
            // Network hiccup: cache nothing so a later scan can retry.
            _logger.LogDebug(ex, "arm id mapping failed for {Source}:{Id}", source, id);
            return null;
        }

        _cache[key] = 0;
        return null;
    }
}
