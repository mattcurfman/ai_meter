// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.ExceptionServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using W = System.Windows;
using C = System.Windows.Controls;

namespace AIMeter.Tests;

[TestClass]
public sealed class UsagePanelTests
{
    [TestMethod]
    public void PanelRendersUsedMetersAndReplacesFailedUsageWithAnError()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            UsagePanel? panel = null;
            try
            {
                panel = new UsagePanel(() => false, _ => { }, () => { }, darkOverride: true);
                panel.Update([
                    new(Provider.Claude, new("Max", [new("Session (5 hr)", 8, null), new("Weekly", 73, null)])),
                    new(Provider.Codex, new("Pro", [new("Weekly", 92, null)]))
                ]);
                panel.Show();
                panel.UpdateLayout();
                var image = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(panel);
                Assert.IsTrue(image.PixelHeight > 150);
                var labels = Descendants(panel).OfType<C.TextBlock>().Select(label => label.Text).ToArray();
                CollectionAssert.Contains(labels, "8%");
                CollectionAssert.Contains(labels, "73%");
                CollectionAssert.Contains(labels, "92%");
                var buttons = Descendants(panel).OfType<C.Button>().Select(AutomationProperties.GetName).ToArray();
                CollectionAssert.Contains(buttons, "Open Claude usage page");
                CollectionAssert.Contains(buttons, "Open Codex usage page");
                CollectionAssert.Contains(buttons, "More options");

                panel.Update([
                    new(Provider.Claude, Error: "Rate limited", CheckedAt: DateTimeOffset.UtcNow, NextAttemptAt: DateTimeOffset.UtcNow.AddMinutes(8)),
                    new(Provider.Codex, new("Pro", []))
                ]);
                panel.UpdateLayout();
                labels = Descendants(panel).OfType<C.TextBlock>().Select(label => label.Text).ToArray();
                Assert.IsTrue(labels.Any(label => label.Contains("Rate limited")));
                CollectionAssert.Contains(labels, "No usage limits reported.");
                Assert.IsFalse(labels.Any(label => label.EndsWith('%')));
            }
            catch (Exception error) { failure = error; }
            finally { panel?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(10)), "The panel did not finish rendering.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IEnumerable<W.DependencyObject> Descendants(W.DependencyObject root)
    {
        foreach (var child in W.LogicalTreeHelper.GetChildren(root).OfType<W.DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
