// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;

namespace AIMeter;

public sealed class RetrySchedule(Func<double>? random = null)
{
    private readonly Func<double> random = random ?? Random.Shared.NextDouble;
    private int failures;
    public double NextAttempt { get; private set; }
    public bool IsDue(double now) => now >= NextAttempt;

    public void Succeeded(double now)
    {
        failures = 0;
        NextAttempt = now + 300;
    }

    public void Failed(double now, TimeSpan? retryAfter = null)
    {
        failures = Math.Min(failures + 1, 4);
        var ceiling = Math.Min(300 * Math.Pow(2, failures), 3600);
        var delay = ceiling / 2 * (1 + random());
        NextAttempt = now + Math.Max(delay, retryAfter?.TotalSeconds ?? 0);
    }
}

public sealed record ProviderStatus(Provider Provider, ProviderUsage? Usage = null, string? Error = null,
    DateTimeOffset? CheckedAt = null, DateTimeOffset? NextAttemptAt = null)
{
    public bool IsLoading => CheckedAt is null && Error is null && Usage is null;
}

public sealed class UsageStore
{
    private readonly Dictionary<Provider, IUsageClient> clients;
    private readonly Dictionary<Provider, RetrySchedule> schedules;
    private readonly Dictionary<Provider, ProviderStatus> states;
    private readonly TimeProvider clock;
    private readonly long origin;
    private readonly object gate = new();
    private int busy;

    public event Action? Changed;

    public UsageStore(IUsageClient claude, IUsageClient codex, TimeProvider? clock = null, Func<double>? random = null)
    {
        this.clock = clock ?? TimeProvider.System;
        origin = this.clock.GetTimestamp();
        clients = new() { [Provider.Claude] = claude, [Provider.Codex] = codex };
        schedules = Enum.GetValues<Provider>().ToDictionary(provider => provider, _ => new RetrySchedule(random));
        states = Enum.GetValues<Provider>().ToDictionary(provider => provider, provider => new ProviderStatus(provider));
    }

    public IReadOnlyList<ProviderStatus> Snapshot()
    {
        lock (gate) return Enum.GetValues<Provider>().Select(provider => states[provider]).ToArray();
    }

    public async Task CheckDueAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = Now();
            var due = schedules.Where(pair => pair.Value.IsDue(now)).Select(pair => pair.Key).ToArray();
            await Task.WhenAll(due.Select(provider => CheckAsync(provider, cancellationToken)));
        }
        finally { Volatile.Write(ref busy, 0); }
    }

    private double Now() => clock.GetElapsedTime(origin).TotalSeconds;

    private async Task CheckAsync(Provider provider, CancellationToken cancellationToken)
    {
        ProviderUsage? usage = null;
        string? error = null;
        TimeSpan? retryAfter = null;
        try { usage = await clients[provider].FetchAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (UsageException failure) { error = failure.Message; retryAfter = failure.RetryAfter; }
        catch (OperationCanceledException) { error = "The usage request timed out."; }
        catch (HttpRequestException) { error = "Cannot reach the usage service. Check your connection."; }
        catch (Exception) { error = "The usage check failed. Try again later."; }
        if (cancellationToken.IsCancellationRequested) return;
        var now = Now();
        if (error is null) schedules[provider].Succeeded(now);
        else schedules[provider].Failed(now, retryAfter);
        var checkedAt = clock.GetUtcNow();
        var delay = schedules[provider].NextAttempt - now;
        var nextAttempt = delay >= (DateTimeOffset.MaxValue - checkedAt).TotalSeconds
            ? DateTimeOffset.MaxValue : checkedAt.AddSeconds(delay);
        lock (gate)
            states[provider] = new(provider, usage, error, checkedAt, nextAttempt);
        Changed?.Invoke();
    }
}
