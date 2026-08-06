using UnitOfWork.Sample.ConsoleApp;

try
{
    var summary = await SampleApplication.RunAsync(Console.Out);
    return summary.AllPassed ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
