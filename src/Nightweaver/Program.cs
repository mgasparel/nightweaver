using Nightweaver.Inspection;

namespace Nightweaver;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;
        try
        {
            var environment = new AppEnvironment(
                Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                Environment.CurrentDirectory,
                Console.Out,
                Console.Error);

            return await NightweaverApp.RunAsync(args, environment, new SystemProcessRunner(), cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
