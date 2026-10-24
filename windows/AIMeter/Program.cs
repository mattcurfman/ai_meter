// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using W = System.Windows;

namespace AIMeter;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        if (Option("--check") is { } output) return CheckAsync(output).GetAwaiter().GetResult();
        var preview = Option("--preview");
        var mutexName = preview is null ? @"Local\AIMeter.Windows" : $@"Local\AIMeter.Preview.{Environment.ProcessId}";
        using var mutex = new Mutex(true, mutexName, out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AIMeter.Windows.Show");
        if (!first && preview is null) { show.Set(); return 0; }
        var application = new W.Application { ShutdownMode = W.ShutdownMode.OnExplicitShutdown };
        using var host = new TrayHost(application, preview, Option("--theme"), Option("--capture"),
            double.TryParse(Option("--scale"), CultureInfo.InvariantCulture, out var scale) && scale is >= 1 and <= 3 ? scale : 2);
        var registration = preview is null ? ThreadPool.RegisterWaitForSingleObject(show,
            (_, _) => application.Dispatcher.BeginInvoke(host.OpenPanel), null, -1, false) : null;
        application.Startup += async (_, _) => await host.StartAsync(args.Contains("--show"));
        var exit = application.Run();
        registration?.Unregister(null);
        if (first) mutex.ReleaseMutex();
        return exit;
    }

    private static async Task<int> CheckAsync(string output)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var store = new UsageStore(new ClaudeUsageClient(), new CodexUsageClient());
        await store.CheckDueAsync(cancellation.Token);
        var states = store.Snapshot();
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        return states.Any(state => state.Error is not null || state.IsLoading) ? 1 : 0;
    }
}

