using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Overlay.Windows;
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    internal static void MakeOverlay(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr(style | 0x20 | 0x80000 | 0x8000000 | 0x80));
    }
    internal static bool Position(Window window, double offsetX, double offsetY)
    {
        foreach (var process in Process.GetProcessesByName("MTGA"))
        using (process)
        {
            try
            {
            var handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero || IsIconic(handle) || GetForegroundWindow() != handle) continue;
            if (!GetClientRect(handle, out var rect)) continue;
            var origin = new Point(); if (!ClientToScreen(handle, ref origin)) continue;
            double dpi = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
            int w = (int)Math.Ceiling(window.ActualWidth * dpi), h = (int)Math.Ceiling(window.ActualHeight * dpi);
            int x = origin.X + Math.Max(0, Math.Min((int)(offsetX * dpi), rect.Right - w));
            int y = origin.Y + Math.Max(0, rect.Bottom - h - (int)(offsetY * dpi));
            // Physical coordinates avoid mixed-DPI virtual-screen rounding. Never activate or steal input.
            SetWindowPos(new WindowInteropHelper(window).Handle, new IntPtr(-1), x, y, 0, 0, 0x10 | 0x1 | 0x40);
            return true;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { /* Arena exited between enumeration and inspection. */ }
        }
        return false;
    }
}
