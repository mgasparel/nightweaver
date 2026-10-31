using System.Diagnostics;
using System.Text.Json;

var fixture = Path.Combine(Path.GetTempPath(), "nightweaver-qa-" + Guid.NewGuid().ToString("N"));
try
{
    var repository = Path.Combine(fixture, "repo");
    Directory.CreateDirectory(repository);
    var userHome = Path.Combine(fixture, "home");
    Directory.CreateDirectory(userHome);
    var config = Path.Combine(fixture, "project.json");
    File.WriteAllText(config, JsonSerializer.Serialize(new { name = "qa", target_repository = new { path = repository } }));

    var builder = DistributedApplication.CreateBuilder(args);
    builder.OnBeforeStart(async (_, cancellationToken) =>
    {
        await RunAsync("git", repository, userHome, cancellationToken, "init", "-b", "main");
        await RunAsync("bd", repository, userHome, cancellationToken, "init", "--prefix", "qa", "--non-interactive", "--skip-agents", "--skip-hooks");
        await RunAsync("bd", repository, userHome, cancellationToken, "create", "--title", "QA fixture control", "--type", "task");
        await RunAsync("bd", repository, userHome, cancellationToken, "create", "--title", "Active QA task", "--type", "task", "--status", "in_progress");
        var decisionId = await CreateBeadAsync(repository, userHome, cancellationToken, "QA product decision", "feature", "product-decision-required");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", decisionId, "Prior cycle: the implementation can keep the current route.");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", decisionId, "Which option should we choose now? Keep the existing API contract.");
        var secondDecisionId = await CreateBeadAsync(repository, userHome, cancellationToken, "QA second decision", "feature", "product-decision-required");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", secondDecisionId, "Which option should this second fixture choose?");

        var needsHumanId = await CreateBeadAsync(repository, userHome, cancellationToken, "QA needs human", "task", "needs-human", "stage:qa");
        await RunAsync("bd", repository, userHome, cancellationToken, "update", needsHumanId, "--set-metadata", "repair_cycles=2");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", needsHumanId,
            NeedsHumanDiagnostic(needsHumanId, "task", "qa", "stage:qa", "Verification needs a human choice."));

        var abortId = await CreateBeadAsync(repository, userHome, cancellationToken, "QA needs human abort", "task", "needs-human", "stage:review");
        await RunAsync("bd", repository, userHome, cancellationToken, "update", abortId, "--set-metadata", "repair_cycles=1");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", abortId,
            NeedsHumanDiagnostic(abortId, "task", "reviewer", "stage:review", "Review cannot continue without an explicit decision."));

        var markdownId = await CreateBeadAsync(repository, userHome, cancellationToken, "QA workspace markdown", "feature");
        var longCode = new string('x', 600);
        var markdown = string.Join("\n\n", "# Long workspace fixture",
            "This specification checks **Markdown** rendering, tables, code and links.",
            $"```text\n{longCode}\n```",
            "| Column | Value |\n| --- | --- |\n| Safe | wrapped |",
            "[Relative file](notes.txt) [Root file](/notes.txt) [Protocol-relative](//example.invalid/file) [Unsafe](javascript:alert(1)) [Data](data:text/html,hello) [Allowed](https://example.com/reference) [Current comment](#workspace-comment-0)",
            "<script>window.__workspaceInjected = true</script><img src=\"https://example.invalid/remote.png\" onerror=\"window.__workspaceInjected = true\">",
            "![remote image](https://example.invalid/markdown-image.png)");
        await RunAsync("bd", repository, userHome, cancellationToken, "update", markdownId,
            "--description", markdown,
            "--design", "## Design source\n\nKeep this text exact.",
            "--acceptance", "- [ ] Acceptance remains readable as text.",
            "--notes", "Workspace fixture note.");
        await RunAsync("bd", repository, userHome, cancellationToken, "comments", "add", markdownId,
            "Comment anchor for the sanitizer fixture. <script>window.__workspaceInjected = true</script><img src=\"https://example.invalid/comment.png\" onerror=\"window.__workspaceInjected = true\">");
        await RunAsync("bd", repository, userHome, cancellationToken, "create", "--title", "Blocked QA task", "--type", "task", "--status", "blocked");
    });
    builder.AddProject<Projects.Nightweaver_Web>("web")
        // Publish the direct local binding checked by the mutation-origin policy.
        .WithHttpEndpoint(name: "http", isProxied: false)
        .WithEnvironment("Nightweaver__Root", fixture)
        .WithEnvironment("Nightweaver__ConfigPath", config)
        // The app's bd subprocesses must resolve configuration and credentials only
        // inside this disposable fixture, just like the seed commands above.
        .WithEnvironment("HOME", userHome)
        .WithEnvironment("USERPROFILE", userHome)
        .WithEnvironment("APPDATA", userHome)
        .WithEnvironment("XDG_CONFIG_HOME", userHome)
        .WithEnvironment("GIT_CONFIG_GLOBAL", Path.Combine(userHome, "gitconfig"))
        .WithEnvironment("GIT_CONFIG_NOSYSTEM", "1")
        .WithEnvironment("BD_DOLT_AUTO_START", "false")
        .WithEnvironment("BD_DISABLE_METRICS", "1")
        .WithEnvironment("BD_NO_HOOKS", "true")
        .WithEnvironment("BEADS_DOLT_SERVER_HOST", "127.0.0.1")
        .WithEnvironment("BD_IMPORT_AUTO", "false")
        .WithEnvironment("BD_EXPORT_AUTO", "false")
        .WithEnvironment("BD_BACKUP_ENABLED", "false")
        .WithEnvironment("BD_DOLT_AUTO_PUSH", "false")
        .WithEnvironment("BD_EVENTS_EXPORT", "false")
        .WithBrowserLogs();
    using var app = builder.Build();
    await app.RunAsync();
}
finally
{
    for (var attempt = 0; attempt < 10 && Directory.Exists(fixture); attempt++)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(fixture, recursive: true);
        }
        catch (IOException) when (attempt < 9) { await Task.Delay(1000); }
        catch (UnauthorizedAccessException) when (attempt < 9) { await Task.Delay(1000); }
    }

    if (Directory.Exists(fixture))
    {
        Console.Error.WriteLine($"Could not remove QA fixture directory '{fixture}' after ten attempts.");
        Environment.ExitCode = 1;
    }
}

