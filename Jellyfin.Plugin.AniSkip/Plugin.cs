using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AniSkip.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AniSkip;

/// <summary>
/// AniSkip plugin: sources anime opening/ending/recap timestamps from the AniSkip
/// database and publishes them through Jellyfin's Media Segments API.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "AniSkip";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b7e9c4a2-1f3d-4c8e-9a5b-6d2e0f1a3c7d");

    /// <inheritdoc />
    public override string Description =>
        "Crowd-sourced anime opening, ending and recap skip times from AniSkip, exposed as media segments.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            },
        };
    }
}
