using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace FinTubeX;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static readonly Guid PluginId = Guid.Parse("a8c90302-3b12-4c28-98e1-5bf3d33f3e1a");

    public override string Name => "FinTubeX";
    public override Guid Id => PluginId;
    public override string Description => "A front end for yt-dlp: search YouTube or paste a link and download it into your library.";

    /// <summary>Writable scratch space for yt-dlp / deno caches (the container user may have no HOME).</summary>
    public string CacheDir { get; }

    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        CacheDir = Path.Combine(applicationPaths.CachePath, "fintubex");
        Directory.CreateDirectory(CacheDir);
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "FinTubeX",
            DisplayName = "YouTube",
            EmbeddedResourcePath = GetType().Namespace + ".Web.FinTubeX.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "smart_display"
        };
    }
}

public class PluginConfiguration : BasePluginConfiguration
{
    // These defaults assume the docker-compose layout from the setup notes.
    public string DownloadPath { get; set; } = "/media/library/YouTube";
    public string YtDlpPath { get; set; } = "/usr/bin/yt-dlp";
    public string DenoPath { get; set; } = "/usr/bin/deno";
    public string FfmpegPath { get; set; } = "/usr/bin/ffmpeg";
    public string RemoteComponents { get; set; } = "ejs:github";
    public string DefaultQuality { get; set; } = "720";
}
