using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.FirstAdded;
using Jellyfin.Plugin.FirstAdded.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;

BaseItem.ConfigurationManager = Stub<IServerConfigurationManager>.Create((m, a) =>
    m.Name == "get_Configuration" ? new ServerConfiguration() : null);
var tests = new List<(string, Func<Task>)>();
var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { checks++; return; }
    throw new Exception("Expected " + typeof(T).Name);
}
var old = new DateTime(2024, 2, 3, 12, 34, 56, DateTimeKind.Utc);
var recent = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);

tests.Add(("Initialization, preview and disabled mode never write metadata", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("101", old);
    var preview = await f.Service.PreviewAsync(default);
    Check(preview.Single().Action == "Initialize first", "Uninitialized scopes must be visible");
    Check(!File.Exists(f.LedgerPath), "Preview must not create a ledger");
    Check((await f.Service.ReconcileAsync(default)).Restored == 0, "Uninitialized reconcile must do nothing");
    await f.Service.InitializeAsync(default);
    Check(movie.Writes == 0 && movie.DateCreated == old, "Initialization must be metadata read only");
    movie.DateCreated = recent; f.Config.Enabled = false;
    await f.Service.ReconcileAsync(default);
    Check(movie.Writes == 0 && movie.DateCreated == recent, "Disabled mode must not change dates");
}
));
tests.Add(("A replacement movie inherits its date; a new movie remains new", async () =>
{
    using var f = new Fixture(); var original = f.Movie("102", old);
    await f.Service.InitializeAsync(default); f.Items.Remove(original);
    var replacement = f.Movie("102", recent); replacement.Path = "/media/renamed-upgrade.mkv";
    var fresh = f.Movie("103", recent); replacement.Overview = "Keep overview"; replacement.ForcedSortName = "Keep sort";
    await f.Service.ReconcileAsync(default);
    Check(replacement.DateCreated == old && replacement.Writes == 1, "Replacement must retain first date");
    Check(fresh.DateCreated == recent && fresh.Writes == 0, "New title must stay recent");
    Check(replacement.Overview == "Keep overview" && replacement.ForcedSortName == "Keep sort", "Other metadata must stay intact");
    var saves = f.Store.Saves; await f.Service.ReconcileAsync(default);
    Check(replacement.Writes == 1 && f.Store.Saves == saves, "Repeated runs must avoid redundant writes");
    var backup = f.Store.Load().Writes[replacement.Id.ToString("N")];
    Check(backup.OriginalDateUtc == recent && backup.AppliedDateUtc == old && !backup.PendingWrite, "Backup must contain both dates");
}
));
tests.Add(("Same-ID refreshes restore dates and learn compatible aliases", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("104", old); await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; movie.SetProviderId(MetadataProvider.Imdb, "tt0000104");
    Check(await f.Event(movie, ItemUpdateType.MetadataImport) == "Restored", "Metadata import must repair same-ID date");
    f.Restart(); Check(f.Store.Load().Records.Single().Value.Keys.Contains("movie:imdb=tt0000104"), "New aliases must survive restart");
    var newFile = f.Movie(null, recent); newFile.SetProviderId(MetadataProvider.Imdb, "tt0000104");
    await f.Service.ReconcileAsync(default);
    Check(newFile.DateCreated == old, "A learned alias alone must match later replacements");
}
));
tests.Add(("Episodes match series and numbering, including specials", async () =>
{
    using var f = new Fixture(); var series = f.Series("201");
    var first = f.Episode(series, 1, 1, old); var special = f.Episode(series, 0, 1, old);
    await f.Service.InitializeAsync(default); f.Items.Remove(first); f.Items.Remove(special);
    var upgrade = f.Episode(series, 1, 1, recent); var upgradedSpecial = f.Episode(series, 0, 1, recent);
    var next = f.Episode(series, 1, 2, recent); var anotherSeries = f.Series("202");
    var unrelated = f.Episode(anotherSeries, 1, 1, recent);
    await f.Service.ReconcileAsync(default);
    Check(upgrade.DateCreated == old && upgradedSpecial.DateCreated == old, "Episode and special upgrades must retain dates");
    Check(next.DateCreated == recent && unrelated.DateCreated == recent, "New episodes and other series must stay new");
}
));
tests.Add(("Unidentified, virtual, combined and invalid items are skipped", async () =>
{
    using var f = new Fixture(); var noId = f.Movie(null, old); var virtualMovie = f.Movie("301", old); virtualMovie.IsVirtualItem = true;
    var noPath = f.Movie("302", old); noPath.Path = "";
    var invalidDate = f.Movie("303", DateTime.MinValue); var series = f.Series("304");
    var combined = f.Episode(series, 1, 1, old); combined.IndexNumberEnd = 2;
    var unknownNumber = f.Episode(series, 1, 2, old); unknownNumber.ParentIndexNumber = null;
    await f.Service.InitializeAsync(default); await f.Service.ReconcileAsync(default);
    Check(f.Store.Load().Records.Count == 0, "Unreliable identities must not enter the ledger");
    Check(new[] { noId, virtualMovie, noPath, invalidDate }.All(m => m.Writes == 0) && combined.Writes == 0 && unknownNumber.Writes == 0,
        "Skipped items must never be written");
}
));
tests.Add(("Provider conflicts cannot combine records", async () =>
{
    using var f = new Fixture(); var a = f.Movie("401", old); a.SetProviderId(MetadataProvider.Imdb, "tt0000401");
    var b = f.Movie("402", recent); b.SetProviderId(MetadataProvider.Imdb, "tt0000402");
    await f.Service.InitializeAsync(default);
    a.DateCreated = recent; a.SetProviderId(MetadataProvider.Imdb, "tt0000402");
    Check(await f.Event(a) == "Identity conflict" && a.Writes == 0, "Cross-record aliases must fail closed");
    a.SetProviderId(MetadataProvider.Imdb, "tt0000999");
    Check(await f.Event(a) == "Identity conflict", "Conflicting new ID must also fail closed");
    a.ProviderIds.Clear(); a.SetProviderId(MetadataProvider.Tmdb, "999");
    Check(await f.Event(a) == "Identity conflict", "Reidentified same-ID item must not inherit unrelated history");
    var series = f.Series("403"); var ep = f.Episode(series, 1, 1, old); ep.SetProviderId(MetadataProvider.Tmdb, "404");
    await f.Service.InitializeAsync(default); ep.IndexNumber = 2; ep.DateCreated = recent;
    Check(await f.Event(ep) == "Identity conflict", "Changed episode numbering must not alias via a stale direct ID");
}
));
tests.Add(("Duplicate versions seed the earliest current date only once", async () =>
{
    using var f = new Fixture(); var later = f.Movie("501", recent); f.Movie("501", old);
    await f.Service.InitializeAsync(default);
    Check(f.Store.Load().Records.Single().Value.FirstAddedUtc == old && later.Writes == 0, "Initialization must choose earliest existing version without writes");
    later.DateCreated = old.AddYears(-1); await f.Service.InitializeAsync(default);
    Check(f.Store.Load().Records.Single().Value.FirstAddedUtc == old, "Repeated initialization must not rewrite stored history");
}
));
tests.Add(("Explicit library and item-type scopes gate all writes", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("601", old); var ep = f.Episode(f.Series("602"), 1, 1, old);
    f.Config.IncludeEpisodes = false; await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; ep.DateCreated = recent; f.Config.LibraryIds.Clear();
    await f.Service.ReconcileAsync(default); Check(movie.Writes == 0 && ep.Writes == 0, "Deselected libraries must be ignored");
    f.Config.LibraryIds.Add(f.Folder.Id.ToString("N")); f.Config.IncludeEpisodes = true;
    await f.Service.ReconcileAsync(default);
    Check(movie.DateCreated == old && ep.Writes == 0, "A newly selected item type must require initialization");
}
));
tests.Add(("Manual date edits become the baseline; unrelated metadata edits do not", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("701", old); await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; await f.Event(movie); Check(movie.DateCreated == old, "Upgrade should restore first");
    var manual = old.AddDays(5); movie.DateCreated = manual;
    Check(await f.Event(movie, ItemUpdateType.MetadataEdit) == "Manual date retained", "Manual date must be preserved");
    Check(f.Store.Load().Records.Single().Value.FirstAddedUtc == manual, "Manual date must become future baseline");
    await f.Event(movie, ItemUpdateType.MetadataEdit); Check(movie.DateCreated == manual, "Unchanged date with metadata edit must remain stable");
    movie.DateCreated = recent; await f.Event(movie); Check(movie.DateCreated == manual, "Next upgrade must use manual baseline");
    await f.Service.RollbackAsync(default);
    Check(movie.DateCreated == recent, "Rollback after a subsequent upgrade must not resurrect a pre-manual backup");
}
));
tests.Add(("Captured manual edits survive a sweep racing the event worker", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("702", old); await f.Service.InitializeAsync(default);
    var manual = old.AddDays(7); movie.DateCreated = manual;
    f.Service.CaptureEdit(movie.Id, manual, ItemUpdateType.MetadataEdit);
    await f.Service.ReconcileAsync(default);
    Check(movie.DateCreated == manual && movie.Writes == 0, "Sweep must honor a queued manual edit");
    Check(await f.Service.ProcessEventAsync(movie.Id, recent, ItemUpdateType.MetadataImport, default) == "Stale event", "Stale event must not rewrite current state");
}
));
tests.Add(("Manual protection can be disabled; full metadata locks are always respected", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("703", old); await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; movie.IsLocked = true; await f.Service.ReconcileAsync(default);
    Check(movie.Writes == 0 && movie.DateCreated == recent, "Locked item must be skipped");
    movie.IsLocked = false; f.Config.RespectManualDateEdits = false;
    await f.Event(movie, ItemUpdateType.MetadataEdit); Check(movie.DateCreated == old, "Explicit enforcement must override manual changes");
}
));
tests.Add(("Ledger save failures occur before metadata writes and can be retried", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("801", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent;
    f.Store.FailNextSave = true; await Throws<IOException>(() => f.Event(movie));
    Check(movie.DateCreated == recent && movie.Writes == 0, "Failed journal persistence must prevent repository writes");
    await f.Event(movie); Check(movie.DateCreated == old && movie.Writes == 1, "Failed save must not poison in-memory retry state");
    var fresh = f.Movie("802", recent); f.Store.FailNextSave = true;
    await Throws<IOException>(() => f.Event(fresh)); await f.Event(fresh); f.Restart();
    Check(f.Store.Load().Records.Count == 2, "New-record save retry must persist history");
}
));
tests.Add(("Repository failure and restart keep a durable retryable journal", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("803", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent;
    movie.FailNextWrite = true; await Throws<IOException>(() => f.Event(movie));
    Check(f.Store.Load().Writes.Single().Value.PendingWrite, "Failed write must retain journal");
    f.Restart(); await f.Event(movie, ItemUpdateType.MetadataEdit);
    Check(movie.Writes == 1 && movie.DateCreated == old && !f.Store.Load().Writes.Single().Value.PendingWrite, "Retry must commit even if in-memory date already equals target");
}
));
tests.Add(("Manual edits supersede an interrupted write", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("805", old); await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; movie.FailNextWrite = true;
    await Throws<IOException>(() => f.Event(movie));
    var manual = old.AddDays(15); movie.DateCreated = manual;
    await f.Event(movie, ItemUpdateType.MetadataEdit);
    var backup = f.Store.Load().Writes.Single().Value;
    Check(backup.ManualOverride && !backup.PendingWrite, "A manual edit must cancel pending preservation");
    await f.Service.ReconcileAsync(default);
    Check(movie.DateCreated == manual && movie.Writes == 0, "Cancelled preservation must not retry over a manual baseline");
}
));
tests.Add(("An invalid rollback date cannot reach the repository", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("806", old); await f.Service.InitializeAsync(default);
    movie.DateCreated = recent; await f.Event(movie);
    var state = f.Store.Load(); state.Writes.Single().Value.OriginalDateUtc = DateTime.MinValue;
    f.Store.Save(state); f.Restart();
    await Throws<InvalidDataException>(() => f.Service.RollbackAsync(default));
    Check(movie.DateCreated == old && movie.Writes == 1, "Malformed recovery data must fail before touching metadata");
}
));
tests.Add(("A concurrent lock or date change after journal save cancels the write", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("804", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent;
    f.Store.AfterSave = () => { f.Store.AfterSave = null; movie.IsLocked = true; };
    Check(await f.Event(movie) == "Changed during write" && movie.Writes == 0, "Lock added while journaling must be respected");
    movie.IsLocked = false; var manual = old.AddDays(10);
    f.Store.AfterSave = () => { f.Store.AfterSave = null; movie.DateCreated = manual; };
    Check(await f.Event(movie) == "Changed during write" && movie.Writes == 0 && movie.DateCreated == manual, "Concurrent date change must not be overwritten");
}
));
tests.Add(("Rollback revokes scopes and skips manual edits and locks", async () =>
{
    using var f = new Fixture(); var a = f.Movie("901", old); var b = f.Movie("902", old); var c = f.Movie("903", old);
    await f.Service.InitializeAsync(default); a.DateCreated = b.DateCreated = c.DateCreated = recent; await f.Service.ReconcileAsync(default);
    b.IsLocked = true; c.DateCreated = old.AddDays(20); await f.Event(c, ItemUpdateType.MetadataEdit);
    var result = await f.Service.RollbackAsync(default);
    Check(result.Restored == 1 && a.DateCreated == recent && b.DateCreated == old && c.DateCreated == old.AddDays(20), "Rollback must restore only owned unlocked dates");
    Check(f.Store.Load().InitializedScopes.Count == 0, "Rollback must revoke all scopes durably");
    await f.Service.ReconcileAsync(default); Check(a.DateCreated == recent, "Task must not immediately reapply rolled-back dates");
    b.IsLocked = false; await f.Service.RollbackAsync(default); Check(b.DateCreated == recent, "Skipped lock must be retryable");
}
));
tests.Add(("Interrupted rollback remains retryable after restart", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("904", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent; await f.Event(movie);
    movie.FailNextWrite = true; await Throws<IOException>(() => f.Service.RollbackAsync(default));
    Check(f.Store.Load().Writes.Single().Value.PendingRollback && f.Store.Load().InitializedScopes.Count == 0, "Interrupted rollback must retain journal and revoked scopes");
    f.Restart(); await f.Service.RollbackAsync(default);
    Check(movie.DateCreated == recent && f.Store.Load().Writes.Count == 0, "Restart must finish interrupted rollback");
}
));
tests.Add(("Corrupt and conflicting ledgers fail closed", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("1001", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent;
    File.WriteAllText(f.LedgerPath, "{broken"); f.Restart();
    await Throws<JsonException>(() => f.Service.ReconcileAsync(default)); Check(movie.Writes == 0, "Corrupt state must not write metadata");
    var state = new LedgerState(); var a = new AddedRecord { Keys = new() { "movie:tmdb=1" }, FirstAddedUtc = old };
    var b = new AddedRecord { Keys = new() { "movie:tmdb=1" }, FirstAddedUtc = recent };
    state.Records.Add(a.Id, a); state.Records.Add(b.Id, b); f.Store.Save(state); f.Restart();
    await Throws<InvalidDataException>(() => f.Service.ReconcileAsync(default));
}
));
tests.Add(("Real store uses private permissions, atomic saves and a previous backup", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("1002", old); await f.Service.InitializeAsync(default); movie.DateCreated = recent; await f.Event(movie);
    Check(File.Exists(Path.Combine(f.Root, "first-added", "ledger.previous.json")), "Previous ledger backup must exist");
    Check(!Directory.EnumerateFiles(Path.Combine(f.Root, "first-added"), "*.tmp").Any(), "Temporary files must be cleaned up");
    if (!OperatingSystem.IsWindows()) Check((File.GetUnixFileMode(f.LedgerPath) & (UnixFileMode.GroupRead | UnixFileMode.OtherRead)) == 0, "Ledger must be private to service account");
}
));
tests.Add(("Hosted events route changes and unsubscribe at shutdown", async () =>
{
    using var f = new Fixture(); var movie = f.Movie("1101", old); await f.Service.InitializeAsync(default);
    using var host = new LibraryEvents(f.Library, f.Service, NullLogger<LibraryEvents>.Instance);
    await host.StartAsync(default); Check(f.Updated is not null && f.Added is not null, "Host must subscribe to both events");
    movie.DateCreated = recent; f.Updated!(f.Library, new() { Item = movie, UpdateReason = ItemUpdateType.MetadataImport });
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (movie.Writes == 0) await Task.Delay(10, timeout.Token);
    Check(movie.DateCreated == old, "Hosted event must apply restoration");
    await host.StopAsync(default); Check(f.Updated is null && f.Added is null, "Shutdown must unsubscribe handlers");
}
));
tests.Add(("Admin API and release DLL privacy", () =>
{
    var attribute = typeof(FirstAddedController).GetCustomAttribute<AuthorizeAttribute>();
    Check(attribute?.Policy == "RequiresElevation", "Every API endpoint must require administrator elevation");
    var dll = File.ReadAllBytes(typeof(Plugin).Assembly.Location);
    var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    Check(!Encoding.UTF8.GetString(dll).Contains(home, StringComparison.OrdinalIgnoreCase)
        && !Encoding.Unicode.GetString(dll).Contains(home, StringComparison.OrdinalIgnoreCase), "Release DLL must not embed local home path");
    return Task.CompletedTask;
}
));
foreach (var (name, test) in tests) { await test(); Console.WriteLine("PASS " + name); }
Console.WriteLine($"Passed {tests.Count} scenarios, {checks} assertions.");

