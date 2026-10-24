// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIMeter.Tests;

[TestClass]
public sealed class UsageStoreTests
{
    [TestMethod]
    [DataRow(0d, 300d, 600d, 1200d, 1800d)]
    [DataRow(0.5d, 450d, 900d, 1800d, 2700d)]
    [DataRow(1d, 600d, 1200d, 2400d, 3600d)]
    public void RetryDelaysMatchOtherPlatforms(double random, double first, double second, double third, double capped)
    {
        var schedule = new RetrySchedule(() => random);
        Assert.IsTrue(schedule.IsDue(0));
        foreach (var delay in new[] { first, second, third, capped, capped, capped })
        {
            schedule.Failed(37);
            Assert.AreEqual(37 + delay, schedule.NextAttempt);
            Assert.IsFalse(schedule.IsDue(37 + delay - 0.001));
            Assert.IsTrue(schedule.IsDue(37 + delay));
        }
        schedule.Failed(37, TimeSpan.FromHours(2));
        Assert.AreEqual(7237d, schedule.NextAttempt);
        schedule.Succeeded(83);
        Assert.AreEqual(383d, schedule.NextAttempt);
        schedule.Failed(83, TimeSpan.FromSeconds(1));
        Assert.AreEqual(83 + first, schedule.NextAttempt);
    }

    [TestMethod]
    public async Task ProvidersRetryIndependentlyAndIdleChecksDoNotChangeCheckedTime()
    {
        var clock = new TestClock();
        var claude = new SequenceClient(
            () => throw new UsageException("Rate limited", TimeSpan.FromHours(2)), () => Usage(17));
        var codex = new SequenceClient(() => Usage(21), () => Usage(33), () => Usage(44));
        var store = new UsageStore(claude, codex, clock, () => 0.5);
        await store.CheckDueAsync(default);
        var initial = store.Snapshot();
        Assert.AreEqual("Rate limited", initial[0].Error);
        Assert.AreEqual(clock.GetUtcNow().AddHours(2), initial[0].NextAttemptAt);
        Assert.AreEqual(21d, initial[1].Usage!.PeakPercent);
        clock.Advance(299);
        await store.CheckDueAsync(default);
        CollectionAssert.AreEqual(initial.ToArray(), store.Snapshot().ToArray());
        clock.Advance(1);
        await store.CheckDueAsync(default);
        Assert.AreEqual(initial[0], store.Snapshot()[0]);
        Assert.AreEqual(33d, store.Snapshot()[1].Usage!.PeakPercent);
        clock.Advance(6900);
        await store.CheckDueAsync(default);
        var recovered = store.Snapshot()[0];
        Assert.IsNull(recovered.Error);
        Assert.AreEqual(17d, recovered.Usage!.PeakPercent);
        Assert.AreEqual(clock.GetUtcNow().AddMinutes(5), recovered.NextAttemptAt);
    }

    [TestMethod]
    public async Task FailedCheckHidesOldUsageAndWallClockChangesDoNotBypassBackoff()
    {
        var clock = new TestClock();
        var client = new SequenceClient(() => Usage(12), () => throw new UsageException("Offline"), () => Usage(38));
        var store = new UsageStore(client, new SequenceClient(() => Usage(7)), clock, () => 0.5);
        await store.CheckDueAsync(default);
        clock.Advance(300);
        await store.CheckDueAsync(default);
        var failed = store.Snapshot()[0];
        Assert.IsNull(failed.Usage);
        Assert.AreEqual("Offline", failed.Error);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(450), failed.NextAttemptAt);
        clock.JumpWallClock(TimeSpan.FromDays(20));
        clock.Advance(449);
        await store.CheckDueAsync(default);
        Assert.AreEqual(failed, store.Snapshot()[0]);
        clock.Advance(1);
        await store.CheckDueAsync(default);
        Assert.AreEqual(38d, store.Snapshot()[0].Usage!.PeakPercent);
    }

    [TestMethod]
    public async Task LongServerDelayDoesNotOverflowTheDisplayedNextCheck()
    {
        var client = new SequenceClient(() => throw new UsageException("Rate limited", TimeSpan.MaxValue));
        var store = new UsageStore(client, new SequenceClient(() => Usage(7)), new TestClock(), () => 0.5);
        await store.CheckDueAsync(default);
        Assert.AreEqual(DateTimeOffset.MaxValue, store.Snapshot()[0].NextAttemptAt);
        Assert.AreEqual("Rate limited", store.Snapshot()[0].Error);
        Assert.AreEqual(7d, store.Snapshot()[1].Usage!.PeakPercent);
    }

    [TestMethod]
    public async Task ShutdownCancellationDoesNotBecomeAnErrorOrStartAnotherBatch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new WaitingClient(started);
        var store = new UsageStore(blocked, blocked);
        using var cancellation = new CancellationTokenSource();
        var check = store.CheckDueAsync(cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await store.CheckDueAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(store.Snapshot().All(state => state.IsLoading));
        cancellation.Cancel();
        await check.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(store.Snapshot().All(state => state.IsLoading && state.Error is null));
    }

    private static ProviderUsage Usage(double value) => new("Pro", [new("Session (5 hr)", 3, null), new("Weekly", value, null)]);

    private sealed class SequenceClient(params Func<ProviderUsage>[] responses) : IUsageClient
    {
        private int index;
        public Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken) =>
            Task.FromResult(responses[Math.Min(index++, responses.Length - 1)]());
    }

    private sealed class WaitingClient(TaskCompletionSource started) : IUsageClient
    {
        public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Usage(0);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        private DateTimeOffset wallOrigin = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => wallOrigin.AddTicks(ticks);
        public void Advance(double seconds) => ticks += (long)(seconds * TimeSpan.TicksPerSecond);
        public void JumpWallClock(TimeSpan change) => wallOrigin += change;
    }
}
