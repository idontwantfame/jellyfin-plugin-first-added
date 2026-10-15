using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FirstAdded.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; }
    public bool IncludeMovies { get; set; } = true;
    public bool IncludeEpisodes { get; set; } = true;
    public bool RespectManualDateEdits { get; set; } = true;
    public List<string> LibraryIds { get; set; } = new();
}
