// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using System.Windows.Automation;
using W = System.Windows;
using C = System.Windows.Controls;
using M = System.Windows.Media;

namespace AIMeter;

public sealed class UsagePanel : W.Window
{
    private readonly Func<bool> launchEnabled;
    private readonly Action<bool> setLaunch;
    private readonly Action quit;
    private readonly bool? darkOverride;
    private readonly bool preview;
    private IReadOnlyList<ProviderStatus> statuses = [];
    private M.Brush panel = M.Brushes.White, text = M.Brushes.Black, muted = M.Brushes.Gray;
    private M.Brush track = M.Brushes.LightGray, rule = M.Brushes.LightGray;

    public UsagePanel(Func<bool> launchEnabled, Action<bool> setLaunch, Action quit,
        bool? darkOverride = null, bool preview = false)
    {
        this.launchEnabled = launchEnabled;
        this.setLaunch = setLaunch;
        this.quit = quit;
        this.darkOverride = darkOverride;
        this.preview = preview;
        Title = "AI Meter";
        Width = 336;
        SizeToContent = W.SizeToContent.Height;
        WindowStyle = W.WindowStyle.None;
        ResizeMode = W.ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = M.Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = new M.FontFamily("Segoe UI");
        FontSize = 13;
        AutomationProperties.SetName(this, "AI Meter usage");
        PreviewKeyDown += (_, args) => { if (args.Key == System.Windows.Input.Key.Escape) Hide(); };
    }

    public void Update(IReadOnlyList<ProviderStatus> values)
    {
        statuses = values;
        var dark = darkOverride ?? IsDark();
        if (W.SystemParameters.HighContrast)
        {
            panel = W.SystemColors.WindowBrush;
            text = W.SystemColors.WindowTextBrush;
            muted = text;
            track = W.SystemColors.ControlDarkBrush;
            rule = track;
        }
        else
        {
            panel = Brush(dark ? "#292929" : "#FAFAFA");
            text = Brush(dark ? "#F0F0F0" : "#202020");
            muted = Brush(dark ? "#B6B6BA" : "#636369");
            track = Brush(dark ? "#464648" : "#E3E3E6");
            rule = Brush(dark ? "#414143" : "#DEDEE2");
        }
        Foreground = text;
        var content = new C.StackPanel();
        foreach (var state in statuses)
        {
            if (content.Children.Count > 0) content.Children.Add(Separator());
            content.Children.Add(Section(state));
        }
        content.Children.Add(Separator());
        content.Children.Add(Footer());
        Content = new C.Border
        {
            Background = panel, BorderBrush = rule, BorderThickness = new W.Thickness(1),
            CornerRadius = new W.CornerRadius(10), Padding = new W.Thickness(0),
            Child = content, Margin = new W.Thickness(4)
        };
    }

    private C.StackPanel Section(ProviderStatus state)
    {
        var section = new C.StackPanel { Margin = new W.Thickness(16, 13, 16, 14) };
        var header = new C.DockPanel { LastChildFill = false, Margin = new W.Thickness(0, 0, 0, 10) };
        var link = FlatButton("↗", $"Open {state.Provider} usage page");
        link.FontSize = 19;
        link.Click += (_, _) => OpenDashboard(state.Provider);
        C.DockPanel.SetDock(link, C.Dock.Right);
        header.Children.Add(link);
        header.Children.Add(Label(state.Provider.ToString(), 14, true));
        if (!string.IsNullOrWhiteSpace(state.Usage?.Plan))
        {
            var plan = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(state.Usage.Plan);
            header.Children.Add(new C.Border
            {
                Background = track, CornerRadius = new W.CornerRadius(7),
                Padding = new W.Thickness(7, 1, 7, 2), Margin = new W.Thickness(8, 1, 0, 0),
                VerticalAlignment = W.VerticalAlignment.Center,
                Child = Label(plan, 11, false, muted)
            });
        }
        section.Children.Add(header);
        if (state.IsLoading) section.Children.Add(Label("Checking usage…", 12, false, muted));
        else if (state.Error is not null)
        {
            var message = Label("⚠  " + state.Error, 12, false, muted);
            message.TextWrapping = W.TextWrapping.Wrap;
            section.Children.Add(message);
            if (state.NextAttemptAt is { } next)
                section.Children.Add(Label("Next check " + Relative(next), 11, false, muted, new W.Thickness(0, 7, 0, 0)));
        }
        else if (state.Usage?.Windows.Count == 0)
            section.Children.Add(Label("No usage limits reported.", 12, false, muted));
        else if (state.Usage is { } usage)
            foreach (var window in usage.Windows) section.Children.Add(WindowRow(state.Provider, window));
        return section;
    }

    private C.StackPanel WindowRow(Provider provider, UsageWindow window)
    {
        var row = new C.StackPanel { Margin = new W.Thickness(0, 0, 0, 10) };
        AutomationProperties.SetName(row, $"{provider}, {window.Title}, {window.PercentUsed:0}% used");
        var heading = new C.DockPanel { Margin = new W.Thickness(0, 0, 0, 5) };
        var percent = Label($"{Math.Round(window.PercentUsed, MidpointRounding.AwayFromZero):0}%", 13, true);
        AutomationProperties.SetName(percent, $"{window.PercentUsed:0}% used");
        C.DockPanel.SetDock(percent, C.Dock.Right);
        heading.Children.Add(percent);
        heading.Children.Add(Label(window.Title, 13));
        row.Children.Add(heading);
        var bar = new C.Grid { Height = 6, Margin = new W.Thickness(0, 0, 0, 5) };
        bar.Children.Add(new C.Border { Background = track, CornerRadius = new W.CornerRadius(3) });
        var fill = new C.Border
        {
            Background = W.SystemParameters.HighContrast ? W.SystemColors.HighlightBrush : Brush(MeterColor(window.PercentUsed)),
            HorizontalAlignment = W.HorizontalAlignment.Left, CornerRadius = new W.CornerRadius(3)
        };
        bar.SizeChanged += (_, _) => fill.Width = window.PercentUsed <= 0 ? 0 :
            Math.Min(bar.ActualWidth, Math.Max(6, bar.ActualWidth * window.PercentUsed / 100));
        bar.Children.Add(fill);
        row.Children.Add(bar);
        if (window.ResetsAt is { } reset)
        {
            var resetLabel = Label("Resets " + Relative(reset), 11, false, muted);
            resetLabel.ToolTip = reset.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            row.Children.Add(resetLabel);
        }
        return row;
    }

