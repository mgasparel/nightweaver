using Nightweaver.Configuration;
using Nightweaver;
using Nightweaver.Inspection;
using System.Text.Json;

namespace Nightweaver.Web;

public sealed class Program
{
    private static readonly Action<ILogger, Exception?> LogBeadsExplorerFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, "BeadsExplorerFailure"), "Beads Explorer query failed.");

    private Program() { }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        builder.Services.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            var root = configuration["Nightweaver:Root"] ?? AppContext.BaseDirectory;
            try
            {
                return new HostPaths(Path.GetFullPath(root));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException($"Invalid Nightweaver:Root '{root}': {exception.Message}", exception);
            }
        });
        builder.Services.AddSingleton(services =>
        {
            var configuration = services.GetRequiredService<IConfiguration>();
            var configPath = configuration["Nightweaver:ConfigPath"];
            if (string.IsNullOrWhiteSpace(configPath))
            {
                throw new InvalidOperationException(
                    "Nightweaver:ConfigPath is required. Supply an existing Project JSON file using --Nightweaver:ConfigPath or Nightweaver__ConfigPath.");
            }

            try
            {
                return ProjectConfigLoader.Load(configPath, expectedName: null, services.GetRequiredService<HostPaths>());
            }
            catch (ProjectConfigurationException exception)
            {
                var errors = string.Join("; ", exception.Errors.Select(error => $"{error.JsonPath}: {error.Message}"));
                throw new InvalidOperationException($"Invalid Nightweaver:ConfigPath '{exception.ConfigFilePath}': {errors}", exception);
            }
        });

        builder.Services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        builder.Services.AddSingleton<TargetRepositoryValidator>();
        builder.Services.AddSingleton<BeadsReader>();
        builder.Services.AddSingleton<BeadsExplorerService>();

        var app = builder.Build();
        // Resolve before listening, after all host configuration providers have been applied.
        _ = app.Services.GetRequiredService<ProjectConfig>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/beads", async (HttpContext context, BeadsExplorerService explorer, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = context.Request.Query;
            if (query.TryGetValue("view", out var viewValues) && viewValues.Count > 1)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Beads query", detail: "The view parameter may be supplied only once.");
            if (query.TryGetValue("q", out var searchValues) && searchValues.Count > 1)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Beads query", detail: "The q parameter may be supplied only once.");

            var rawView = query.TryGetValue("view", out viewValues) ? viewValues.ToString().Trim() : string.Empty;
            var view = string.IsNullOrEmpty(rawView) ? "all" : rawView.ToLowerInvariant();
            if (view is not ("all" or "active" or "ready" or "blocked" or "closed"))
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Beads query", detail: "Supported views are all, active, ready, blocked, and closed.");

            var rawSearch = query.TryGetValue("q", out searchValues) ? searchValues.ToString().Trim() : string.Empty;
            var search = string.IsNullOrEmpty(rawSearch) ? null : rawSearch;
            try
            {
                return Results.Ok(await explorer.ReadAsync(view, search, context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is ToolExecutionException or TargetRepositoryException
                or ProjectConfigurationException or JsonException or IOException or UnauthorizedAccessException)
            {
                LogBeadsExplorerFailure(logger, exception);
                var (title, detail) = GetSafeFailure(exception);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: title, detail: detail);
            }
        });
        app.Run();
    }

    private static (string Title, string Detail) GetSafeFailure(Exception exception)
    {
        var message = exception.Message;
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return ("Beads tracker timed out", "The tracker did not respond before the read timed out. Try again shortly.");
        if (exception is JsonException || message.Contains("incompatible or malformed", StringComparison.OrdinalIgnoreCase))
            return ("Incompatible Beads output", "The bd command returned data that Nightweaver could not read.");
        if (exception is TargetRepositoryException)
            return ("Beads tracker unavailable", "The configured target repository is unavailable or has no initialized Beads tracker.");
        if (exception is IOException or UnauthorizedAccessException)
            return ("Beads tracker unavailable", "A local read error prevented access to the configured tracker.");
        return ("Beads tracker unavailable", "The configured Beads tracker could not be read. Check that bd is installed and the tracker is prepared.");
    }
}