static async Task<string> CreateBeadAsync(string repository, string userHome, CancellationToken cancellationToken,
    string title, string type, params string[] labels)
{
    var arguments = new List<string> { "create", "--title", title, "--type", type, "--json" };
    if (labels.Length > 0)
    {
        arguments.Add("--labels");
        arguments.Add(string.Join(',', labels));
    }
    using var document = JsonDocument.Parse(await RunAsync("bd", repository, userHome, cancellationToken, arguments.ToArray()));
    return document.RootElement.GetProperty("id").GetString()
        ?? throw new InvalidOperationException("The QA Bead fixture has no ID.");
}

static string NeedsHumanDiagnostic(string beadId, string beadType, string failedRole, string stage, string reason)
    => JsonSerializer.Serialize(new
    {
        schema = "nightweaver-needs-human-v1",
        bead_id = beadId,
        bead_type = beadType,
        cause = "operational-retries-exhausted",
        failed_role = failedRole,
        reason,
        latest_findings = "The isolated browser fixture needs a human decision.",
        cleanup_preflight = "",
        stage,
        invocation_id = "qa-fixture",
        log_directory = "logs/qa-fixture",
        log_files = Array.Empty<string>()
    });

static async Task<string> RunAsync(string executable, string repository, string userHome, CancellationToken cancellationToken, params string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        WorkingDirectory = repository,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    foreach (var key in new[] { "BEADS_DIR", "BEADS_DB", "BD_DB", "BEADS_DOLT_SERVER_DATABASE", "BEADS_DOLT_DATA_DIR", "BEADS_DOLT_SERVER_MODE", "BEADS_DOLT_SHARED_SERVER", "BEADS_MEM_PROFILE", "BEADS_MEM_STATS", "BD_CPU_PROFILE", "BD_MEM_PROFILE", "BD_TRACE", "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" }) start.Environment.Remove(key);
    start.Environment["HOME"] = userHome;
    start.Environment["USERPROFILE"] = userHome;
    start.Environment["APPDATA"] = userHome;
    start.Environment["XDG_CONFIG_HOME"] = userHome;
    start.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(userHome, "gitconfig");
    start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
    foreach (var key in new[] { "BD_IMPORT_AUTO", "BD_EXPORT_AUTO", "BD_BACKUP_ENABLED", "BD_DOLT_AUTO_PUSH", "BD_EVENTS_EXPORT" }) start.Environment[key] = "false";
    start.Environment["BD_DISABLE_METRICS"] = "1";
    start.Environment["BD_NO_HOOKS"] = "true";
    start.Environment["BD_DOLT_AUTO_START"] = "false";
    start.Environment["BEADS_DOLT_SERVER_HOST"] = "127.0.0.1";

    using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
    var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
    var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
    try
    {
        await process.WaitForExitAsync(linked.Token);
        var output = await stdout;
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{executable} failed (exit {process.ExitCode}): {error} {output}");
        return output;
    }
    catch (OperationCanceledException)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        _ = await stdout;
        var error = await stderr;
        if (cancellationToken.IsCancellationRequested) throw;
        throw new TimeoutException($"{executable} timed out after 30 seconds: {error}");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}


