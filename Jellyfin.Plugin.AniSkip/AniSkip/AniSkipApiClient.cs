using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniSkip.AniSkip;

/// <summary>
/// A single AniSkip skip interval for an episode.
/// </summary>
/// <param name="SkipType">AniSkip category: op, ed, recap, mixed-op, mixed-ed.</param>
/// <param name="StartSeconds">Interval start, in seconds.</param>
/// <param name="EndSeconds">Interval end, in seconds.</param>
public readonly record struct AniSkipInterval(string SkipType, double StartSeconds, double EndSeconds);

/// <summary>
/// Thin client for the AniSkip v2 API (api.aniskip.com). Concurrency is capped so a
/// full-library scan stays polite to the public endpoint.
/// </summary>
public sealed class AniSkipApiClient
{
    private const string ApiBase = "https://api.aniskip.com/v2";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AniSkipApiClient> _logger;
    private readonly SemaphoreSlim _gate = new(4, 4);

    /// <summary>
    /// Initializes a new instance of the <see cref="AniSkipApiClient"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public AniSkipApiClient(IHttpClientFactory httpClientFactory, ILogger<AniSkipApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Queries AniSkip skip times for a MAL id + episode number.
    /// </summary>
    /// <param name="malId">The MyAnimeList id.</param>
    /// <param name="episode">The episode number within that MAL entry.</param>
    /// <param name="episodeLengthSeconds">The episode runtime in seconds.</param>
    /// <param name="types">The AniSkip categories to request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching skip intervals; empty when none are found.</returns>
    public async Task<IReadOnlyList<AniSkipInterval>> GetSkipTimesAsync(
        int malId,
        int episode,
        double episodeLengthSeconds,
        IReadOnlyCollection<string> types,
        CancellationToken cancellationToken)
    {
        if (types.Count == 0)
        {
            return Array.Empty<AniSkipInterval>();
        }

        var query = string.Join("&", types.Select(t => "types=" + t));
        var length = episodeLengthSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var url = $"{ApiBase}/skip-times/{malId}/{episode}?{query}&episodeLength={length}";

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = no crowd data for this episode; anything else is transient.
                return Array.Empty<AniSkipInterval>();
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("found", out var found)
                || found.ValueKind != JsonValueKind.True)
            {
                return Array.Empty<AniSkipInterval>();
            }

            if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<AniSkipInterval>();
            }

            var list = new List<AniSkipInterval>();
            foreach (var result in results.EnumerateArray())
            {
                if (!result.TryGetProperty("skipType", out var skipTypeEl)
                    || !result.TryGetProperty("interval", out var interval)
                    || !interval.TryGetProperty("startTime", out var startEl)
                    || !interval.TryGetProperty("endTime", out var endEl))
                {
                    continue;
                }

                var skipType = skipTypeEl.GetString();
                if (string.IsNullOrEmpty(skipType))
                {
                    continue;
                }

                list.Add(new AniSkipInterval(skipType, startEl.GetDouble(), endEl.GetDouble()));
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AniSkip query failed for MAL {MalId} episode {Episode}", malId, episode);
            return Array.Empty<AniSkipInterval>();
        }
        finally
        {
            _gate.Release();
        }
    }
}
