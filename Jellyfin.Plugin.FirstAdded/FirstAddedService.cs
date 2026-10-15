using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FirstAdded.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.FirstAdded;

public sealed record DatePreview(string ItemId, string Name, DateTime CurrentDateUtc,
    DateTime? FirstAddedUtc, string Action);
public sealed record RunResult(int Recorded, int Restored, int Skipped);

/// <summary>Serializes settings, ledger changes and repository writes through the plugin gate.</summary>
public sealed class FirstAddedService
{
    private readonly ILibraryManager _library;
    private readonly ILedgerStore _store;
    private readonly Func<PluginConfiguration> _configuration;
    private LedgerState? _state;
    private readonly ConcurrentDictionary<Guid, DateTime> _edits = new();
    public void CaptureEdit(Guid id, DateTime date, ItemUpdateType reason)
    {
        if (reason.HasFlag(ItemUpdateType.MetadataEdit)) _edits[id] = date;
    }
    private Dictionary<string, AddedRecord> _index = new(StringComparer.Ordinal);

    public FirstAddedService(ILibraryManager library, ILedgerStore store)
        : this(library, store, () => Plugin.Instance!.Configuration) { }
    public FirstAddedService(ILibraryManager library, ILedgerStore store, Func<PluginConfiguration> configuration)
    { _library = library; _store = store; _configuration = configuration; }

