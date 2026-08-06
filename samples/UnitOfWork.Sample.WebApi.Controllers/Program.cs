using UnitOfWork.Core;
using UnitOfWork.Sample.WebApi.Infrastructure;
using UnitOfWork.Sample.WebApi.Repositories;
using UnitOfWork.Sample.WebApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddSingleton<SqliteSampleDatabase>();
builder.Services.AddSingleton<IDbConnectionFactory>(serviceProvider =>
    serviceProvider.GetRequiredService<SqliteSampleDatabase>());
builder.Services.AddSingleton<IUnitOfWorkManager>(serviceProvider =>
{
    var database = serviceProvider.GetRequiredService<SqliteSampleDatabase>();
    return new UnitOfWorkManager(database, (repositoryType, connection) =>
    {
        if (repositoryType == typeof(ICounterRepository))
            return new DapperCounterRepository(connection);

        throw new NotSupportedException(
            $"Repository is not registered: {repositoryType.FullName}");
    });
});
builder.Services.AddScoped<NestedCounterService>();
builder.Services.AddScoped<CounterApplicationService>();

var app = builder.Build();

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment() &&
    !app.Environment.IsEnvironment("Testing"))
{
    app.UseHttpsRedirection();
}

app.MapControllers();
app.Run();

public partial class Program
{
}
