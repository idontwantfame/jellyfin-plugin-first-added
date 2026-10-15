namespace Jellyfin.Plugin.FirstAdded;

public sealed class AddedRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> Keys { get; set; } = new();
    public DateTime FirstAddedUtc { get; set; }
}
public sealed class ItemObservation
{
    public string RecordId { get; set; } = string.Empty;
    public DateTime LastDateUtc { get; set; }
}
public sealed class DateWrite
{
    public string ItemId { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public DateTime OriginalDateUtc { get; set; }
    public DateTime AppliedDateUtc { get; set; }
    public DateTime? PreviousDateUtc { get; set; }
    public bool PendingWrite { get; set; }
    public bool PendingRollback { get; set; }
    public bool ManualOverride { get; set; }
}
public sealed class LedgerState
{
    public int Version { get; set; } = 1;
    public HashSet<string> InitializedScopes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, AddedRecord> Records { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, ItemObservation> Observations { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateWrite> Writes { get; set; } = new(StringComparer.Ordinal);
}