internal sealed class TrayHost : IDisposable
{
    private readonly W.Application application;
    private readonly string? preview, capture;
    private readonly double captureScale;
    private readonly UsageStore? store;
    private readonly UsagePanel panel;
    private readonly Forms.NotifyIcon? tray;
    private readonly CancellationTokenSource cancellation = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(60) };
    private Task currentCheck = Task.CompletedTask;
    private bool stopping;

    public TrayHost(W.Application application, string? preview, string? theme, string? capture, double captureScale)
    {
        this.application = application;
        this.preview = preview;
        this.capture = capture;
        this.captureScale = captureScale;
        panel = new UsagePanel(StartupRegistration.Enabled, StartupRegistration.SetEnabled, Quit,
            theme == "dark" ? true : theme == "light" ? false : null, preview is not null);
        panel.Closing += (_, args) => { if (!stopping) { args.Cancel = true; panel.Hide(); } };
        panel.Deactivated += (_, _) => { if (preview is null && panel.ContextMenu?.IsOpen != true) panel.Hide(); };
        if (preview is null)
        {
            store = new UsageStore(new ClaudeUsageClient(), new CodexUsageClient());
            store.Changed += RefreshView;
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open AI Meter", null, (_, _) => OpenPanel());
            var startup = new Forms.ToolStripMenuItem("Start at Login") { CheckOnClick = true };
            menu.Opening += (_, _) => startup.Checked = StartupRegistration.Enabled();
            startup.Click += (_, _) =>
            {
                try { StartupRegistration.SetEnabled(startup.Checked); }
                catch { W.MessageBox.Show("Cannot change Start at Login.", "AI Meter"); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Quit AI Meter", null, (_, _) => Quit());
            tray = new Forms.NotifyIcon { Text = "AI Meter · checking usage", ContextMenuStrip = menu, Visible = false };
            tray.MouseClick += (_, args) =>
            {
                if (args.Button == Forms.MouseButtons.Left)
                {
                    if (panel.IsVisible) panel.Hide(); else OpenPanel();
                }
            };
            timer.Tick += async (_, _) =>
            {
                currentCheck = store.CheckDueAsync(cancellation.Token);
                await currentCheck;
                RefreshView();
            };
        }
    }

    public async Task StartAsync(bool show)
    {
        RefreshView();
        if (preview is not null)
        {
            OpenPanel();
        }
        else
        {
            timer.Start();
            if (show) OpenPanel();
            currentCheck = store!.CheckDueAsync(cancellation.Token);
            await currentCheck;
            if (show || capture is not null) OpenPanel();
        }
        if (capture is not null)
        {
            await Task.Delay(350);
            panel.UpdateLayout();
            var target = new RenderTargetBitmap((int)Math.Ceiling(panel.ActualWidth * captureScale),
                (int)Math.Ceiling(panel.ActualHeight * captureScale), 96 * captureScale, 96 * captureScale, PixelFormats.Pbgra32);
            target.Render(panel);
            var image = new PngBitmapEncoder();
            image.Frames.Add(BitmapFrame.Create(target));
            using (var stream = File.Create(capture)) image.Save(stream);
            Quit();
        }
    }

    private IReadOnlyList<ProviderStatus> States() => store?.Snapshot() ?? PreviewStates(preview!);

    private void RefreshView()
    {
        if (stopping) return;
        var states = States();
        panel.Update(states);
        if (tray is null) return;
        var summary = string.Join("  ·  ", states.Select(state =>
            $"{state.Provider}: {(state.Usage?.PeakPercent is { } percent ? $"{percent:0}% used" : state.IsLoading ? "checking" : state.Error is not null ? "unavailable" : "no limits")}"));
        tray.Text = summary.Length > 127 ? summary[..127] : summary;
        var peak = states.Where(state => state.Usage?.PeakPercent is not null).Select(state => state.Usage!.PeakPercent!.Value).DefaultIfEmpty(0).Max();
        var icon = NativeMethods.MakeIcon(peak, states.Any(state => state.Error is not null));
        var old = tray.Icon;
        tray.Icon = icon;
        old?.Dispose();
        tray.Visible = true;
        if (panel.IsVisible) { panel.UpdateLayout(); PositionPanel(); }
    }

    public void OpenPanel()
    {
        if (stopping) return;
        panel.Update(States());
        panel.Show();
        panel.UpdateLayout();
        PositionPanel();
        panel.Activate();
    }

    private void PositionPanel()
    {
        var area = Forms.Screen.FromPoint(Forms.Control.MousePosition).WorkingArea;
        var handle = new WindowInteropHelper(panel).Handle;
        NativeMethods.GetWindowRect(handle, out var bounds);
        NativeMethods.SetWindowPos(handle, new IntPtr(-1),
            Math.Max(area.Left, area.Right - (bounds.Right - bounds.Left) - 12),
            Math.Max(area.Top, area.Bottom - (bounds.Bottom - bounds.Top) - 12), 0, 0, 0x0011);
    }

    private async void Quit()
    {
        if (stopping) return;
        stopping = true;
        timer.Stop();
        cancellation.Cancel();
        try { await currentCheck; } catch (OperationCanceledException) { }
        panel.Close();
        application.Shutdown();
    }

    public void Dispose()
    {
        timer.Stop();
        cancellation.Cancel();
        if (tray is not null)
        {
            tray.Visible = false;
            tray.Icon?.Dispose();
            tray.ContextMenuStrip?.Dispose();
            tray.Dispose();
        }
        cancellation.Dispose();
    }

    private static ProviderStatus[] PreviewStates(string scenario)
    {
        var now = DateTimeOffset.UtcNow;
        if (scenario == "loading") return [new(Provider.Claude), new(Provider.Codex)];
        if (scenario == "unavailable") return
        [
            new(Provider.Claude, Error: "The usage service returned HTTP 429.", CheckedAt: now, NextAttemptAt: now.AddMinutes(8)),
            new(Provider.Codex, new("Pro", []), CheckedAt: now)
        ];
        var warnings = scenario == "warnings";
        return
        [
            new(Provider.Claude, new("Max", [new("Session (5 hr)", warnings ? 70 : 8, now.AddHours(3)),
                new("Weekly", warnings ? 90 : 25, now.AddDays(5))]), CheckedAt: now),
            new(Provider.Codex, new("Pro", [new("Session (5 hr)", warnings ? 0 : 42, now.AddHours(2)),
                new("Weekly", warnings ? 100 : 58, now.AddDays(6))]), CheckedAt: now)
        ];
    }
}

internal static class StartupRegistration
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("AIMeter") as string == Command;
    }
    private static string Command => $"\"{Environment.ProcessPath}\"";
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (enabled) key.SetValue("AIMeter", Command);
        else key.DeleteValue("AIMeter", false);
    }
}
