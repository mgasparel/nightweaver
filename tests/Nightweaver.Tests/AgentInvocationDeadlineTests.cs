using Nightweaver.Execution;

namespace Nightweaver.Tests;

public sealed class AgentInvocationDeadlineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoleDeadlineIncludesStartupAndPromptDelivery(bool promptDelivered)
    {
        var clock = new InvocationDeadlineClock();
        var root = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcome = AgentInvocationExecutor.WaitForOutcomeAsync(root.Task,
            promptDelivered ? Task.CompletedTask : pending.Task, pending.Task, pending.Task,
            TimeSpan.FromSeconds(2), clock.GetTimestamp(), clock, TestContext.Current.CancellationToken);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(outcome.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await outcome.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionStatus.TimedOut, result.Status);
        Assert.Null(result.ExitCode);
        Assert.False(root.Task.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeSpentInsideNativeLaunchIsNotGivenANewBudget(bool rootExited)
    {
        var clock = new InvocationDeadlineClock();
        var started = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(2));
        var root = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (rootExited) root.SetResult(0);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = await AgentInvocationExecutor.WaitForOutcomeAsync(root.Task, Task.CompletedTask,
            pending.Task, pending.Task, TimeSpan.FromSeconds(2), started,
            clock, TestContext.Current.CancellationToken);
        Assert.Equal(AgentExecutionStatus.TimedOut, result.Status);
        Assert.Equal(rootExited ? 0 : (int?)null, result.ExitCode);
    }
}
