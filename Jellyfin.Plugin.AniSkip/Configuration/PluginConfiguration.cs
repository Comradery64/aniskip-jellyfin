using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AniSkip.Configuration;

/// <summary>
/// AniSkip plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether openings (AniSkip "op") are published as Intro segments.
    /// </summary>
    public bool EnableIntro { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether endings (AniSkip "ed") are published as Outro segments.
    /// </summary>
    public bool EnableOutro { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether recaps (AniSkip "recap") are published as Recap segments.
    /// </summary>
    public bool EnableRecap { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether mixed op/ed (AniSkip "mixed-op"/"mixed-ed"), where the
    /// opening/ending is fused with story content, are published (as Intro/Outro respectively).
    /// </summary>
    public bool EnableMixed { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the TheTVDB series id + season number is mapped to
    /// the correct MAL entry via the Fribb anime-lists dataset. This is what makes multi-season
    /// (multi-cour) shows resolve to the right AniSkip entry. On by default.
    /// </summary>
    public bool EnableTvdbMapping { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a series-level MAL id may be applied to seasons &gt; 1.
    /// Off by default: multi-cour anime map each season to a distinct MAL entry, so applying a
    /// season 1 MAL id to later seasons would place skips at the wrong timestamps. Only used as a
    /// fallback when the TVDB mapping does not cover the show.
    /// </summary>
    public bool AssumeSingleCour { get; set; }

    /// <summary>
    /// Gets or sets the minimum episode runtime (seconds) to query. Guards against shorts/specials.
    /// </summary>
    public int MinEpisodeSeconds { get; set; } = 60;
}
