// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIMeter.Tests;

[TestClass]
public sealed class ProviderClientTests
{
    private const string Credentials = """{"claudeAiOauth":{"accessToken":"fixture-token","subscriptionType":"max"}}""";

    [TestMethod]
    public async Task ClaudeSendsFixedHeadersAndParsesAsymmetricWindowsWithoutChangingCredentials()
    {
        using var directory = new TemporaryDirectory();
        var credentialFile = directory.Write(".credentials.json", Credentials);
        var writtenAt = File.GetLastWriteTimeUtc(credentialFile);
        using var handler = new FixtureHandler((request, _) =>
        {
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("https://api.anthropic.com/api/oauth/usage", request.RequestUri?.AbsoluteUri);
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("fixture-token", request.Headers.Authorization?.Parameter);
            Assert.AreEqual("oauth-2025-04-20", request.Headers.GetValues("anthropic-beta").Single());
            return Task.FromResult(JsonResponse("""
                {"five_hour":{"utilization":8.5,"resets_at":"2026-10-15T14:00:00+02:00"},
                 "seven_day":{"utilization":25,"resets_at":"invalid"},"seven_day_opus":null,
                 "seven_day_sonnet":{"utilization":105}}
                """));
        });
        using var http = new HttpClient(handler);
        var usage = await new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None);

