using System.Diagnostics;
using Nightweaver.Tests.AgentInvocationFixture;

namespace Nightweaver.Tests;

public sealed class NativeProcessIdentityFileTests
{
    [Fact]
    public async Task ReaderCannotObserveAnIdentityBeforePublicationCompletes()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bd.started");
        var arrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var token = TestContext.Current.CancellationToken;
        var writer = Task.Run(() => NativeProcessIdentityFile.Write(path, () =>
        {
            arrived.TrySetResult(true);
            release.Wait(token);
        }), token);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            Exception? readFailure = null;
            if (File.Exists(path)) readFailure = Record.Exception(() => NativeProcessIdentityFile.Read(path));
            Assert.False(File.Exists(path), $"Identity exposed before publication completed; reader={readFailure}");
        }
        finally
        {
            release.Set();
            await writer.WaitAsync(TimeSpan.FromSeconds(5), token);
        }
        var identity = NativeProcessIdentityFile.Read(path).Split('|');
        Assert.Equal(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), identity[0]);
        Assert.Equal(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
            long.Parse(identity[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FailedPublicationPreservesThePreviouslyCompletedIdentity()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("identity.pid", "previous complete identity");
        Assert.Throws<InvalidOperationException>(() => NativeProcessIdentityFile.Write(path,
            () => throw new InvalidOperationException("controlled pre-publication failure")));
        Assert.Equal("previous complete identity", NativeProcessIdentityFile.Read(path));
        Assert.Single(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void PublishedIdentityCanReplaceAPreviousOneWhileASharedReaderIsOpen()
    {
        using var temp = new TempDirectory();
        var path = temp.WriteFile("identity.pid", "previous complete identity");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        NativeProcessIdentityFile.Write(path);
        Assert.StartsWith($"{Environment.ProcessId}|", NativeProcessIdentityFile.Read(path), StringComparison.Ordinal);
    }
}
