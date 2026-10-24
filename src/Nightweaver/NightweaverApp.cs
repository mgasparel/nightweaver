using Nightweaver.Application;
using Nightweaver.Cli;
using Nightweaver.Inspection;

namespace Nightweaver;

internal static class NightweaverApp
{
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        AppEnvironment environment,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = CommandLineParser.Parse(args);
            if (options.Help)
            {
                environment.Out.Write(HelpText.Usage);
                return ExitCodes.Success;
            }

            await DryRunApplication.RunAsync(options, environment, processRunner, cancellationToken);
            return ExitCodes.Success;
        }
        catch (UsageException exception)
        {
            environment.Err.WriteLine($"nightweaver: error: {exception.Message}");
            environment.Err.WriteLine("Run 'nightweaver --help' for usage.");
            return ExitCodes.UsageOrConfiguration;
        }
        catch (ProjectConfigurationException exception)
        {
            var parts = new List<string> { exception.ConfigFilePath };
            if (!string.IsNullOrEmpty(exception.JsonPath))
            {
                parts.Add(exception.JsonPath);
            }

            parts.Add(exception.Message);
            environment.Err.WriteLine($"nightweaver: error: {string.Join(": ", parts)}");
            return ExitCodes.UsageOrConfiguration;
        }
        catch (TargetRepositoryException exception)
        {
            environment.Err.WriteLine($"nightweaver: error: {exception.Message}");
            return ExitCodes.InspectionFailure;
        }
        catch (ToolExecutionException exception)
        {
            environment.Err.WriteLine($"nightweaver: error: {exception.Message}");
            WriteStandardErrorTail(environment.Err, exception.StandardError);
            return ExitCodes.InspectionFailure;
        }
        catch (OperationCanceledException)
        {
            environment.Err.WriteLine("nightweaver: error: cancelled");
            return ExitCodes.InspectionFailure;
        }
        catch (Exception exception)
        {
            environment.Err.WriteLine($"nightweaver: internal error: {exception.GetType().Name}: {exception.Message}");
            return ExitCodes.InspectionFailure;
        }
    }

    private static void WriteStandardErrorTail(TextWriter writer, string? standardError)
    {
        if (string.IsNullOrEmpty(standardError))
        {
            return;
        }

        var lines = standardError.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.TakeLast(20))
        {
            writer.WriteLine($"  {line}");
        }
    }
}