    private C.DockPanel Footer()
    {
        var footer = new C.DockPanel { Margin = new W.Thickness(16, 7, 12, 8) };
        var more = FlatButton("···", "More options");
        more.FontSize = 18;
        more.Click += (_, _) =>
        {
            var menu = new C.ContextMenu();
            var startup = new C.MenuItem { Header = "Start at Login", IsCheckable = true, IsChecked = launchEnabled() };
            startup.Click += (_, _) =>
            {
                try { setLaunch(startup.IsChecked); }
                catch { W.MessageBox.Show("Cannot change Start at Login.", "AI Meter"); }
            };
            menu.Items.Add(startup);
            menu.Items.Add(new C.Separator());
            var exit = new C.MenuItem { Header = "Quit AI Meter" };
            exit.Click += (_, _) => quit();
            menu.Items.Add(exit);
            menu.PlacementTarget = more;
            ContextMenu = menu;
            menu.IsOpen = true;
        };
        C.DockPanel.SetDock(more, C.Dock.Right);
        footer.Children.Add(more);
        var latest = statuses.Max(state => state.CheckedAt);
        var checkedText = preview ? "Preview · sample data" : latest is null ? "Checking usage…" :
            "Checked " + RelativePast(latest.Value);
        var checkedLabel = Label(checkedText, 11, false, muted);
        if (!preview && latest is { } checkedAt)
        {
            checkedLabel.ToolTip = checkedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture);
            AutomationProperties.SetHelpText(checkedLabel, checkedAt.ToString("O", CultureInfo.InvariantCulture));
        }
        footer.Children.Add(checkedLabel);
        return footer;
    }

    private C.Button FlatButton(string content, string name)
    {
        var button = new C.Button
        {
            Content = content, ToolTip = name, Width = 28, Height = 24,
            Background = M.Brushes.Transparent, Foreground = muted,
            BorderThickness = new W.Thickness(0), Padding = new W.Thickness(0),
            VerticalAlignment = W.VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        AutomationProperties.SetName(button, name);
        var border = new W.FrameworkElementFactory(typeof(C.Border));
        border.SetValue(C.Border.CornerRadiusProperty, new W.CornerRadius(4));
        border.SetValue(C.Border.BackgroundProperty, new W.TemplateBindingExtension(C.Button.BackgroundProperty));
        var presenter = new W.FrameworkElementFactory(typeof(C.ContentPresenter));
        presenter.SetValue(W.FrameworkElement.HorizontalAlignmentProperty, W.HorizontalAlignment.Center);
        presenter.SetValue(W.FrameworkElement.VerticalAlignmentProperty, W.VerticalAlignment.Center);
        border.AppendChild(presenter);
        var template = new C.ControlTemplate(typeof(C.Button)) { VisualTree = border };
        var hover = new W.Trigger { Property = C.Button.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new W.Setter(C.Button.BackgroundProperty, track));
        template.Triggers.Add(hover);
        button.Template = template;
        return button;
    }

    private void OpenDashboard(Provider provider)
    {
        var address = provider == Provider.Claude ? "https://claude.ai/settings/usage" : "https://chatgpt.com/codex/settings/usage";
        try { Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); Hide(); }
        catch { W.MessageBox.Show("Cannot open the usage page. Check your default browser.", "AI Meter"); }
    }

    private C.TextBlock Label(string value, double size, bool bold = false, M.Brush? color = null, W.Thickness? margin = null) => new()
    {
        Text = value, FontSize = size, FontWeight = bold ? W.FontWeights.SemiBold : W.FontWeights.Normal,
        Foreground = color ?? text, VerticalAlignment = W.VerticalAlignment.Center,
        Margin = margin ?? new W.Thickness(0)
    };

    private C.Border Separator() => new() { Height = 1, Background = rule };
    private static M.Brush Brush(string value) => (M.Brush)new M.BrushConverter().ConvertFromString(value)!;
    public static string MeterColor(double percent) => percent >= 90 ? "#E64045" : percent >= 70 ? "#F59929" : "#1685F8";

    public static string Relative(DateTimeOffset target)
    {
        var minutes = Math.Max(0, (long)Math.Ceiling((target - DateTimeOffset.UtcNow).TotalMinutes));
        if (minutes == 0) return "now";
        if (minutes < 60) return $"in {minutes} min";
        if (minutes < 1440) return $"in {minutes / 60} hr{(minutes % 60 == 0 ? "" : $" {minutes % 60} min")}";
        var days = (int)Math.Ceiling(minutes / 1440d);
        return $"in {days} day{(days == 1 ? "" : "s")}";
    }

    private static string RelativePast(DateTimeOffset target)
    {
        var minutes = Math.Max(0, (int)(DateTimeOffset.UtcNow - target).TotalMinutes);
        return minutes == 0 ? "just now" : $"{minutes} min ago";
    }

    private static bool IsDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
