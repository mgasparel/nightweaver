using Nightweaver.Inspection;
using Nightweaver.Application;

namespace Nightweaver;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var shutdownSignals = new RuntimeShutdownSignals(gracefulStopAccepted: static () =>
        {
            Console.Error.WriteLine("nightweaver: graceful stop accepted; draining current turns.");
            Console.Error.Flush();
        });
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdownSignals.RequestStop();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            var environment = new AppEnvironment(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Environment.CurrentDirectory,
                Console.Out,
                Console.Error);

            return await NightweaverApp.RunAsync(args, environment, new SystemProcessRunner(),
                shutdownSignals, shutdownSignals.FirstStopToken);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

}
