using System.Text.Json;
using Nightweaver.Configuration;

namespace Nightweaver.Web;

internal static class OperationObservationEndpoints
{
    internal static void AddOperationalObservations(this IServiceCollection services)
    {
        services.AddSingleton<ObservationMemoryBudget>();
        services.AddSingleton<IOperationalObservationSource, OperationalObservationSource>();
        services.AddSingleton(services => new OperationalObservationService(
            services.GetRequiredService<ProjectConfig>().Name.Value,
            services.GetRequiredService<IOperationalObservationSource>(), services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<ObservationMemoryBudget>()));
        services.AddHostedService(services => services.GetRequiredService<OperationalObservationService>());
    }

    internal static void MapOperationalObservations(this WebApplication app)
    {
        var observer = app.Services.GetRequiredService<OperationalObservationService>();
        var tracker = app.Services.GetRequiredService<TrackerBridge>();
        tracker.ObservationRequested += observer.RequestReconciliation;
        // File mutations and uncertain cleanup outcomes also wake the collector. The
        // handler has completed (and released its gates) before the wake is published.
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            finally
            {
                if (HttpMethods.IsPost(context.Request.Method)
                    && (context.Request.Path.StartsWithSegments("/api/digest/catch-up") || context.Request.Path.StartsWithSegments("/api/cleanup")))
                    observer.RequestReconciliation();
            }
        });
        app.MapGet("/api/operations/v1/projects/{projectId}/resources", (HttpContext context, string projectId) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (projectId != observer.ProjectId) return Results.NotFound();
            if (context.Request.Query.Count != 1 || !context.Request.Query.TryGetValue("key", out var values) || values.Count != 1)
                return Results.BadRequest();
            try { return Results.Json(observer.Read(values[0]!), ObservationJson.Options); }
            catch (ObservationRequestException exception) { return Problem(context, exception.StatusCode); }
        });
        app.MapPost("/api/operations/v1/projects/{projectId}/refresh", async (HttpContext context, string projectId, LocalMutationRequestPolicy policy) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (projectId != observer.ProjectId) return Results.NotFound();
            if (!policy.Allows(context.Request)) return Problem(context, 403);
            if (context.Request.Query.Count != 0) return Problem(context, 400);
            if (!context.Request.HasJsonContentType()) return Problem(context, 415);
            var body = await BoundedRequestBody.ReadAsync(context.Request, context.RequestAborted, 8192);
            if (body is null) return Problem(context, 413);
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Problem(context, 400);
                var properties = document.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 1 || !properties[0].NameEquals("keys") || properties[0].Value.ValueKind != JsonValueKind.Array)
                    return Problem(context, 400);
                var keys = properties[0].Value.EnumerateArray().Select(item => item.GetString()!).ToArray();
                if (keys.Any(key => key is null)) return Problem(context, 400);
                observer.RequestRefresh(keys);
                return Results.Accepted(value: new { accepted = true, keys });
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return Problem(context, 400); }
            catch (ObservationRequestException exception) { return Problem(context, exception.StatusCode); }
        });
    }

    private static IResult Problem(HttpContext context, int status)
    {
        if (status == 429) context.Response.Headers.RetryAfter = "2";
        return Results.Problem(statusCode: status, title: status switch
        {
            400 => "Invalid observation request", 403 => "Request origin rejected", 413 => "Request body too large",
            415 => "JSON required", 429 => "Observation capacity exceeded", _ => "Observer is shutting down"
        });
    }
}
