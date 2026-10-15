using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace Jellyfin.Plugin.FirstAdded;

public sealed class LibraryEvents : BackgroundService
{
    private sealed record Change(Guid Id, DateTime Date, ItemUpdateType Reason);
    private readonly Channel<Change> _changes = Channel.CreateUnbounded<Change>(new() { SingleReader = true });
    private readonly ILibraryManager _library;
    private readonly FirstAddedService _service;
    private readonly ILogger<LibraryEvents> _logger;
    public LibraryEvents(ILibraryManager library, FirstAddedService service, ILogger<LibraryEvents> logger)
    { _library = library; _service = service; _logger = logger; }
    public override Task StartAsync(CancellationToken ct)
    {
        _library.ItemAdded += Added;
        _library.ItemUpdated += Updated;
        return base.StartAsync(ct);
    }
    private void Added(object? sender, ItemChangeEventArgs args) => Enqueue(args, ItemUpdateType.None);
    private void Updated(object? sender, ItemChangeEventArgs args) => Enqueue(args, args.UpdateReason);
    private void Enqueue(ItemChangeEventArgs args, ItemUpdateType reason)
    {
        if (args.Item is not MediaBrowser.Controller.Entities.Movies.Movie
            && args.Item is not MediaBrowser.Controller.Entities.TV.Episode) return;
        var date = IdentityBuilder.Utc(args.Item.DateCreated);
        _service.CaptureEdit(args.Item.Id, date, reason);
        _changes.Writer.TryWrite(new(args.Item.Id, date, reason));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var change in _changes.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await _service.ProcessEventAsync(change.Id, change.Date, change.Reason, stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "First Added could not process a library event; preservation will retry on the next run."); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
    public override async Task StopAsync(CancellationToken ct)
    {
        _library.ItemAdded -= Added;
        _library.ItemUpdated -= Updated;
        _changes.Writer.TryComplete();
        await base.StopAsync(ct).ConfigureAwait(false);
    }
    public override void Dispose()
    {
        _library.ItemAdded -= Added;
        _library.ItemUpdated -= Updated;
        base.Dispose();
    }
}
