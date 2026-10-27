using Nightweaver.Configuration;
using Nightweaver;
using Nightweaver.Inspection;
using System.Text.Json;
using System.Net.Http.Headers;

namespace Nightweaver.Web;

public sealed class Program
{
    private static readonly Action<ILogger, Exception?> LogBeadsExplorerFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, "BeadsExplorerFailure"), "Beads Explorer query failed.");
    private static readonly Action<ILogger, Exception?> LogProductBeadCreationFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(2, "ProductBeadCreationFailure"), "Product Bead creation failed.");
    private static readonly Action<ILogger, Exception?> LogAttentionDeskFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(3, "AttentionDeskFailure"), "Human Attention Desk query failed.");
    private static readonly Action<ILogger, string, string, Exception?> LogAttentionActionFailure = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(4, "AttentionActionFailure"),
        "Attention Desk action {Action} failed for Bead {BeadId}.");
    private static readonly Action<ILogger, string, Exception?> LogAttentionActionError = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(5, "AttentionActionError"), "Attention Desk action failed for Bead {BeadId}.");

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
        builder.Services.AddSingleton<BeadsWriter>();
        builder.Services.AddSingleton<TrackerBridge>();
        builder.Services.AddSingleton<LocalMutationRequestPolicy>();

        var app = builder.Build();
        // Resolve before listening, after all host configuration providers have been applied.
        _ = app.Services.GetRequiredService<ProjectConfig>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/api/beads", async (HttpContext context, TrackerBridge explorer, ILogger<Program> logger) =>
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
        app.MapGet("/api/attention", async (HttpContext context, TrackerBridge tracker, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk query", detail: "Query parameters are not supported.");
            try
            {
                return Results.Ok(await tracker.ReadAttentionAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedTrackerFailure(exception))
            {
                LogAttentionDeskFailure(logger, exception);
                var (title, detail) = GetSafeFailure(exception);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: title, detail: detail);
            }
        });
        app.MapPost("/api/beads/{id}/decide", async (HttpContext context, string id, TrackerBridge tracker,
            LocalMutationRequestPolicy policy, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: "Query parameters are not supported.");
            if (!policy.Allows(context.Request))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Request origin rejected", detail: "Use the local Nightweaver console to submit this action.");
            if (!IsJsonContentType(context.Request.ContentType))
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "Unsupported media type", detail: "Submit the decision as application/json.");
            var body = await BoundedRequestBody.ReadAsync(context.Request, context.RequestAborted, 32 * 1024);
            if (body is null)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Request body too large", detail: "The request body cannot exceed 32 KiB.");
            DecideAttentionRequest request;
            try
            {
                request = AttentionMutationRequest.ParseDecide(body);
            }
            catch (AttentionRequestValidationException exception)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: exception.Message);
            }
            return await ExecuteAttentionActionAsync(context, id,
                () => tracker.DecideAsync(id, request.Instruction, request.Action, context.RequestAborted), logger);
        });
        app.MapPost("/api/beads/{id}/retry", async (HttpContext context, string id, TrackerBridge tracker,
            LocalMutationRequestPolicy policy, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: "Query parameters are not supported.");
            if (!policy.Allows(context.Request))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Request origin rejected", detail: "Use the local Nightweaver console to submit this action.");
            var body = await ReadEmptyActionBodyAsync(context.Request, context.RequestAborted);
            if (body.Error is not null) return body.Error;
            try
            {
                AttentionMutationRequest.ParseEmptyAction(body.Bytes!);
            }
            catch (AttentionRequestValidationException exception)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: exception.Message);
            }
            return await ExecuteAttentionActionAsync(context, id,
                () => tracker.RetryAsync(id, context.RequestAborted), logger);
        });
        app.MapPost("/api/beads/{id}/abort", async (HttpContext context, string id, TrackerBridge tracker,
            LocalMutationRequestPolicy policy, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: "Query parameters are not supported.");
            if (!policy.Allows(context.Request))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Request origin rejected", detail: "Use the local Nightweaver console to submit this action.");
            var body = await ReadEmptyActionBodyAsync(context.Request, context.RequestAborted);
            if (body.Error is not null) return body.Error;
            try
            {
                AttentionMutationRequest.ParseEmptyAction(body.Bytes!);
            }
            catch (AttentionRequestValidationException exception)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Attention Desk request", detail: exception.Message);
            }
            return await ExecuteAttentionActionAsync(context, id,
                () => tracker.AbortAsync(id, context.RequestAborted), logger);
        });
        app.MapPost("/api/beads", async (HttpContext context, TrackerBridge tracker, ILogger<Program> logger) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Query.Count != 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Product Bead request", detail: "Query parameters are not supported.");
            if (!IsAllowedMutationOrigin(context.Request))
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden,
                    title: "Request origin rejected", detail: "This request must come from the same origin as the Nightweaver console.");
            if (!IsJsonContentType(context.Request.ContentType))
                return Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "Unsupported media type", detail: "Submit the Product Bead as application/json.");

            var body = await BoundedRequestBody.ReadAsync(context.Request, context.RequestAborted);
            if (body is null)
                return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Request body too large", detail: "The request body cannot exceed 64 KiB.");

            var parsed = ProductBeadIntakeRequest.Parse(body);
            if (parsed.Errors.Count > 0)
                return Results.ValidationProblem(parsed.Errors, statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid Product Bead request");

            try
            {
                var created = await tracker.CreateProductBeadAsync(parsed.Submission!, context.RequestAborted);
                return Results.Json(created, statusCode: StatusCodes.Status201Created);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (ProductBeadCreationException exception)
            {
                LogProductBeadCreationFailure(logger, exception);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Creation could not be confirmed",
                    detail: "The request may have created a Product Bead. Check the backlog before resubmitting.");
            }
            catch (Exception exception) when (exception is ToolExecutionException or TargetRepositoryException
                or ProjectConfigurationException or JsonException or IOException or UnauthorizedAccessException)
            {
                LogProductBeadCreationFailure(logger, exception);
                var detail = exception is JsonException || exception.Message.Contains("incompatible or malformed", StringComparison.OrdinalIgnoreCase)
                    ? "The bd command returned data that Nightweaver could not read."
                    : "The configured Beads tracker could not create the Product Bead safely. Check that bd is installed and the tracker is prepared.";
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: exception is JsonException ? "Incompatible Beads output" : "Beads tracker unavailable",
                    detail: detail);
            }
        });
        app.Run();
    }

    private static bool IsJsonContentType(string? contentType)
        => MediaTypeHeaderValue.TryParse(contentType, out var parsed)
            && string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<(byte[]? Bytes, IResult? Error)> ReadEmptyActionBodyAsync(HttpRequest request,
        CancellationToken cancellationToken)
    {
        var bodyPresent = request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0;
        if (!bodyPresent) return (Array.Empty<byte>(), null);
        if (!IsJsonContentType(request.ContentType))
            return (null, Results.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported media type", detail: "Retry and abort bodies must use application/json."));
        var body = await BoundedRequestBody.ReadAsync(request, cancellationToken, 32 * 1024);
        return body is null
            ? (null, Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Request body too large", detail: "The request body cannot exceed 32 KiB."))
            : (body, null);
    }

    private static async Task<IResult> ExecuteAttentionActionAsync(HttpContext context, string beadId,
        Func<Task<AttentionMutationResult>> execute, ILogger<Program> logger)
    {
        try
        {
            return Results.Ok(await execute());
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (AttentionActionException exception)
        {
            if (exception.InnerException is not null)
                LogAttentionActionFailure(logger, context.Request.Method, beadId, exception.InnerException);
            var extensions = new Dictionary<string, object?>();
            if (exception.IncludeProgress)
            {
                extensions["commentRecorded"] = exception.CommentRecorded;
                extensions["stateChanged"] = exception.StateChanged;
            }
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Title,
                detail: exception.Detail, extensions: extensions);
        }
        catch (BeadsGuardConflictException exception)
        {
            LogAttentionActionFailure(logger, context.Request.Method, beadId, exception);
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Bead changed", detail: "The workflow changed before the action could be confirmed. Refresh the Attention Desk before retrying.");
        }
        catch (Exception exception) when (IsExpectedTrackerFailure(exception))
        {
            LogAttentionActionError(logger, beadId, exception);
            var (title, detail) = GetSafeFailure(exception);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: title, detail: detail);
        }
    }

    private static bool IsExpectedTrackerFailure(Exception exception)
        => exception is ToolExecutionException or TargetRepositoryException or ProjectConfigurationException
            or BeadsGuardConflictException or JsonException or IOException or UnauthorizedAccessException;

    private static bool IsAllowedMutationOrigin(HttpRequest request)
    {
        if (request.Headers["Sec-Fetch-Site"].Any(value =>
            string.Equals(value, "cross-site", StringComparison.OrdinalIgnoreCase)))
            return false;

        if (!request.Headers.TryGetValue("Origin", out var origins))
            return true;
        if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin)
            || !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return false;

        if (!Uri.TryCreate($"{request.Scheme}://{request.Host}/", UriKind.Absolute, out var requestOrigin))
            return false;
        return string.Equals(origin.Scheme, requestOrigin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.IdnHost, requestOrigin.IdnHost, StringComparison.OrdinalIgnoreCase)
            && origin.Port == requestOrigin.Port;
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