public class Stub<T> : DispatchProxy where T : class
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    public static T Create(Func<MethodInfo, object?[]?, object?> handler)
    {
        var result = DispatchProxy.Create<T, Stub<T>>(); ((Stub<T>)(object)result).Handler = handler; return result;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public sealed class Fixture : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("first-added-test-").FullName;
    public string LedgerPath => Path.Combine(Root, "first-added", "ledger.json");
    public PluginConfiguration Config { get; } = new() { Enabled = true };
    public CollectionFolder Folder { get; } = new() { Id = Guid.NewGuid() };
    public List<BaseItem> Items { get; } = new();
    public ILibraryManager Library { get; }
    public TestStore Store { get; }
    public FirstAddedService Service { get; private set; }
    public EventHandler<ItemChangeEventArgs>? Added;
    public EventHandler<ItemChangeEventArgs>? Updated;
    public Fixture()
    {
        Config.LibraryIds.Add(Folder.Id.ToString("N"));
        Library = Stub<ILibraryManager>.Create((m, a) =>
        {
            switch (m.Name)
            {
                case "GetItemList": return Items.Where(i => i is MediaBrowser.Controller.Entities.Movies.Movie or MediaBrowser.Controller.Entities.TV.Episode).ToList();
                case "GetItemById": return Items.FirstOrDefault(i => i.Id == (Guid)a![0]!);
                case "GetCollectionFolders": return new List<Folder> { Folder };
                case "add_ItemAdded": Added += (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                case "remove_ItemAdded": Added -= (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                case "add_ItemUpdated": Updated += (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                case "remove_ItemUpdated": Updated -= (EventHandler<ItemChangeEventArgs>)a![0]!; return null;
                default: return null;
            }
        });
        var paths = Stub<IApplicationPaths>.Create((m, a) => m.Name.StartsWith("get_") ? Root : null);
        Store = new(new LedgerStore(paths)); Service = new(Library, Store, () => Config);
    }
    public void Restart() => Service = new(Library, Store, () => Config);
    public TestMovie Movie(string? provider, DateTime date)
    {
        var item = new TestMovie { Id = Guid.NewGuid(), Name = "Example movie", Path = "/media/movie.mkv", DateCreated = date };
        if (provider is not null) item.SetProviderId(MetadataProvider.Tmdb, provider); Items.Add(item); return item;
    }
    public Series Series(string provider)
    {
        var item = new Series { Id = Guid.NewGuid(), Name = "Example series" };
        item.SetProviderId(MetadataProvider.Tvdb, provider); Items.Add(item); return item;
    }
    public TestEpisode Episode(Series series, int season, int number, DateTime date)
    {
        var item = new TestEpisode
        {
            Id = Guid.NewGuid(),
            SeriesId = series.Id,
            ParentIndexNumber = season,
            IndexNumber = number,
            DateCreated = date,
            Name = "Example episode",
            Path = "/media/episode.mkv"
        };
        Items.Add(item); return item;
    }
    public Task<string> Event(BaseItem item, ItemUpdateType reason = ItemUpdateType.MetadataImport)
        => Service.ProcessEventAsync(item.Id, IdentityBuilder.Utc(item.DateCreated), reason, default);
    public void Dispose() => Directory.Delete(Root, recursive: true);
}
public sealed class TestStore(ILedgerStore inner) : ILedgerStore
{
    public int Saves { get; private set; }
    public bool FailNextSave { get; set; }
    public Action? AfterSave { get; set; }
    public LedgerState Load() => inner.Load();
    public void Save(LedgerState state)
    {
        if (FailNextSave) { FailNextSave = false; throw new IOException("Simulated ledger failure"); }
        inner.Save(state); Saves++; AfterSave?.Invoke();
    }
}
public sealed class TestMovie : Movie
{
    public int Writes { get; private set; }
    public bool FailNextWrite { get; set; }
    public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailNextWrite) { FailNextWrite = false; throw new IOException("Simulated repository failure"); }
        Writes++; return Task.CompletedTask;
    }
}
public sealed class TestEpisode : Episode
{
    public int Writes { get; private set; }
    public override Task UpdateToRepositoryAsync(ItemUpdateType reason, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Writes++; return Task.CompletedTask; }
}
