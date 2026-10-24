// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIMeter;

public sealed class ClaudeUsageClient : IUsageClient
{
    private static readonly HttpClient ProductionHttp = new(new SocketsHttpHandler
    {
        // Never forward the saved bearer token to a redirected endpoint.
        AllowAutoRedirect = false,
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private readonly HttpClient http;
    private readonly string credentialDirectory;
    private readonly TimeSpan timeout;

    public ClaudeUsageClient() : this(ProductionHttp, DefaultCredentialDirectory()) { }

    // The caller owns the injected HttpClient. The endpoint cannot be overridden.
    public ClaudeUsageClient(HttpClient httpClient, string credentialDirectory, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialDirectory);
        this.http = httpClient;
        this.credentialDirectory = credentialDirectory;
        this.timeout = timeout ?? TimeSpan.FromSeconds(20);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.timeout, TimeSpan.Zero);
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            var (token, plan) = await ReadCredentialsAsync(deadline.Token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var retryAfter = ParseRetryAfter(response);
                throw new UsageException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Claude usage API rejected the saved token. Open Claude Code to refresh it.",
                    HttpStatusCode.Forbidden => "Claude usage API denied access (HTTP 403). CLI access can still work.",
                    _ => $"Claude usage API returned HTTP {(int)response.StatusCode}.",
                }, retryAfter);
            }

            if (response.Content.Headers.ContentLength > ProviderJson.MaximumBytes)
                throw new UsageException("Claude usage response is too large.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var bytes = await ProviderJson.ReadBoundedAsync(stream, deadline.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            ProviderJson.RequireObject(root);
            var windows = new List<UsageWindow>();
            AddWindow("five_hour", "Session (5 hr)");
            AddWindow("seven_day", "Weekly");
            AddWindow("seven_day_opus", "Weekly \u00b7 Opus");
            AddWindow("seven_day_sonnet", "Weekly \u00b7 Sonnet");
            return new ProviderUsage(plan, windows);

            void AddWindow(string key, string title)
            {
                if (!root.TryGetProperty(key, out var window) || window.ValueKind == JsonValueKind.Null)
                    return;
                ProviderJson.RequireObject(window);
                var percent = ProviderJson.Percent(window, "utilization");
                DateTimeOffset? resetsAt = null;
                if (window.TryGetProperty("resets_at", out var reset) && reset.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
                    resetsAt = timestamp;
                windows.Add(new UsageWindow(title, percent, resetsAt));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageException("Claude usage request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new UsageException("Cannot reach the Claude usage API.");
        }
        catch (JsonException)
        {
            throw new UsageException("Claude usage API returned invalid JSON.");
        }
        catch (IOException)
        {
            throw new UsageException("Cannot read the Claude usage response.");
        }
    }

    private static string DefaultCredentialDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    private async Task<(string Token, string? Plan)> ReadCredentialsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(Path.Combine(credentialDirectory, ".credentials.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var bytes = await ProviderJson.ReadBoundedAsync(file, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.All(value => value is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t'))
                throw new UsageException("Claude credential file is empty. Check the CLI account and CLAUDE_CONFIG_DIR.");
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (!root.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind == JsonValueKind.Null)
                throw EmptyToken();
            if (oauth.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            if (!oauth.TryGetProperty("accessToken", out var accessToken) || accessToken.ValueKind == JsonValueKind.Null)
                throw EmptyToken();
            if (accessToken.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var token = accessToken.GetString();
            if (string.IsNullOrWhiteSpace(token))
                throw EmptyToken();
            // Reject header injection before constructing a request; never include the token in an error.
            if (token.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                throw new JsonException();
            return (token, ProviderJson.OptionalString(oauth, "subscriptionType"));
        }
        catch (FileNotFoundException)
        {
            throw MissingCredentials();
        }
        catch (DirectoryNotFoundException)
        {
            throw MissingCredentials();
        }
        catch (JsonException)
        {
            throw new UsageException("Claude credential file is malformed. Check the CLI account and CLAUDE_CONFIG_DIR.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new UsageException("Cannot read the Claude credential file.");
        }

        static UsageException MissingCredentials() => new("Claude credentials not found. Check CLAUDE_CONFIG_DIR and the CLI account.");
        static UsageException EmptyToken() => new("Claude usage token is empty. Check the CLI account and CLAUDE_CONFIG_DIR.");
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        using var iterator = values.GetEnumerator();
        if (!iterator.MoveNext())
            return null;
        var value = iterator.Current.Trim();
        if (iterator.MoveNext() || value.Length == 0)
            return null;
        if (value.All(character => character is >= '0' and <= '9'))
        {
            if (!double.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds))
                return null;
            // Preserve long server hints, up to the largest delay .NET can represent.
            return seconds >= TimeSpan.MaxValue.TotalSeconds ? TimeSpan.MaxValue : TimeSpan.FromSeconds(seconds);
        }
        // Do not let date parsing reinterpret fractions, signs, NaN, or Infinity as dates.
        if (value.Length < 3 || !new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
                .Contains(value[..3], StringComparer.OrdinalIgnoreCase))
            return null;
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            return null;
        var delay = date - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }
}

public sealed class CodexUsageClient : IUsageClient
{
    private readonly string? executable;
    private readonly string[] arguments;
    private readonly TimeSpan timeout;

    public CodexUsageClient()
    {
        arguments = ["app-server"];
        timeout = TimeSpan.FromSeconds(45);
    }

    public CodexUsageClient(string executable, IEnumerable<string>? arguments = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        this.executable = executable;
        this.arguments = arguments?.ToArray() ?? ["app-server"];
        this.timeout = timeout ?? TimeSpan.FromSeconds(45);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.timeout, TimeSpan.Zero);
    }

    public async Task<ProviderUsage> FetchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = CreateStartInfo(executable ?? FindExecutable(), arguments) };
        using var drainCancellation = new CancellationTokenSource();
        Task stderrDrain = Task.CompletedTask;
        var started = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
                throw new UsageException("Cannot start the Codex CLI.");
            stderrDrain = DiscardAsync(process.StandardError.BaseStream, drainCancellation.Token);
            var lines = new JsonLines(process.StandardOutput);
            await RequestAsync(1, "initialize", new
            {
                clientInfo = new { name = "ai_meter", title = "AI Meter", version = "1.0" },
            }).ConfigureAwait(false);
            await SendAsync(new { method = "initialized" }).ConfigureAwait(false);
            var accountResult = await RequestAsync(2, "account/read", new { refreshToken = false }).ConfigureAwait(false);
            if (!accountResult.TryGetProperty("account", out var account) || account.ValueKind == JsonValueKind.Null)
                throw new UsageException("Codex CLI has no account. Run codex login.");
            ProviderJson.RequireObject(account);
            if (ProviderJson.OptionalString(account, "type") == "apiKey")
                throw new UsageException("Codex uses an API key, which has no plan usage limits.");
            var result = await RequestAsync(3, "account/rateLimits/read", new { excludeResetCreditDetails = true }).ConfigureAwait(false);
            if (!result.TryGetProperty("rateLimits", out var limits) || limits.ValueKind == JsonValueKind.Null)
                throw new UsageException("Codex did not return plan usage limits.");
            return ParseUsage(limits);

            async Task SendAsync<T>(T message)
            {
                var json = JsonSerializer.Serialize(message);
                await process.StandardInput.WriteLineAsync(json.AsMemory(), deadline.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            }

            async Task<JsonElement> RequestAsync(int id, string method, object parameters)
            {
                await SendAsync(new { id, method, @params = parameters }).ConfigureAwait(false);
                while (true)
                {
                    using var document = JsonDocument.Parse(await lines.ReadAsync(deadline.Token).ConfigureAwait(false));
                    var message = document.RootElement;
                    ProviderJson.RequireObject(message);
                    if (!message.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number ||
                        !responseId.TryGetInt32(out var number) || number != id)
                        continue;
                    if (message.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                        throw new UsageException($"Codex rejected the {method} request.");
                    if (!message.TryGetProperty("result", out var result))
                        throw new UsageException("Codex returned an invalid usage response.");
                    ProviderJson.RequireObject(result);
                    return result.Clone();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UsageException("Codex usage request timed out.");
        }
        catch (JsonException)
        {
            throw new UsageException("Codex returned invalid JSON.");
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            throw new UsageException("Cannot read the Codex app-server response.");
        }
        catch (Win32Exception)
        {
            throw new UsageException("Cannot start the Codex CLI. Install Codex and sign in.");
        }
        finally
        {
            if (started)
                await StopAsync(process, stderrDrain, drainCancellation).ConfigureAwait(false);
        }
    }

    private static ProviderUsage ParseUsage(JsonElement limits)
    {
        ProviderJson.RequireObject(limits);
        var windows = new List<UsageWindow>();
        AddWindow("primary");
        AddWindow("secondary");
        return new ProviderUsage(ProviderJson.OptionalString(limits, "planType"), windows);

        void AddWindow(string key)
        {
            if (!limits.TryGetProperty(key, out var window) || window.ValueKind == JsonValueKind.Null)
                return;
            ProviderJson.RequireObject(window);
            var percent = ProviderJson.Percent(window, "usedPercent");
            var title = "Limit";
            if (window.TryGetProperty("windowDurationMins", out var duration) && duration.ValueKind != JsonValueKind.Null)
            {
                if (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var minutes) ||
                    !double.IsFinite(minutes) || minutes <= 0)
                    throw new UsageException("Codex returned an invalid usage window duration.");
                title = minutes switch
                {
                    10080 => "Weekly",
                    1440 => "Daily",
                    < 1440 => $"Session ({(minutes / 60).ToString(CultureInfo.InvariantCulture)} hr)",
                    _ => $"{(minutes / 1440).ToString(CultureInfo.InvariantCulture)}-day",
                };
            }
            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds))
            {
                try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
                catch (ArgumentOutOfRangeException) { /* Invalid reset dates are unknown, not a failed usage read. */ }
            }
            windows.Add(new UsageWindow(title, percent, resetsAt));
        }
    }

    private static string FindExecutable()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var extension in new[] { ".exe", ".cmd" })
        {
            foreach (var entry in directories)
            {
                var directory = entry.Trim().Trim('"');
                // Do not search the working directory through an empty or relative PATH entry.
                if (!Path.IsPathFullyQualified(directory))
                    continue;
                var candidate = Path.Combine(directory, "codex" + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        throw new UsageException("Codex CLI not found. Install Codex and sign in.");
    }

    private static ProcessStartInfo CreateStartInfo(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false, true),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(executable).Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // cmd.exe needs its own quoting, not ProcessStartInfo.ArgumentList's CRT quoting.
            // Reject expansion/quote characters rather than allowing paths or test args to become shell code.
            var words = new[] { executable }.Concat(arguments).Select(word =>
            {
                if (word.Any(character => character is '"' or '%' or '\r' or '\n' or '\0'))
                    throw new UsageException("Codex command contains unsupported shell characters.");
                return '"' + word + '"';
            });
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.Arguments = "/d /s /v:off /c \"" + string.Join(" ", words) + "\"";
        }
        else
        {
            foreach (var argument in arguments)
                info.ArgumentList.Add(argument);
        }
        return info;
    }

    private static async Task StopAsync(Process process, Task stderrDrain, CancellationTokenSource drainCancellation)
    {
        // Closing stdin is the app-server shutdown signal, including errors and caller cancellation.
        try { process.StandardInput.Close(); }
        catch (IOException) { }
        var stdoutDrain = DiscardAsync(process.StandardOutput.BaseStream, drainCancellation.Token);
        try
        {
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                catch (Win32Exception) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            await drainCancellation.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        }
    }

    private static async Task DiscardAsync(Stream stream, CancellationToken cancellationToken)
    {
        try { await stream.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private sealed class JsonLines(StreamReader reader)
    {
        private readonly char[] buffer = new char[4096];
        private int offset;
        private int count;

        public async Task<string> ReadAsync(CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset == count)
                {
                    count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    offset = 0;
                    if (count == 0)
                        throw new UsageException("Codex app-server closed before it returned usage.");
                }
                var newline = Array.IndexOf(buffer, '\n', offset, count - offset);
                var end = newline < 0 ? count : newline;
                if (line.Length + end - offset > ProviderJson.MaximumBytes)
                    throw new UsageException("Codex app-server response is too large.");
                line.Append(buffer, offset, end - offset);
                offset = end + (newline < 0 ? 0 : 1);
                if (newline >= 0)
                    return line.ToString();
            }
        }
    }
}

internal static class ProviderJson
{
    internal const int MaximumBytes = 1024 * 1024;

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return output.ToArray();
            if (output.Length + count > MaximumBytes)
                throw new UsageException("Provider data is too large.");
            output.Write(buffer, 0, count);
        }
    }

    internal static void RequireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new UsageException("The usage service returned an invalid response.");
    }

    internal static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static double Percent(JsonElement window, string property)
    {
        if (!window.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new UsageException("The usage service returned an invalid percentage.");
        return number;
    }
}
