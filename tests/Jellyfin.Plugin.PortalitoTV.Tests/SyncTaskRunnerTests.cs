using Jellyfin.Plugin.PortalitoTV.Api;
using MediaBrowser.Model.Tasks;
using Xunit;

namespace Jellyfin.Plugin.PortalitoTV.Tests;

public class SyncTaskRunnerTests
{
    private const string Waiting = "waiting";
    private const string Running = "syncing";

    private static readonly SyncTaskRunner.Timing Fast =
        new(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(400));

    [Fact]
    public async Task IdleTaskIsExecutedOnceAndAnnounced()
    {
        // Idle before, running after being requested (reads 1-4) and idle at the end.
        var t = new FakeTask { StateAt = (n, runs) => runs == 0 ? TaskState.Idle : (n < 5 ? TaskState.Running : TaskState.Idle) };
        await t.Run();

        Assert.Equal(1, t.Executions);
        Assert.Equal((20, Running), t.Reports[0]);
        Assert.DoesNotContain(t.Reports, r => r.Phase == Waiting);
    }

    [Fact]
    public async Task TaskAlreadyRunningIsWaitedForBeforeExecutingAgain()
    {
        var polls = 0;
        var t = new FakeTask();
        t.StateAt = (_, runs) =>
        {
            if (runs == 0)
            {
                // Running by itself for 4 reads and then free.
                return ++polls <= 4 ? TaskState.Running : TaskState.Idle;
            }

            return TaskState.Idle;
        };
        await t.Run();

        // It wasn't started while it ran: it is started once, after waiting, and the wait was reported.
        Assert.Equal(1, t.Executions);
        Assert.True(polls > 4);
        Assert.Contains(t.Reports, r => r.Phase == Waiting);
        Assert.True(t.Reports.IndexOf(t.Reports.First(r => r.Phase == Running && r.Percent == 20)) > t.Reports.FindLastIndex(r => r.Phase == Waiting));
    }

    [Fact]
    public async Task WaitingPhaseIsMappedBetweenOneAndTwenty()
    {
        var polls = 0;
        var t = new FakeTask
        {
            StateAt = (_, runs) => runs == 0 && ++polls <= 3 ? TaskState.Running : TaskState.Idle,
            ProgressAt = _ => 50,
        };
        await t.Run();

        var waiting = t.Reports.Where(r => r.Phase == Waiting).Select(r => r.Percent).ToList();
        Assert.NotEmpty(waiting);
        Assert.All(waiting, p => Assert.Equal(1 + (19 * 0.5), p));
    }

    [Fact]
    public async Task InvalidOperationFromExecuteIsSwallowedAndTheRunningTaskIsAwaited()
    {
        // "Cannot execute a Task that is already running": it started by itself right as it was requested.
        var reads = 0;
        var t = new FakeTask
        {
            StateAt = (_, runs) => runs == 0 ? TaskState.Idle : (++reads <= 3 ? TaskState.Running : TaskState.Idle),
            OnExecute = () => throw new InvalidOperationException("Cannot execute a Task that is already running"),
        };

        await t.Run(); // must not throw

        Assert.Equal(1, t.Executions);
        Assert.True(reads > 3, "it should have waited for the running task to finish");
    }

    [Fact]
    public async Task OtherExceptionsFromExecuteArePropagated()
    {
        var t = new FakeTask { OnExecute = () => throw new IOException("disk") };
        await Assert.ThrowsAsync<IOException>(() => t.Run());
    }

    [Fact]
    public async Task RunningPhaseIsMappedBetweenTwentyAndFiftyFive()
    {
        var polls = 0;
        var t = new FakeTask
        {
            StateAt = (_, runs) => runs == 0 ? TaskState.Idle : (++polls <= 3 ? TaskState.Running : TaskState.Idle),
            ProgressAt = _ => 100,
        };
        await t.Run();

        var running = t.Reports.Where(r => r.Phase == Running && r.Percent > 20).Select(r => r.Percent).ToList();
        Assert.NotEmpty(running);
        Assert.All(running, p => Assert.Equal(55, p));
    }

    [Theory]
    [InlineData(null, 20)]
    [InlineData(0.0, 20)]
    [InlineData(-30.0, 20)]
    [InlineData(150.0, 55)]
    public async Task ProgressIsNullToleratedAndClamped(double? progress, double expected)
    {
        var polls = 0;
        var t = new FakeTask
        {
            StateAt = (_, runs) => runs == 0 ? TaskState.Idle : (++polls <= 2 ? TaskState.Running : TaskState.Idle),
            ProgressAt = _ => progress,
        };
        await t.Run();

        var last = t.Reports.Where(r => r.Phase == Running).Select(r => r.Percent).Max();
        Assert.Equal(expected, last);
    }

    [Fact]
    public async Task TaskThatNeverStartsReturnsAfterTheStartTimeout()
    {
        var t = new FakeTask(); // always Idle: Execute doesn't start it
        var started = DateTime.UtcNow;
        await t.Run();

        Assert.Equal(1, t.Executions);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task TaskThatNeverEndsReturnsAfterTheMaxWait()
    {
        var t = new FakeTask { StateAt = (_, _) => TaskState.Running };
        var started = DateTime.UtcNow;
        await t.Run(); // it would hang without the cap

        // It waits the cap before starting (it was running) and again afterwards: it doesn't hang.
        Assert.Equal(1, t.Executions);
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EventsAreInOrderWaitThenExecute()
    {
        var polls = 0;
        var t = new FakeTask { StateAt = (_, runs) => runs == 0 && ++polls <= 2 ? TaskState.Running : TaskState.Idle };
        t.OnExecute = () =>
        {
            t.Events.Add($"polls-before={polls}");
            return Task.CompletedTask;
        };
        await t.Run();

        // When it is executed, the previous run had already finished (it was read more than 2 times).
        Assert.Equal(new[] { "execute", "polls-before=3" }, t.Events);
    }

    [Fact]
    public void DefaultTimingsAreTheOnesTheUserWaitsFor()
    {
        var d = SyncTaskRunner.Timing.Default;
        Assert.Equal(TimeSpan.FromSeconds(1), d.Poll);
        Assert.Equal(TimeSpan.FromSeconds(10), d.StartWait);
        Assert.Equal(TimeSpan.FromMinutes(10), d.MaxWait);
        Assert.Equal(20, SyncTaskRunner.WaitEnd);
        Assert.Equal(55, SyncTaskRunner.RunEnd);
    }

    /// <summary>A simulated task: its state and progress change with how many times it was read.</summary>
    private sealed class FakeTask
    {
        private int _stateReads;

        public int Executions { get; private set; }

        public List<string> Events { get; } = new();

        public List<(double Percent, string Phase)> Reports { get; } = new();

        /// <summary>Gets or sets the state by read number (0, 1, 2...) and by whether it was executed already.</summary>
        public Func<int, int, TaskState> StateAt { get; set; } = (_, _) => TaskState.Idle;

        public Func<int, double?> ProgressAt { get; set; } = _ => null;

        public Func<Task>? OnExecute { get; set; }

        public TaskState State() => StateAt(_stateReads++, Executions);

        public Task Execute()
        {
            Executions++;
            Events.Add("execute");
            return OnExecute?.Invoke() ?? Task.CompletedTask;
        }

        public Task Run(SyncTaskRunner.Timing? timing = null) =>
            SyncTaskRunner.RunAsync(State, () => ProgressAt(_stateReads), Execute,
                (p, f) => Reports.Add((p, f)), Waiting, Running, timing ?? Fast);
    }
}
