using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.FirstAdded;

public interface ILedgerStore
{
    LedgerState Load();
    void Save(LedgerState state);
}

public sealed class LedgerStore : ILedgerStore
{
    private readonly string _directory;
    private readonly string _path;
    public LedgerStore(IApplicationPaths paths)
    {
        _directory = Path.Combine(paths.DataPath, "first-added");
        _path = Path.Combine(_directory, "ledger.json");
    }
    public LedgerState Load()
    {
        if (!File.Exists(_path)) return new();
        var state = JsonSerializer.Deserialize<LedgerState>(File.ReadAllBytes(_path))
            ?? throw new InvalidDataException("The First Added ledger is empty or invalid; restore a backup before continuing.");
        if (state.Version != 1) throw new InvalidDataException("Unsupported First Added ledger version.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, record) in state.Records)
        {
            if (id != record.Id || !Guid.TryParseExact(id, "N", out _) || !ValidDate(record.FirstAddedUtc) || record.Keys.Count == 0
                || record.Keys.Any(k => string.IsNullOrWhiteSpace(k) || k.IndexOf('=') <= 0
                    || k.IndexOf('=') != k.LastIndexOf('=') || k.EndsWith('=') || !keys.Add(k)))
                throw new InvalidDataException("Invalid or conflicting identity in the First Added ledger.");
        }
        if (state.Observations.Any(o => !Guid.TryParseExact(o.Key, "N", out _) || !state.Records.ContainsKey(o.Value.RecordId)
                || !ValidDate(o.Value.LastDateUtc))
            || state.Writes.Any(w => w.Key != w.Value.ItemId || !Guid.TryParse(w.Key, out _)
                || !state.Records.ContainsKey(w.Value.RecordId) || !ValidDate(w.Value.OriginalDateUtc)
                || !ValidDate(w.Value.AppliedDateUtc)
                || (w.Value.PreviousDateUtc.HasValue && !ValidDate(w.Value.PreviousDateUtc.Value))
                || (w.Value.PendingWrite && !w.Value.PreviousDateUtc.HasValue)))
            throw new InvalidDataException("Invalid First Added observation or recovery journal.");
        return state;
    }
    private static bool ValidDate(DateTime value) => value.Kind == DateTimeKind.Utc
        && value > DateTime.UnixEpoch && value <= DateTime.UtcNow.AddDays(1);
    public void Save(LedgerState state)
    {
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(_directory, "ledger." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(output, state);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) File.Copy(_path, Path.Combine(_directory, "ledger.previous.json"), overwrite: true);
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