        Assert.AreEqual("max", usage.Plan);
        Assert.AreEqual(3, usage.Windows.Count);
        Assert.AreEqual(new UsageWindow("Session (5 hr)", 8.5, DateTimeOffset.Parse("2026-10-15T12:00:00Z")), usage.Windows[0]);
        Assert.AreEqual(new UsageWindow("Weekly", 25, null), usage.Windows[1]);
        Assert.AreEqual(new UsageWindow("Weekly \u00b7 Sonnet", 100, null), usage.Windows[2]);
        Assert.AreEqual(100d, usage.PeakPercent);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(Credentials, File.ReadAllText(credentialFile));
        Assert.AreEqual(writtenAt, File.GetLastWriteTimeUtc(credentialFile));
    }

    [TestMethod]
    [DataRow(null, "not found")]
    [DataRow("", "credential file is empty")]
    [DataRow("  \r\n", "credential file is empty")]
    [DataRow("{fixture-secret", "malformed")]
    [DataRow("[]", "malformed")]
    [DataRow("{}", "token is empty")]
    [DataRow("{\"claudeAiOauth\":null}", "token is empty")]
    [DataRow("{\"claudeAiOauth\":{\"accessToken\":\" \"}}", "token is empty")]
    [DataRow("{\"claudeAiOauth\":{\"accessToken\":123}}", "malformed")]
    [DataRow("{\"claudeAiOauth\":{\"accessToken\":\"fixture-secret\\r\\nInjected: yes\"}}", "malformed")]
    public async Task ClaudeRejectsMissingEmptyAndMalformedCredentialsBeforeHttp(string? contents, string message)
    {
        using var directory = new TemporaryDirectory();
        if (contents is not null)
            directory.Write(".credentials.json", contents);
        using var handler = new FixtureHandler((_, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);

        await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), message);
        Assert.AreEqual(0, handler.Calls);
        if (contents is not null)
            Assert.AreEqual(contents, File.ReadAllText(System.IO.Path.Combine(directory.Path, ".credentials.json")));
    }

    [TestMethod]
    [DataRow(401, "rejected the saved token")]
    [DataRow(403, "denied access (HTTP 403)")]
    [DataRow(429, "HTTP 429")]
    [DataRow(503, "HTTP 503")]
    [DataRow(500, "HTTP 500")]
    [DataRow(302, "HTTP 302")]
    public async Task ClaudeReturnsStatusAndLongRetryAfterWithoutRetrying(int status, string message)
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) =>
        {
            var response = JsonResponse("fixture-secret-response-body", (HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", "7200");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);

        var error = await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), message);
        Assert.AreEqual(TimeSpan.FromHours(2), error.RetryAfter);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(503)]
    public async Task ClaudePreservesHttpDateRetryAfter(int status)
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        var reset = DateTimeOffset.UtcNow.AddHours(3);
        using var handler = new FixtureHandler((_, _) =>
        {
            var response = JsonResponse("", (HttpStatusCode)status);
            response.Headers.TryAddWithoutValidation("Retry-After", reset.ToString("r", CultureInfo.InvariantCulture));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), $"HTTP {status}");

        Assert.IsNotNull(error.RetryAfter);
        Assert.IsTrue(error.RetryAfter > TimeSpan.FromHours(3) - TimeSpan.FromSeconds(5));
        Assert.IsTrue(error.RetryAfter <= TimeSpan.FromHours(3));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("1.5")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-1")]
    [DataRow("+1")]
    [DataRow("1e3")]
    [DataRow("not a date")]
    [DataRow("Mon invalid")]
    [DataRow("")]
    public async Task ClaudeIgnoresInvalidRetryAfter(string value)
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) =>
        {
            var response = JsonResponse("", HttpStatusCode.TooManyRequests);
            response.Headers.TryAddWithoutValidation("Retry-After", value);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), "HTTP 429");
        Assert.IsNull(error.RetryAfter);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("Sun, 06 Nov 1994 08:49:37 GMT")]
    public async Task ClaudeAllowsZeroAndPastRetryAfter(string value)
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) =>
        {
            var response = JsonResponse("", HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", value);
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), "HTTP 503");
        Assert.AreEqual(TimeSpan.Zero, error.RetryAfter);
    }

    [TestMethod]
    [DataRow("{\"five_hour\":{\"utilization\":\"NaN\"}}", "invalid percentage")]
    [DataRow("{\"five_hour\":{\"utilization\":1e309}}", "invalid percentage")]
    [DataRow("{\"five_hour\":{\"utilization\":null}}", "invalid percentage")]
    [DataRow("{\"five_hour\":{}}", "invalid percentage")]
    [DataRow("{\"five_hour\":false}", "invalid response")]
    [DataRow("{fixture-secret", "invalid JSON")]
    public async Task ClaudeRejectsInvalidUsageWithoutExposingTheBody(string body, string message)
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var http = new HttpClient(handler);
        await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), message);
    }

    [TestMethod]
    public async Task ClaudeAllowsAbsentAndNullWindows()
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) => Task.FromResult(JsonResponse("{\"five_hour\":null}")));
        using var http = new HttpClient(handler);
        var usage = await new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None);
        Assert.AreEqual(0, usage.Windows.Count);
        Assert.IsNull(usage.PeakPercent);
    }

    [TestMethod]
    public async Task ClaudeBoundsResponseBytesWithoutContentLength()
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler((_, _) =>
        {
            var content = new StreamContent(new NonSeekableMemoryStream(new byte[1024 * 1024 + 1]));
            Assert.IsNull(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var http = new HttpClient(handler);
        await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(CancellationToken.None), "too large");
    }

    [TestMethod]
    public async Task ClaudeDistinguishesDeadlineFromCallerCancellation()
    {
        using var directory = new TemporaryDirectory();
        directory.Write(".credentials.json", Credentials);
        using var handler = new FixtureHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var http = new HttpClient(handler);
        await ExpectUsageAsync(() => new ClaudeUsageClient(http, directory.Path, TimeSpan.FromMilliseconds(100))
            .FetchAsync(CancellationToken.None), "timed out");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await ExpectCancellationAsync(() => new ClaudeUsageClient(http, directory.Path).FetchAsync(cancellation.Token));
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task CodexHandshakesIgnoresNotificationsDrainsStderrAndExitsAfterStdinCloses()
    {
        using var fixture = new CodexFixture();
        var usage = await fixture.Client().FetchAsync(CancellationToken.None);

        Assert.AreEqual("pro", usage.Plan);
        Assert.AreEqual(2, usage.Windows.Count);
        Assert.AreEqual(new UsageWindow("Weekly", 21, DateTimeOffset.FromUnixTimeSeconds(1792542244)), usage.Windows[0]);
        Assert.AreEqual(new UsageWindow("Session (5 hr)", 7.5, null), usage.Windows[1]);
        Assert.AreEqual(21d, usage.PeakPercent);
        fixture.AssertFinished();
    }

    [TestMethod]
    public async Task CodexLaunchesCmdWithSpacesAndShellMetacharactersInPath()
    {
        using var fixture = new CodexFixture();
        var wrapper = fixture.Directory.Write("codex fixture.cmd", $"@echo off\r\n\"{CodexFixture.PowerShell}\" %*\r\n");
        var usage = await fixture.Client(executable: wrapper).FetchAsync(CancellationToken.None);
        Assert.AreEqual("pro", usage.Plan);
        fixture.AssertFinished();
    }

    [TestMethod]
    [DataRow("signed-out", "no account")]
    [DataRow("api-key", "API key")]
    [DataRow("rpc-error", "rejected the account/rateLimits/read request")]
    [DataRow("invalid-percent", "invalid percentage")]
    [DataRow("infinite-percent", "invalid percentage")]
    [DataRow("invalid-json", "invalid JSON")]
    [DataRow("oversized", "too large")]
    public async Task CodexRejectsAccountAndProtocolErrorsAndClosesStdin(string mode, string message)
    {
        using var fixture = new CodexFixture();
        await ExpectUsageAsync(() => fixture.Client(mode).FetchAsync(CancellationToken.None), message);
        fixture.AssertFinished();
    }

    [TestMethod]
    public async Task CodexReportsEarlyExit()
    {
        using var fixture = new CodexFixture();
        await ExpectUsageAsync(() => fixture.Client("early-exit").FetchAsync(CancellationToken.None), "closed before");
        fixture.AssertExited("pid");
    }

    [TestMethod]
    public async Task CodexAllowsNullPrimaryAndClampsSecondary()
    {
        using var fixture = new CodexFixture();
        var usage = await fixture.Client("secondary-only").FetchAsync(CancellationToken.None);
        Assert.AreEqual(1, usage.Windows.Count);
        Assert.AreEqual(new UsageWindow("Daily", 100, null), usage.Windows[0]);
        fixture.AssertFinished();
    }

    [TestMethod]
    public async Task CodexAllowsNoUsageWindows()
    {
        using var fixture = new CodexFixture();
        var usage = await fixture.Client("empty").FetchAsync(CancellationToken.None);
        Assert.AreEqual(0, usage.Windows.Count);
        Assert.IsNull(usage.PeakPercent);
        fixture.AssertFinished();
    }

    [TestMethod]
    public async Task CodexDeadlineClosesStdinWithoutForcingRefresh()
    {
        using var fixture = new CodexFixture();
        await ExpectUsageAsync(() => fixture.Client("pending", timeout: TimeSpan.FromSeconds(3))
            .FetchAsync(CancellationToken.None), "timed out");
        Assert.IsTrue(fixture.Directory.Exists("ready"), "Fixture must reach the non-refreshing account/read before the deadline.");
        fixture.AssertFinished();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CodexCancellationKillsStubbornProcessTreeAndWaitsForExit(bool useCmd)
    {
        using var fixture = new CodexFixture();
        using var cancellation = new CancellationTokenSource();
        var wrapper = useCmd
            ? fixture.Directory.Write("codex fixture.cmd", $"@echo off\r\n\"{CodexFixture.PowerShell}\" %*\r\n")
            : null;
        var pending = fixture.Client("stubborn", executable: wrapper).FetchAsync(cancellation.Token);
        try
        {
            await fixture.WaitUntilReadyAsync(pending);
        }
        finally
        {
            cancellation.Cancel();
        }
        await ExpectCancellationAsync(() => pending);
        fixture.AssertExited("pid");
        fixture.AssertExited("child-pid");
    }

    [TestMethod]
    public async Task CodexPreCanceledRequestDoesNotStartAProcess()
    {
        using var fixture = new CodexFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectCancellationAsync(() => fixture.Client().FetchAsync(cancellation.Token));
        Assert.IsFalse(fixture.Directory.Exists("pid"));
    }

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task<UsageException> ExpectUsageAsync(Func<Task<ProviderUsage>> action, string message)
    {
        try { await action(); }
        catch (UsageException error)
        {
            Assert.IsTrue(error.Message.Contains(message, StringComparison.Ordinal), error.Message);
            Assert.IsFalse(error.ToString().Contains("fixture-secret", StringComparison.Ordinal));
            Assert.IsFalse(error.ToString().Contains("fixture-token", StringComparison.Ordinal));
            return error;
        }
        Assert.Fail($"Expected UsageException containing '{message}'.");
        throw new InvalidOperationException();
    }

    private static async Task ExpectCancellationAsync(Func<Task<ProviderUsage>> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        Assert.Fail("Expected caller cancellation, not a timeout or a usage error.");
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        // Spaces, ampersands, and non-ASCII characters exercise native and cmd quoting and UTF-8.
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AI Meter & fixture \u00e9 " + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory() => System.IO.Directory.CreateDirectory(Path);

        internal string Write(string name, string contents)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }

        internal bool Exists(string name) => File.Exists(System.IO.Path.Combine(Path, name));
        public void Dispose() => System.IO.Directory.Delete(Path, recursive: true);
    }

    private sealed class CodexFixture : IDisposable
    {
        internal static string PowerShell => System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        internal TemporaryDirectory Directory { get; } = new();
        private readonly string script;

        internal CodexFixture()
        {
            script = System.IO.Path.Combine(Directory.Path, "fake codex.ps1");
            File.Copy(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "FakeCodex.ps1"), script);
        }

        internal CodexUsageClient Client(string mode = "success", string? executable = null, TimeSpan? timeout = null) =>
            new(executable ?? PowerShell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, mode, Directory.Path],
                timeout ?? TimeSpan.FromSeconds(15));

        internal async Task WaitUntilReadyAsync(Task<ProviderUsage> pending)
        {
            var deadline = Stopwatch.StartNew();
            while (!Directory.Exists("ready"))
            {
                if (pending.IsCompleted)
                    await pending;
                Assert.IsTrue(deadline.Elapsed < TimeSpan.FromSeconds(10), "Fixture did not reach account/read.");
                await Task.Delay(20);
            }
        }

        internal void AssertFinished()
        {
            Assert.IsTrue(Directory.Exists("finished"), "Child did not observe stdin closing.");
            AssertExited("pid");
        }

        internal void AssertExited(string marker)
        {
            var pid = int.Parse(File.ReadAllText(System.IO.Path.Combine(Directory.Path, marker)), CultureInfo.InvariantCulture);
            try
            {
                using var process = Process.GetProcessById(pid);
                Assert.IsTrue(process.HasExited, "Fixture child is still running.");
            }
            catch (ArgumentException) { /* No process with this id remains. */ }
        }

        public void Dispose()
        {
            // A failing test must not leave its disposable fake processes behind.
            foreach (var marker in new[] { "child-pid", "pid" })
            {
                if (!Directory.Exists(marker))
                    continue;
                var pid = int.Parse(File.ReadAllText(System.IO.Path.Combine(Directory.Path, marker)), CultureInfo.InvariantCulture);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                    }
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            Directory.Dispose();
        }
    }
}
