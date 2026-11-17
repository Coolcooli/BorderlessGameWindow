using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BorderlessGameWindow
{
    internal static class Native
    {
        internal const int Style = -16, ExStyle = -20;
        internal const uint Frame = 0x00CF0000; // caption, thick frame, system menu and caption buttons
        internal const uint Popup = 0x80000000;
        internal const uint Edges = 0x00020301; // static, client, window and modal edges
        internal const uint ToolWindow = 0x80, Child = 0x40000000;
        internal const uint FrameChanged = 0x20, NoMove = 2, NoSize = 1;
        internal const uint Quiet = 0x214; // NOACTIVATE | NOZORDER | NOOWNERZORDER

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            public int Left, Top, Right, Bottom;
            public Rectangle Bounds { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } }
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Placement
        {
            public int Length, Flags, Show;
            public Point Min, Max;
            public Rect Normal;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MonitorInfo
        {
            public int Size;
            public Rect Monitor, Work;
            public int Flags;
        }

        internal delegate bool EnumProc(IntPtr hwnd, IntPtr data);
        [DllImport("user32")] internal static extern bool EnumWindows(EnumProc callback, IntPtr data);
        [DllImport("user32")] internal static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32")] internal static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32")] internal static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32")] internal static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32")] internal static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32", SetLastError=true)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32", SetLastError=true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32", SetLastError=true)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);
        [DllImport("user32", SetLastError=true)] internal static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);
        [DllImport("user32", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32", EntryPoint="GetWindowLongW", SetLastError=true)] private static extern uint GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32", EntryPoint="SetWindowLongW", SetLastError=true)] private static extern uint SetWindowLong(IntPtr hwnd, int index, uint value);
        [DllImport("user32")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("kernel32")] private static extern void SetLastError(uint error);
        [DllImport("kernel32", SetLastError=true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32", CharSet=CharSet.Unicode, SetLastError=true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
        [DllImport("dwmapi")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
        [DllImport("dwmapi")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static void Check(bool ok)
        {
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal static string ImagePath(int pid)
        {
            // PROCESS_QUERY_LIMITED_INFORMATION is sufficient for the executable path.
            using (SafeProcessHandle process = OpenProcess(0x1000, false, pid))
            {
                if (process.IsInvalid) return null;
                int capacity = 32768;
                StringBuilder path = new StringBuilder(capacity);
                return QueryFullProcessImageName(process, 0, path, ref capacity) ? path.ToString() : null;
            }
        }
        internal static string ClassName(IntPtr hwnd)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }
        internal static uint ReadStyle(IntPtr hwnd, int index)
        {
            SetLastError(0);
            uint value = GetWindowLong(hwnd, index);
            if (value == 0) Check(Marshal.GetLastWin32Error() == 0);
            return value;
        }
        internal static void WriteStyle(IntPtr hwnd, int index, uint value)
        {
            SetLastError(0);
            if (SetWindowLong(hwnd, index, value) == 0) Check(Marshal.GetLastWin32Error() == 0);
        }
        internal static Rectangle Bounds(IntPtr hwnd)
        {
            Rect rect;
            Check(GetWindowRect(hwnd, out rect));
            return rect.Bounds;
        }
        internal static Size ClientSize(IntPtr hwnd)
        {
            Rect rect;
            Check(GetClientRect(hwnd, out rect));
            return rect.Bounds.Size;
        }
        internal static Rectangle MonitorBounds(IntPtr hwnd)
        {
            MonitorInfo info = new MonitorInfo();
            info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            Check(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info));
            return info.Monitor.Bounds;
        }
        internal static Placement ReadPlacement(IntPtr hwnd)
        {
            Placement placement = new Placement();
            placement.Length = Marshal.SizeOf(typeof(Placement));
            Check(GetWindowPlacement(hwnd, ref placement));
            return placement;
        }
        internal static void Position(IntPtr hwnd, Rectangle bounds, uint flags)
        {
            Check(SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, Quiet | flags));
        }
    }
}
