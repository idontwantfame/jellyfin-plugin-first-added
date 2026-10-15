using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace Jellyfin.Plugin.FirstAdded;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("FirstAdded")]
public sealed class FirstAddedController : ControllerBase
{
    private readonly FirstAddedService _service;
    public FirstAddedController(FirstAddedService service) => _service = service;
    [HttpGet("Status")]
    public Task<object> Status(CancellationToken ct) => _service.StatusAsync(ct);
    [HttpGet("Preview")]
    public Task<List<DatePreview>> Preview(CancellationToken ct) => _service.PreviewAsync(ct);
    [HttpPost("Initialize")]
    public Task<RunResult> Initialize(CancellationToken ct) => _service.InitializeAsync(ct);
    [HttpPost("Reconcile")]
    public Task<RunResult> Reconcile(CancellationToken ct) => _service.ReconcileAsync(ct);
    [HttpPost("Rollback")]
    public Task<RunResult> Rollback(CancellationToken ct) => _service.RollbackAsync(ct);
}
