using Nightweaver.Configuration;

namespace Nightweaver.Web;

public sealed class Program
{
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

        var app = builder.Build();
        // Resolve before listening, after all host configuration providers have been applied.
        _ = app.Services.GetRequiredService<ProjectConfig>();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.Run();
    }
}
