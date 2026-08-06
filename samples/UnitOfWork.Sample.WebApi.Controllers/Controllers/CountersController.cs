using Microsoft.AspNetCore.Mvc;
using UnitOfWork.Sample.WebApi.Contracts;
using UnitOfWork.Sample.WebApi.Models;
using UnitOfWork.Sample.WebApi.Services;

namespace UnitOfWork.Sample.WebApi.Controllers;

[ApiController]
[Route("api/counters")]
public sealed class CountersController : ControllerBase
{
    private readonly CounterApplicationService _applicationService;

    public CountersController(CounterApplicationService applicationService)
    {
        _applicationService = applicationService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<CounterItem>>> GetAsync(
        CancellationToken cancellationToken)
    {
        var items = await _applicationService.ListAsync(cancellationToken);
        return Ok(items);
    }

    [HttpPost("commit")]
    public async Task<ActionResult<TransactionScenarioResponse>> CommitAsync(
        [FromBody] CreateCounterPairRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _applicationService.CommitPairAsync(
            request,
            cancellationToken);
        return Ok(response);
    }

    [HttpPost("rollback")]
    public async Task<ActionResult<TransactionScenarioResponse>> RollbackAsync(
        [FromBody] CreateCounterPairRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _applicationService.RollbackPairAsync(
            request,
            cancellationToken);
        return Ok(response);
    }

    [HttpGet("diagnostics")]
    public async Task<ActionResult<DiagnosticsResponse>> GetDiagnosticsAsync(
        CancellationToken cancellationToken)
    {
        var response = await _applicationService.GetDiagnosticsAsync(
            cancellationToken);
        return Ok(response);
    }
}