    private LedgerState State
    {
        get
        {
            if (_state is null)
            {
                _state = _store.Load();
                _index = _state.Records.Values.SelectMany(r => r.Keys.Select(k => (k, r)))
                    .ToDictionary(x => x.k, x => x.r, StringComparer.Ordinal);
            }
            return _state;
        }
    }
    private async Task<T> Exclusive<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await Plugin.MutationGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await action().ConfigureAwait(false); }
        catch { _state = null; throw; } // A failed save is never mistaken for durable state.
        finally { Plugin.MutationGate.Release(); }
    }
    private IReadOnlyList<BaseItem> Items() => _library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
        Recursive = true,
        IsVirtualItem = false,
    });
    private List<string> Scopes(BaseItem item, ItemIdentity identity)
    {
        var config = _configuration();
        if ((identity.Kind == "Movie" && !config.IncludeMovies)
            || (identity.Kind == "Episode" && !config.IncludeEpisodes)) return new();
        return _library.GetCollectionFolders(item).Select(f => f.Id.ToString("N"))
            .Where(id => config.LibraryIds.Contains(id, StringComparer.Ordinal))
            .Select(id => id + ":" + identity.Kind).Distinct(StringComparer.Ordinal).ToList();
    }
    private (AddedRecord? Record, bool Conflict) Match(BaseItem item, ItemIdentity identity)
    {
        _ = State;
        var matches = identity.Keys.Where(_index.ContainsKey).Select(k => _index[k]).DistinctBy(r => r.Id).ToList();
        if (matches.Count > 1) return (null, true);
        var record = matches.SingleOrDefault();
        if (record is not null && !IdentityBuilder.Compatible(record.Keys, identity.Keys)) return (null, true);
        if (State.Observations.TryGetValue(item.Id.ToString("N"), out var prior)
            && (record is null || prior.RecordId != record.Id)) return (null, true);
        return (record, false);
    }
    private void AddKeys(AddedRecord record, ItemIdentity identity)
    {
        foreach (var key in identity.Keys.Where(k => !record.Keys.Contains(k, StringComparer.Ordinal)))
        { record.Keys.Add(key); _index.Add(key, record); }
    }
    private AddedRecord Add(ItemIdentity identity, DateTime date)
    {
        var record = new AddedRecord { FirstAddedUtc = date };
        State.Records.Add(record.Id, record);
        AddKeys(record, identity);
        return record;
    }
    private static bool ValidDate(DateTime date) => date > DateTime.UnixEpoch && date <= DateTime.UtcNow.AddDays(1);
    private static string Id(BaseItem item) => item.Id.ToString("N");
    private static DateTime Date(BaseItem item) => IdentityBuilder.Utc(item.DateCreated);
    private bool StillMatches(BaseItem item, ItemIdentity identity, DateTime observed)
    {
        var current = IdentityBuilder.Build(item, _library);
        return !item.IsLocked && Date(item) == observed && current is not null
            && current.Keys.SequenceEqual(identity.Keys, StringComparer.Ordinal);
    }

    public Task<object> StatusAsync(CancellationToken ct) => Exclusive<object>(() => Task.FromResult<object>(new
    {
        Libraries = _library.GetVirtualFolders().Select(f => new { Id = f.ItemId, f.Name }).ToList(),
        Records = State.Records.Count,
        Backups = State.Writes.Count,
        InitializedScopes = State.InitializedScopes.Order(StringComparer.Ordinal).ToList(),
        PendingWrites = State.Writes.Values.Count(w => w.PendingWrite || w.PendingRollback),
    }), ct);

    public Task<List<DatePreview>> PreviewAsync(CancellationToken ct) => Exclusive(() =>
    {
        var rows = new List<DatePreview>();
        foreach (var item in Items())
        {
            ct.ThrowIfCancellationRequested();
            var identity = IdentityBuilder.Build(item, _library);
            if (identity is null || Scopes(item, identity).Count == 0) continue;
            var (record, conflict) = Match(item, identity);
            var initialized = Scopes(item, identity).Any(State.InitializedScopes.Contains);
            var action = conflict ? "Identity conflict" : item.IsLocked ? "Locked" : !ValidDate(Date(item)) ? "Invalid date"
                : !initialized ? "Initialize first" : !_configuration().Enabled ? "Disabled"
                : State.Writes.TryGetValue(Id(item), out var write) && write.PendingRollback ? "Rollback pending"
                : record is null ? "Record new title" : Date(item) == record.FirstAddedUtc ? "Keep" : "Restore";
            rows.Add(new(Id(item), item.Name, Date(item), record?.FirstAddedUtc, action));
        }
        return Task.FromResult(rows);
    }, ct);

    /// <summary>Seeds current dates; does not write Jellyfin metadata. Existing baselines stay intact.</summary>
    public Task<RunResult> InitializeAsync(CancellationToken ct) => Exclusive(() =>
    {
        var recorded = 0; var skipped = 0;
        var created = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Items())
        {
            ct.ThrowIfCancellationRequested();
            var identity = IdentityBuilder.Build(item, _library);
            if (identity is null || Scopes(item, identity).Count == 0 || !ValidDate(Date(item))) { skipped++; continue; }
            var (record, conflict) = Match(item, identity);
            if (conflict) { skipped++; continue; }
            if (record is null) { record = Add(identity, Date(item)); created.Add(record.Id); recorded++; }
            else
            {
                AddKeys(record, identity);
                if (created.Contains(record.Id) && Date(item) < record.FirstAddedUtc) record.FirstAddedUtc = Date(item);
            }
            State.Observations[Id(item)] = new() { RecordId = record.Id, LastDateUtc = Date(item) };
        }
        foreach (var library in _configuration().LibraryIds)
        {
            if (_configuration().IncludeMovies) State.InitializedScopes.Add(library + ":Movie");
            if (_configuration().IncludeEpisodes) State.InitializedScopes.Add(library + ":Episode");
        }
        _store.Save(State);
        return Task.FromResult(new RunResult(recorded, 0, skipped));
    }, ct);

    public Task<RunResult> ReconcileAsync(CancellationToken ct) => Exclusive(async () =>
    {
        var recorded = 0; var restored = 0; var skipped = 0;
        if (!_configuration().Enabled) return new RunResult(0, 0, 0);
        foreach (var item in Items())
        {
            ct.ThrowIfCancellationRequested();
            var result = await Process(item, ItemUpdateType.None, null, ct).ConfigureAwait(false);
            if (result == "Recorded") recorded++; else if (result == "Restored") restored++; else skipped++;
        }
        return new RunResult(recorded, restored, skipped);
    }, ct);

    public Task<string> ProcessEventAsync(Guid itemId, DateTime capturedDate, ItemUpdateType reason, CancellationToken ct)
        => Exclusive(async () => _library.GetItemById(itemId) is { } item
            ? await Process(item, reason, capturedDate, ct).ConfigureAwait(false) : "Missing", ct);

    private async Task<string> Process(BaseItem item, ItemUpdateType reason, DateTime? capturedDate, CancellationToken ct)
    {
        if (!_configuration().Enabled || item.IsLocked) return "Disabled or locked";
        var date = Date(item);
        if (_edits.TryGetValue(item.Id, out var edited) && edited == date) reason |= ItemUpdateType.MetadataEdit;
        if (capturedDate.HasValue && date != capturedDate.Value) return "Stale event";
        var identity = IdentityBuilder.Build(item, _library);
        if (identity is null || !Scopes(item, identity).Any(State.InitializedScopes.Contains) || !ValidDate(date)) return "Out of scope";
        var (record, conflict) = Match(item, identity);
        if (conflict) return "Identity conflict";
        var id = Id(item);
        if (record is null)
        {
            record = Add(identity, date);
            State.Observations[id] = new() { RecordId = record.Id, LastDateUtc = date };
            _store.Save(State);
            return "Recorded";
        }
        State.Writes.TryGetValue(id, out var backup);
        if (backup?.PendingRollback == true) return "Rollback pending";
        if (_configuration().RespectManualDateEdits && reason.HasFlag(ItemUpdateType.MetadataEdit)
            && State.Observations.TryGetValue(id, out var observed) && observed.LastDateUtc != date
            && (backup is null || date != backup.AppliedDateUtc))
        {
            record.FirstAddedUtc = date;
            if (backup is not null)
            {
                backup.ManualOverride = true;
                backup.PendingWrite = false;
            }
            State.Observations[id] = new() { RecordId = record.Id, LastDateUtc = date };
            AddKeys(record, identity);
            _store.Save(State);
            return "Manual date retained";
        }
        var learnedKeys = identity.Keys.Any(k => !record.Keys.Contains(k, StringComparer.Ordinal));
        AddKeys(record, identity);
        if (date == record.FirstAddedUtc && backup?.PendingWrite != true)
        {
            // Persist newly learned provider aliases, even when no date needs changing.
            if (learnedKeys || !State.Observations.TryGetValue(id, out var existing)
                || existing.RecordId != record.Id || existing.LastDateUtc != date)
            {
                State.Observations[id] = new() { RecordId = record.Id, LastDateUtc = date };
                _store.Save(State);
            }
            return "Keep";
        }
        if (backup is null || backup.ManualOverride)
            backup = new() { ItemId = id, RecordId = record.Id, OriginalDateUtc = date };
        backup.PreviousDateUtc = date;
        backup.AppliedDateUtc = record.FirstAddedUtc;
        backup.PendingWrite = true;
        backup.ManualOverride = false;
        State.Writes[id] = backup;
        _store.Save(State); // Durable recovery journal BEFORE touching Jellyfin.
        if (!StillMatches(item, identity, date)) return "Changed during write";
        item.DateCreated = record.FirstAddedUtc;
        await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        backup.PendingWrite = false;
        State.Observations[id] = new() { RecordId = record.Id, LastDateUtc = backup.AppliedDateUtc };
        _store.Save(State);
        return "Restored";
    }

    /// <summary>Revokes all scopes before restoring dates. Failed or locked entries remain retryable.</summary>
    public Task<RunResult> RollbackAsync(CancellationToken ct) => Exclusive(async () =>
    {
        State.InitializedScopes.Clear();
        _store.Save(State); // No later event or task can reapply rolled-back dates.
        var restored = 0; var skipped = 0;
        foreach (var (id, backup) in State.Writes.ToList())
        {
            ct.ThrowIfCancellationRequested();
            if (_library.GetItemById(Guid.Parse(id)) is not { } item || item.IsLocked || backup.ManualOverride)
            { skipped++; continue; }
            var identity = IdentityBuilder.Build(item, _library);
            if (identity is null || Match(item, identity) is not { Conflict: false, Record: not null } match
                || match.Record.Id != backup.RecordId) { skipped++; continue; }
            var date = Date(item);
            if (date != backup.AppliedDateUtc
                && !(backup.PendingWrite && date == backup.PreviousDateUtc)
                && !(backup.PendingRollback && date == backup.OriginalDateUtc)) { skipped++; continue; }
            backup.PendingRollback = true;
            _store.Save(State);
            if (!StillMatches(item, identity, date)) { skipped++; continue; }
            item.DateCreated = backup.OriginalDateUtc;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            State.Writes.Remove(id);
            State.Observations[id] = new() { RecordId = backup.RecordId, LastDateUtc = backup.OriginalDateUtc };
            _store.Save(State);
            restored++;
        }
        return new RunResult(0, restored, skipped);
    }, ct);
}
