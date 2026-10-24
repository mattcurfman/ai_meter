// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

namespace AIMeter;

public enum Provider { Claude, Codex }

public sealed record UsageWindow
{
    public string Title { get; }
    public double PercentUsed { get; }
    public DateTimeOffset? ResetsAt { get; }

    public UsageWindow(string title, double percentUsed, DateTimeOffset? resetsAt)
    {
        if (!double.IsFinite(percentUsed))
            throw new UsageException("The usage service returned an invalid percentage.");
        Title = title;
        PercentUsed = Math.Clamp(percentUsed, 0, 100);
        ResetsAt = resetsAt;
    }
}

public sealed record ProviderUsage(string? Plan, IReadOnlyList<UsageWindow> Windows)
{
    public double? PeakPercent => Windows.Count == 0 ? null : Windows.Max(window => window.PercentUsed);
}

public sealed class UsageException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public interface IUsageClient
{
    Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken);
}
