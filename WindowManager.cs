using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;

namespace BorderlessGameWindow
{
    internal sealed class ManagedWindow
    {
        internal readonly IntPtr Handle;
        internal readonly string Path;
        private readonly int processId;
        private readonly uint threadId;
        private readonly string windowClass;
        private readonly Rectangle originalBounds;
        private readonly Native.Placement originalPlacement;
        private readonly uint originalStyle, originalExStyle;
        private int? borderColor, corners;
        private Size requested;
        private Rectangle lastTarget;
        internal bool PendingRestore;
        internal bool Changed;

        internal ManagedWindow(IntPtr handle, int pid, string path)
        {
            Handle = handle;
            Path = path;
            threadId = Native.GetWindowThreadProcessId(handle, out processId);
            if (processId != pid || threadId == 0) throw new Win32Exception(1400);
            windowClass = Native.ClassName(handle);
            originalBounds = Native.Bounds(handle);
            originalPlacement = Native.ReadPlacement(handle);
            originalStyle = Native.ReadStyle(handle, Native.Style);
            originalExStyle = Native.ReadStyle(handle, Native.ExStyle);
            requested = Native.ClientSize(handle);
        }

        internal bool StillExists()
        {
            int pid;
            return Native.GetWindowThreadProcessId(Handle, out pid) == threadId &&
                pid == processId && Native.ClassName(Handle) == windowClass;
        }

        internal static Rectangle Centered(Size client, Rectangle monitor)
        {
            double scale = Math.Min(1.0, Math.Min((double)monitor.Width / client.Width,
                                                   (double)monitor.Height / client.Height));
            int width = Math.Max(1, (int)Math.Round(client.Width * scale));
            int height = Math.Max(1, (int)Math.Round(client.Height * scale));
            return new Rectangle(monitor.Left + (monitor.Width - width) / 2,
                                 monitor.Top + (monitor.Height - height) / 2, width, height);
        }

        private int? SetAppearance(int attribute, int desired)
        {
            int previous;
            if (Native.DwmGetWindowAttribute(Handle, attribute, out previous, 4) == 0 &&
                previous != desired && Native.DwmSetWindowAttribute(Handle, attribute, ref desired, 4) == 0)
                return previous;
            return null;
        }
        private void RestoreAppearance(int attribute, ref int? saved)
        {
            if (!saved.HasValue) return;
            int value = saved.Value;
            int result = Native.DwmSetWindowAttribute(Handle, attribute, ref value, 4);
            if (result < 0) System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
            saved = null;
        }

        internal string Apply()
        {
            Rectangle bounds = Native.Bounds(Handle);
            Rectangle monitor = Native.MonitorBounds(Handle);
            Size client = Native.ClientSize(Handle);
            if (client.Width <= 0 || client.Height <= 0) return "Waiting for game window";
            uint style = Native.ReadStyle(Handle, Native.Style);
            uint exStyle = Native.ReadStyle(Handle, Native.ExStyle);
            uint cleanStyle = (style & ~Native.Frame) | Native.Popup;
            uint cleanExStyle = exStyle & ~Native.Edges;
            bool newFrame = style != cleanStyle || exStyle != cleanExStyle;

            // A game can re-add its frame without changing its outer bounds.
            // In that case the temporarily smaller client is not a resolution change.
            if (lastTarget.IsEmpty || (client != lastTarget.Size && !(newFrame && bounds.Size == lastTarget.Size)))
                requested = client;
            Rectangle target = Centered(requested, monitor);
            if (newFrame || bounds != target)
            {
                if (style != cleanStyle)
                {
                    Native.WriteStyle(Handle, Native.Style, cleanStyle);
                    Changed = true;
                }
                if (exStyle != cleanExStyle)
                {
                    Native.WriteStyle(Handle, Native.ExStyle, cleanExStyle);
                    Changed = true;
                }
                Native.Position(Handle, target, newFrame ? Native.FrameChanged : 0);
                Changed = true;
                if (Native.ClientSize(Handle) != target.Size || Native.Bounds(Handle) != target)
                    throw new InvalidOperationException("Game declined the requested window size or position");
            }
            if (lastTarget.IsEmpty && Changed)
            {
                borderColor = SetAppearance(34, -2); // DWMWA_COLOR_NONE on Windows 11
                corners = SetAppearance(33, 1);      // DWMWCP_DONOTROUND
            }
            lastTarget = target;
            string label = target.Size == requested ? "Borderless" : "Fitted to monitor";
            return String.Format("{0}  ·  {1} × {2}", label, target.Width, target.Height);
        }

        internal void Restore()
        {
            PendingRestore = true;
            if (!Changed || !StillExists()) return;
            if (Native.IsHungAppWindow(Handle)) throw new InvalidOperationException("The game is not responding");
            uint style = Native.ReadStyle(Handle, Native.Style);
            uint exStyle = Native.ReadStyle(Handle, Native.ExStyle);
            uint mask = Native.Frame | Native.Popup;
            uint restoredStyle = (style & ~mask) | (originalStyle & mask);
            uint restoredExStyle = (exStyle & ~Native.Edges) | (originalExStyle & Native.Edges);
            if (restoredStyle != style) Native.WriteStyle(Handle, Native.Style, restoredStyle);
            if (restoredExStyle != exStyle) Native.WriteStyle(Handle, Native.ExStyle, restoredExStyle);
            RestoreAppearance(34, ref borderColor);
            RestoreAppearance(33, ref corners);
            if (Native.IsIconic(Handle) || Native.IsZoomed(Handle))
            {
                Native.Placement current = Native.ReadPlacement(Handle);
                current.Normal = originalPlacement.Normal;
                Native.Check(Native.SetWindowPlacement(Handle, ref current));
                Native.Position(Handle, Rectangle.Empty, Native.NoMove | Native.NoSize | Native.FrameChanged);
            }
            else
            {
                Native.Position(Handle, originalBounds, Native.FrameChanged);
            }
            Changed = false;
        }
    }

