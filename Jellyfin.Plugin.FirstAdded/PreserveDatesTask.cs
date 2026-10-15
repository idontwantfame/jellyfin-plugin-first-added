using MediaBrowser.Model.Tasks;
namespace Jellyfin.Plugin.FirstAdded;

public sealed class PreserveDatesTask : IScheduledTask
{
    private readonly FirstAddedService _service;
    public PreserveDatesTask(FirstAddedService service) => _service = service;
    public string Name => "Preserve first-added dates";
    public string Key => "FirstAddedPreserve";
    public string Description => "Record new movies and episodes and restore original dates after file replacements.";
    public string Category => "First Added";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks };
    }
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        progress.Report(0);
        await _service.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
