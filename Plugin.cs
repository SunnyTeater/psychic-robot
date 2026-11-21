using System;
using System.Collections.Generic;
using Jellyfin.Plugin.NamingStandardizer.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.NamingStandardizer;

/// <summary>
/// Bringt Dateinamen von Filmen und Folgen auf Jellyfins Benennungsstandard, damit Versionen erkannt werden.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Pfade des Servers.</param>
    /// <param name="xmlSerializer">XML-Serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets die laufende Instanz.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Naming Standardizer";

    /// <inheritdoc />
    public override string Description => "Bringt Dateinamen von Filmen und Folgen auf Jellyfins Standard, damit Versionen erkannt werden.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("2c6cce90-832f-4654-a0ac-44f1449f3039");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "NamingStandardizer",
                DisplayName = "Naming Standardizer",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true,
                MenuIcon = "drive_file_rename_outline"
            }
        };
    }
}
