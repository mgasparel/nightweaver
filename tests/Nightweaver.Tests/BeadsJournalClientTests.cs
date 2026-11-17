using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nightweaver.Application;
using Nightweaver.Inspection;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class BeadsJournalClientTests
{
    [Fact]
    public async Task MultilineRecordsKeepOnlySequencesAndAcceptOldOrDuplicateHints()
    {
        const string text = """
            {
              "seq": 2, "payload": {"text": "braces } and escaped \" quotes"}
            }
            {"seq":1}
            {"seq":2}
            """;
        Assert.Equal([2L, 1L, 2L], await ParseAsync(text));
    }

    [Theory]
    [InlineData("{\"seq\":1}\n{\"seq\":")]
    [InlineData("{\"seq\":1}\n{\"seq\":-1}")]
    [InlineData("{\"seq\":1}\n{\"seq\":\"2\"}")]
    [InlineData("{\"seq\":1}\n{\"seq\":9223372036854775808}")]
    [InlineData("{\"seq\":1}\n{}")]
    [InlineData("{\"seq\":1}\n[]")]
    [InlineData("{\"seq\":1}\nnot json")]
    [InlineData("{\"seq\":1} {\"seq\":2}")]
    public async Task MalformedSuffixCannotReturnAValidPrefix(string text)
    {
        var exception = await Record.ExceptionAsync(() => ParseAsync(text));
        Assert.True(exception is InvalidDataException or JsonException, exception?.ToString());
    }

    [Fact]
    public async Task BoundsApplyToRecordsAggregateBytesWhitespaceAndRecordCount()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync("{\"seq\":1,\"text\":\"" + new string('x', 1024 * 1024) + "\"}"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync("{\"seq\":1,\"text\":\"" + new string('\u754c', 400_000) + "\"}"));
        var row = "{\"seq\":1,\"text\":\"" + new string('x', 600_000) + "\"}\n";
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(string.Concat(Enumerable.Repeat(row, 15))));
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(new string('\n', BeadsJournalClient.MaximumBatchBytes + 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync(string.Concat(Enumerable.Repeat("{\"seq\":1}\n", 257))));
        Assert.Equal(256, (await ParseAsync(string.Concat(Enumerable.Repeat("{\"seq\":1}\n", 256)))).Count);
    }

    [Fact]
    public async Task StructuredTruncationRequiresACompleteValidBatch()
    {
        var exception = await Assert.ThrowsAsync<BeadsJournalTruncatedException>(() =>
            ParseAsync("{\"code\":\"events_journal_truncated\",\"head\":42}\n"));
        Assert.Equal(42, exception.Head);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ParseAsync("{\"code\":\"events_journal_truncated\",\"head\":42}\npartial"));
    }

    [Fact]
    public async Task NativePipesDrainStderrFloodAndSettleMalformedOutputBeforeReturning()
    {
        using var temp = new TempDirectory();
        var output = Path.Combine(temp.Path, "journal.json");
        await File.WriteAllTextAsync(output, "{\"seq\":7}\n", TestContext.Current.CancellationToken);
        var client = new BeadsJournalClient(new SystemProcessRunner(), request =>
            StartFixture(request, ["journal-output", output, "1100000"]));
        Assert.Equal([7L], await client.ReadBatchAsync(new(temp.Path, "main"), 0, 256, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(output, "{\"seq\":1}\nmalformed\n", TestContext.Current.CancellationToken);
        using var retained = new RetainedProcess();
        client = new BeadsJournalClient(new SystemProcessRunner(), request =>
        {
            var started = StartFixture(request, ["journal-output", output, "0", Path.Combine(temp.Path, "holding.pid")]);
            retained.Attach(started.Id);
            return started;
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadBatchAsync(new(temp.Path, "main"), 0, 256, TestContext.Current.CancellationToken));
        Assert.True(retained.Process!.HasExited);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommandDeadlineAndCancellationKillAndSettleActualChild(bool expireDeadline)
    {
        using var temp = new TempDirectory();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new InvocationDeadlineClock();
        var pid = Path.Combine(temp.Path, "held.pid");
        using var retained = new RetainedProcess();
        var client = new BeadsJournalClient(new SystemProcessRunner(), request =>
        {
            Assert.Equal(TimeSpan.FromSeconds(30), request.Timeout);
            var started = StartFixture(request, ["never-read", pid]);
            retained.Attach(started.Id);
            return started;
        }, clock);
        var read = client.ReadBatchAsync(new(temp.Path, "main"), 0, 256, lifetime.Token);
        try
        {
            var ready = Stopwatch.StartNew();
            while (!File.Exists(pid))
            {
                Assert.False(retained.Process!.HasExited);
                Assert.True(ready.Elapsed < TimeSpan.FromSeconds(10), "Child did not publish readiness.");
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            if (expireDeadline) clock.Advance(TimeSpan.FromSeconds(30));
            else lifetime.Cancel();
            if (expireDeadline) await Assert.ThrowsAsync<TimeoutException>(() => read);
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
            Assert.True(retained.Process!.HasExited);
        }
        finally
        {
            lifetime.Cancel();
            try { await read; }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }
    }

    private static async Task<IReadOnlyList<long>> ParseAsync(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return await BeadsJournalClient.ParseBatchAsync(reader, 256, TestContext.Current.CancellationToken);
    }

    private static Process StartFixture(ProcessRequest request, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = request.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        info.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment.Remove("NIGHTWEAVER_FIXTURE_ACTION");
        info.Environment.Remove("NIGHTWEAVER_FIXTURE_CLASSIFIER_ACTION");
        info.Environment.Remove("NIGHTWEAVER_INVOCATION_PURPOSE");
        var process = new Process { StartInfo = info };
        Assert.True(FixtureProcess.Start(process));
        process.StandardInput.Close();
        return process;
    }

    private sealed class RetainedProcess : IDisposable
    {
        internal Process? Process { get; private set; }
        internal void Attach(int id) => Process = System.Diagnostics.Process.GetProcessById(id);
        public void Dispose() => Process?.Dispose();
    }
}
