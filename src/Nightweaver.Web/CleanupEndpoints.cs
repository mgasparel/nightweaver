using System.Text.Json;
using Nightweaver.Configuration;
using Nightweaver.Execution;
using Nightweaver.Inspection;
using Nightweaver.Planning;

namespace Nightweaver.Web;

internal static class CleanupEndpoints
{
    internal static void MapCleanup(this WebApplication app)
    {
        app.MapGet("/api/cleanup", async (HttpContext context, HostPaths paths, ProjectConfig project,
            TargetRepositoryValidator validator) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var query = context.Request.Query;
            if (query.Any(item => item.Value.Count != 1 || item.Key is not ("page" or "status" or "q"))
                || query["q"].ToString().Length > 200
                || query.ContainsKey("page") && (!int.TryParse(query["page"], out var parsed) || parsed < 1)
                || query.ContainsKey("status") && query["status"].ToString() is not
                    ("pending" or "retry-scheduled" or "needs-attention" or "resolved"))
                return Problem(400, "Invalid cleanup query", "Use page, status and q (up to 200 characters).");
            try
            {
                var repository = await validator.ValidateAsync(project.TargetRepository, project.ConfigFilePath, context.RequestAborted);
                return Results.Ok(new CleanupStatusReader(paths, project, repository).Read(
                    query.ContainsKey("page") ? int.Parse(query["page"]!, System.Globalization.CultureInfo.InvariantCulture) : 1,
                    query.ContainsKey("status") ? query["status"].ToString() : null, query["q"].ToString()));
            }
            catch (Exception exception) when (IsUnavailable(exception))
            {
                return Problem(503, "Housekeeping unavailable", "Repository identity could not be validated. Refresh after repairing local access.");
            }
        });
        app.MapPost("/api/cleanup/{id}/retry", RetryAsync);
    }

    private static async Task<IResult> RetryAsync(HttpContext context, string id, HostPaths paths,
        ProjectConfig project, TargetRepositoryValidator validator, LocalMutationRequestPolicy policy, BeadsReader beads)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!policy.Allows(context.Request)) return Problem(403, "Request origin rejected", "Use the local Nightweaver console.");
        if (context.Request.Query.Count != 0 || !RuntimePaths.SafeId(id) || !WorktreeProvisioner.IsSafeBeadId(id))
            return Problem(400, "Invalid cleanup Retry", "Select a displayed cleanup obligation.");
        if (!context.Request.HasJsonContentType()) return Problem(415, "JSON required", "Submit application/json.");
        var bytes = await BoundedRequestBody.ReadAsync(context.Request, context.RequestAborted, 4096);
        if (bytes is null) return Problem(413, "Request too large", "Cleanup requests are limited to 4 KiB.");
        string runtimeId, revision;
        Guid requestId;
        try
        {
            using var body = JsonDocument.Parse(bytes);
            var fields = body.RootElement.EnumerateObject().ToArray();
            if (fields.Length != 3 || fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != 3
                || fields.Any(field => field.Name is not ("runtimeId" or "revision" or "requestId")))
                throw new JsonException();
            runtimeId = body.RootElement.GetProperty("runtimeId").GetString() ?? "";
            revision = body.RootElement.GetProperty("revision").GetString() ?? "";
            if (runtimeId.Length != 32 || !runtimeId.All(Uri.IsHexDigit)
                || revision.Length != 64 || !revision.All(Uri.IsHexDigit)
                || !Guid.TryParseExact(body.RootElement.GetProperty("requestId").GetString(), "N", out requestId))
                throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return Problem(400, "Invalid cleanup Retry", "Expected exactly runtimeId, revision and requestId from the displayed record.");
        }
        try
        {
            var repository = await validator.ValidateAsync(project.TargetRepository, project.ConfigFilePath, context.RequestAborted);
            var inventory = await beads.ReadSnapshotAsync(repository, context.RequestAborted);
            if (!inventory.ById.TryGetValue(id, out var bead)
                || !StartupEnvironmentClassifier.IsCompletedTmpOwner(bead, project.RecoveryActor))
                return Problem(409, "Refresh housekeeping before Retry", "The current Bead is missing, active, paused or foreign-owned. Resources are protected.");
            var request = await new CleanupRetryInbox(paths, project, repository).QueueAsync(runtimeId, id, revision,
                requestId, context.RequestAborted);
            return Results.Json(new { request, message = "Queued for owner inspection; cleanup is not yet confirmed." }, statusCode: 202);
        }
        catch (InvalidOperationException exception)
        {
            return Problem(409, "Refresh housekeeping before Retry", exception.Message);
        }
        catch (Exception exception) when (IsUnavailable(exception))
        {
            return Problem(503, "Cleanup Retry unavailable", "No cleanup was authorized by this response. Refresh and inspect local access and owner availability.");
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is IOException or UnauthorizedAccessException
        or JsonException or InvalidDataException or TargetRepositoryException or ToolExecutionException
        or ProjectConfigurationException or PlatformNotSupportedException;
    private static IResult Problem(int status, string title, string detail) => Results.Problem(statusCode: status, title: title, detail: detail);
}
