using UnitOfWork.Sample.WebApi.MinimalApi.Contracts;
using UnitOfWork.Sample.WebApi.MinimalApi.Services;

namespace UnitOfWork.Sample.WebApi.MinimalApi.Endpoints;

public static class CounterEndpoints
{
    public static IEndpointRouteBuilder MapCounterEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/counters");

        group.MapGet(string.Empty, async (
            CounterApplicationService service,
            HttpContext context) =>
        {
            var items = await service.ListAsync(context.RequestAborted);
            return Results.Ok(items);
        });

        group.MapPost("/commit", async (
            CreateCounterPairRequest request,
            CounterApplicationService service,
            HttpContext context) =>
        {
            var response = await service.CommitPairAsync(
                request,
                context.RequestAborted);
            return Results.Ok(response);
        });

        group.MapPost("/rollback", async (
            CreateCounterPairRequest request,
            CounterApplicationService service,
            HttpContext context) =>
        {
            var response = await service.RollbackPairAsync(
                request,
                context.RequestAborted);
            return Results.Ok(response);
        });

        group.MapGet("/diagnostics", async (
            CounterApplicationService service,
            HttpContext context) =>
        {
            var response = await service.GetDiagnosticsAsync(
                context.RequestAborted);
            return Results.Ok(response);
        });

        return endpoints;
    }
}
