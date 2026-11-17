using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("Borderless Game Window")]
[assembly: AssemblyProduct("Borderless Game Window")]
[assembly: AssemblyDescription("Center windowed games and restore their original frames.")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace BorderlessGameWindow
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (args.Length > 0 && args[0] == LaunchSettings.ConfigureArgument)
                {
                    LaunchSettings.ConfigureFromArguments(args);
                    return 0;
                }
                bool scheduled = args.Contains(LaunchSettings.TaskArgument);
                bool background = args.Contains("--background");
                if (!scheduled && !LaunchSettings.IsElevated() && LaunchSettings.Read().UseAdministrator)
                {
                    LaunchSettings.StartTask();
                    return 0;
                }
                bool first, restart;
                using (var signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BorderlessGameWindow.Open.v1"))
                using (var mutex = new Mutex(true, @"Local\BorderlessGameWindow.Instance.v1", out first))
                {
                    if (!first) { if (!background) signal.Set(); return 0; }
                    try
                    {
                        using (var app = new TrayApplication(signal, background || scheduled))
                        {
                            Application.Run(app);
                            restart = app.RestartWithNewPrivileges;
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                if (restart) LaunchSettings.StartTask();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Borderless Game Window", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class TrayApplication : ApplicationContext
    {
        private readonly List<Game> games;
        private readonly WindowManager windows = new WindowManager();
        private readonly SettingsForm settings;
        private readonly NotifyIcon tray;
        private readonly ContextMenuStrip menu;
        private readonly Timer timer = new Timer { Interval = 1200 };
        private readonly EventWaitHandle openSettings;
        private readonly Icon icon;
        private bool exiting;
        internal bool RestartWithNewPrivileges { get; private set; }

        internal TrayApplication(EventWaitHandle signal, bool background)
        {
            games = Settings.Load();
            openSettings = signal;
            icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            settings = new SettingsForm(games, windows, RequestExit, delegate
            {
                RestartWithNewPrivileges = true;
                RequestExit();
                if (!exiting) RestartWithNewPrivileges = false;
            });
            settings.Icon = icon;
            settings.FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    settings.Hide();
                }
                else if (!exiting) { exiting = true; timer.Stop(); windows.Restore(null); ExitThread(); }
            };
            // Receive session-end messages even when launched directly into the tray.
            IntPtr unused = settings.Handle;
            menu = new ContextMenuStrip();
            menu.Items.Add("Open settings", null, delegate { ShowSettings(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Restore windows and exit", null, delegate { RequestExit(); });
            tray = new NotifyIcon { Icon = icon, Text = "Borderless Game Window", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowSettings(); };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            if (!background) ShowSettings();
            Tick();
        }

        private void ShowSettings()
        {
            settings.Show();
            // A launcher can supply SW_HIDE for the first ShowWindow call.
            if (!Native.IsWindowVisible(settings.Handle)) Native.ShowWindow(settings.Handle, 5);
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }
        private void Tick()
        {
            if (exiting) return;
            if (openSettings.WaitOne(0)) ShowSettings();
            try { settings.UpdateStatus(windows.Scan(games.Select(g => g.Path))); }
            catch (Exception ex) { settings.ShowScanError(ex.Message); }
        }
        private void RequestExit()
        {
            if (exiting) return;
            exiting = true;
            timer.Stop();
            List<string> errors = windows.Restore(null);
            if (errors.Count > 0 && MessageBox.Show("Windows could not restore the following windows.\n\n" +
                    String.Join("\n", errors) + "\n\nExit anyway?", "Restore windows", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                exiting = false;
                timer.Start();
                return;
            }
            ExitThread();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Stop();
                windows.Restore(null);
                timer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                settings.Dispose();
                icon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
