// SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using D = System.Drawing;

namespace AIMeter;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    internal static D.Icon MakeIcon(double percent, bool error)
    {
        using var bitmap = new D.Bitmap(32, 32);
        using var graphics = D.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = D.Drawing2D.SmoothingMode.AntiAlias;
        using var track = new D.Pen(D.Color.FromArgb(140, 140, 145), 5);
        using var fill = new D.Pen(D.ColorTranslator.FromHtml(UsagePanel.MeterColor(percent)), 5);
        graphics.DrawArc(track, 5, 6, 22, 22, 140, 260);
        if (percent > 0) graphics.DrawArc(fill, 5, 6, 22, 22, 140, (float)(260 * percent / 100));
        using var needle = new D.Pen(D.Color.FromArgb(128, 128, 132), 2);
        var angle = (140 + 260 * percent / 100) * Math.PI / 180;
        graphics.DrawLine(needle, 16, 17, 16 + (float)(8 * Math.Cos(angle)), 17 + (float)(8 * Math.Sin(angle)));
        if (error)
        {
            using var warning = new D.SolidBrush(D.Color.FromArgb(245, 153, 41));
            graphics.FillEllipse(warning, 21, 21, 10, 10);
        }
        var handle = bitmap.GetHicon();
        try { using var source = D.Icon.FromHandle(handle); return (D.Icon)source.Clone(); }
        finally { DestroyIcon(handle); }
    }
}
