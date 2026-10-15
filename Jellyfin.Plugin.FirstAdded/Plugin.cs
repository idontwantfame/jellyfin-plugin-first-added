using Jellyfin.Plugin.FirstAdded.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FirstAdded;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    internal static SemaphoreSlim MutationGate { get; } = new(1, 1);
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public override string Name => "First Added";
    public override Guid Id => Guid.Parse("c7d4af3e-1b9d-4f06-b0c8-cce9bc55ac91");
    public override string Description => "Preserve original date-added timestamps across movie and episode file replacements.";
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var incoming = (PluginConfiguration)configuration;
        if (incoming.LibraryIds.Any(id => !Guid.TryParse(id, out _)))
            throw new ArgumentException("Library IDs must be valid GUIDs.");
        incoming.LibraryIds = incoming.LibraryIds.Select(id => Guid.Parse(id).ToString("N"))
            .Distinct(StringComparer.Ordinal).ToList();
        MutationGate.Wait();
        try { base.UpdateConfiguration(incoming); }
        finally { MutationGate.Release(); }
    }
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new() { Name = Name, EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html" };
    }
}