    internal sealed class WindowManager
    {
        private sealed class Candidate
        {
            internal IntPtr Handle;
            internal string Path;
            internal long Area;
        }
        private readonly Dictionary<IntPtr, ManagedWindow> managed = new Dictionary<IntPtr, ManagedWindow>();
        private sealed class Retry
        {
            internal DateTime After;
            internal string Message;
        }
        private readonly Dictionary<IntPtr, Retry> retries = new Dictionary<IntPtr, Retry>();
        internal static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

        private static string Describe(Exception ex)
        {
            Win32Exception error = ex as Win32Exception;
            if (error == null) return ex.Message;
            if (error.NativeErrorCode == 5) return "Windows denied access to this window";
            if (error.NativeErrorCode == 1400) return "Waiting for game window";
            return "Windows error " + error.NativeErrorCode;
        }

        // Return the largest visible, unowned application window per matching process.
        private static Dictionary<int, Candidate> FindWindows(Dictionary<string, string> status)
        {
            Dictionary<int, string> matched = new Dictionary<int, string>();
            Dictionary<int, Candidate> windows = new Dictionary<int, Candidate>();
            foreach (var group in status.Keys.ToArray().GroupBy(p => Path.GetFileNameWithoutExtension(p), Paths))
            {
                foreach (Process process in Process.GetProcessesByName(group.Key))
                {
                    using (process)
                    {
                        int pid = process.Id;
                        string path = Native.ImagePath(pid);
                        if (path == null)
                        {
                            foreach (string entry in group)
                                status[entry] = "Cannot verify executable path";
                        }
                        else if (status.ContainsKey(path))
                        {
                            matched[pid] = path;
                            status[path] = "Waiting for game window";
                        }
                    }
                }
            }
            if (matched.Count == 0) return windows;
            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr unused)
            {
                int pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                string path;
                if (!matched.TryGetValue(pid, out path) || !Native.IsWindowVisible(hwnd) ||
                    Native.GetWindow(hwnd, 4) != IntPtr.Zero) return true;
                try
                {
                    if ((Native.ReadStyle(hwnd, Native.ExStyle) & Native.ToolWindow) != 0 ||
                        (Native.ReadStyle(hwnd, Native.Style) & Native.Child) != 0 ||
                        Native.ClassName(hwnd) == "#32770") return true;
                    int cloaked;
                    if (Native.DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
                    Size client = Native.ClientSize(hwnd);
                    Candidate previous;
                    long area = (long)client.Width * client.Height;
                    if (!windows.TryGetValue(pid, out previous) || area > previous.Area)
                        windows[pid] = new Candidate { Handle = hwnd, Path = path, Area = area };
                }
                catch (Win32Exception ex) { status[path] = Describe(ex); }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        internal Dictionary<string, string> Scan(IEnumerable<string> games)
        {
            var status = games.ToDictionary(p => p, p => "Waiting for game", Paths);
            foreach (ManagedWindow item in managed.Values.ToArray())
                if (!item.StillExists()) managed.Remove(item.Handle);
            foreach (IntPtr hwnd in retries.Where(p => DateTime.UtcNow >= p.Value.After).Select(p => p.Key).ToArray())
                retries.Remove(hwnd);
            foreach (var pair in FindWindows(status))
            {
                Candidate game = pair.Value;
                IntPtr hwnd = game.Handle;
                ManagedWindow item = null;
                try
                {
                    Retry retry;
                    if (retries.TryGetValue(hwnd, out retry))
                        status[game.Path] = retry.Message;
                    else if (Native.IsHungAppWindow(hwnd)) status[game.Path] = "Waiting for game to respond";
                    else if (Native.IsIconic(hwnd)) status[game.Path] = "Game minimized";
                    else if (Native.IsZoomed(hwnd)) status[game.Path] = "Restore the window to its windowed size";
                    else
                    {
                        if (!managed.TryGetValue(hwnd, out item))
                        {
                            if (Native.Bounds(hwnd) == Native.MonitorBounds(hwnd) &&
                                (Native.ReadStyle(hwnd, Native.Style) & Native.Frame) == 0)
                            {
                                status[game.Path] = "Already fullscreen";
                                continue;
                            }
                            item = new ManagedWindow(hwnd, pair.Key, game.Path);
                            managed.Add(hwnd, item);
                        }
                        if (item.PendingRestore)
                        {
                            item.Restore();
                            managed.Remove(hwnd);
                            continue;
                        }
                        status[game.Path] = item.Apply();
                    }
                }
                catch (Exception ex)
                {
                    status[game.Path] = Describe(ex);
                    if (item != null)
                    {
                        try { item.Restore(); managed.Remove(hwnd); }
                        catch (Exception restoreError) { status[game.Path] = "Restore pending · " + Describe(restoreError); }
                    }
                    retries[hwnd] = new Retry { After = DateTime.UtcNow.AddSeconds(8), Message = status[game.Path] };
                }
            }
            return status;
        }

        internal List<string> Restore(string path)
        {
            List<string> errors = new List<string>();
            foreach (ManagedWindow item in managed.Values.ToArray())
            {
                if (path != null && !Paths.Equals(path, item.Path)) continue;
                try { item.Restore(); managed.Remove(item.Handle); }
                catch (Exception ex) { errors.Add(Path.GetFileName(item.Path) + " — " + Describe(ex)); }
            }
            return errors;
        }
    }
}
