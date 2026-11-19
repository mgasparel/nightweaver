namespace Nightweaver.Tests;

public sealed class FixtureReadinessFileTests
{
    [Fact]
    public void SharingLockReleasedWithinDeadlineAllowsTheOriginalPayload()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "identity-payload");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var elapsed = TimeSpan.Zero;
        try
        {
            var value = FixtureReadinessFile.Read(() => File.ReadAllText(path), TimeSpan.FromSeconds(20),
                () => elapsed, delay => { elapsed += delay; locked.Dispose(); });
            Assert.Equal("identity-payload", value);
            Assert.True(elapsed > TimeSpan.Zero);
        }
        finally { locked.Dispose(); File.Delete(path); }
    }

    [Theory]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(2)]
    public void TransientOpenFailureCannotExtendTheSingleDeadline(int error)
    {
        var elapsed = TimeSpan.Zero;
        var attempts = 0;
        var failure = new IOException("controlled readiness open failure", unchecked((int)0x80070000) | error);
        var result = Assert.Throws<TimeoutException>(() => FixtureReadinessFile.Read(
            () => { attempts++; throw failure; }, TimeSpan.FromMilliseconds(25), () => elapsed,
            delay => elapsed += delay));
        Assert.Same(failure, result.InnerException);
        Assert.Equal(TimeSpan.FromMilliseconds(25), elapsed);
        Assert.InRange(attempts, 1, 3);
    }

    [Fact]
    public void OtherIoFailuresAreNotRetried()
    {
        var failure = new IOException("not a sharing failure", unchecked((int)0x80070005));
        Assert.Same(failure, Assert.Throws<IOException>(() => FixtureReadinessFile.Read(
            () => throw failure, TimeSpan.FromSeconds(20), () => TimeSpan.Zero,
            _ => Assert.Fail("Unexpected wait"))));
    }

    [Fact]
    public void PayloadIsReturnedUnchangedForExistingIdentityValidation()
    {
        Assert.Equal("malformed-or-wrong-identity", FixtureReadinessFile.Read(
            () => "malformed-or-wrong-identity", TimeSpan.FromSeconds(20), () => TimeSpan.Zero,
            _ => Assert.Fail("Unexpected wait")));
    }
}
