using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace BorderlessGameWindow
{
    internal sealed class SettingsForm : Form
    {
        private readonly List<Game> games;
        private readonly WindowManager windows;
        private readonly ListView list = new ListView();
        private readonly TextBox path = new TextBox();
        private readonly Button remove = MakeButton("Remove");
        private readonly CheckBox startup = new CheckBox();
        private readonly CheckBox administrator = new CheckBox();

        internal SettingsForm(List<Game> entries, WindowManager manager, Action exit, Action restart)
        {
            games = entries;
            windows = manager;
            Text = "Borderless Game Window";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10);
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            ClientSize = new Size(688, 535);
            MinimumSize = new Size(625, 535);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 10,
                Padding = new Padding(24, 18, 24, 16) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 10; i++) layout.RowStyles.Add(i == 2 ?
                new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);
            var heading = new Label { Text = "Watched games", AutoSize = true,
                Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Margin = Padding.Empty };
            layout.Controls.Add(heading, 0, 0);
            layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 14),
                Text = "Use Windowed mode in each game. Its play area stays centered on its monitor." }, 0, 1);

            list.Dock = DockStyle.Fill;
            list.Margin = Padding.Empty;
            list.AccessibleName = "Watched games";
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.ShowItemToolTips = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.Columns.Add("Game", 215);
            list.Columns.Add("Window status", 390);
            list.Resize += delegate
            {
                int width = Math.Max(180, list.ClientSize.Width * 36 / 100);
                list.Columns[0].Width = width;
                list.Columns[1].Width = Math.Max(110, list.ClientSize.Width - width - SystemInformation.VerticalScrollBarWidth - 2);
            };
            list.SelectedIndexChanged += delegate { UpdateSelection(); };
            layout.Controls.Add(list, 0, 2);

            path.ReadOnly = true;
            path.BorderStyle = BorderStyle.None;
            path.BackColor = BackColor;
            path.ForeColor = SystemColors.GrayText;
            path.Dock = DockStyle.Fill;
            path.AccessibleName = "Selected executable path";
            path.Margin = new Padding(1, 7, 1, 7);
            layout.Controls.Add(path, 0, 3);

            var actions = Row();
            var add = MakeButton("Add game…");
            add.Click += delegate { AddGame(); };
            remove.Click += delegate { RemoveGame(); };
            actions.Controls.Add(add);
            actions.Controls.Add(remove);
            layout.Controls.Add(actions, 0, 4);

            var divider = new Panel { Height = 1, BackColor = SystemColors.ControlLight,
                Dock = DockStyle.Top, Margin = new Padding(0, 14, 0, 12) };
            layout.Controls.Add(divider, 0, 5);
            startup.Text = "Start in the tray when I sign in";
            startup.AutoSize = true;
            startup.Margin = new Padding(0, 0, 0, 6);
            startup.Click += delegate
            {
                try
                {
                    if (administrator.Checked) LaunchSettings.Configure(true, startup.Checked);
                    else Settings.SetRegularStartup(startup.Checked);
                }
                catch (Exception ex) { Error(ex.Message); }
                RefreshLaunchSettings();
            };
            layout.Controls.Add(startup, 0, 6);
            administrator.Text = "Use administrator privileges";
            administrator.AutoSize = true;
            administrator.Margin = new Padding(0, 0, 0, 2);
            administrator.Click += delegate
            {
                bool changed = false;
                try { changed = LaunchSettings.Configure(administrator.Checked, startup.Checked); }
                catch (Exception ex) { Error(ex.Message); }
                RefreshLaunchSettings();
                if (changed && administrator.Checked != LaunchSettings.IsElevated()) restart();
            };
            layout.Controls.Add(administrator, 0, 7);
            layout.Controls.Add(new Label {
                Text = "Enable this to remove the title bar and border from games that run as administrator, such as Arena Breakout: Infinite. Changing this setting restarts the app in the tray.",
                AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(21, 0, 0, 7)
            }, 0, 8);
            RefreshLaunchSettings();

            var footer = Row();
            footer.FlowDirection = FlowDirection.RightToLeft;
            footer.Dock = DockStyle.Fill;
            var hide = MakeButton("Hide to tray");
            hide.Margin = new Padding(6, 4, 0, 0);
            hide.Click += delegate { Hide(); };
            var quit = MakeButton("Restore windows and exit");
            quit.Click += delegate { exit(); };
            footer.Controls.Add(hide);
            footer.Controls.Add(quit);
            layout.Controls.Add(footer, 0, 9);
            foreach (Game game in games) AddRow(game);
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            UpdateSelection();
        }

        private void RefreshLaunchSettings()
        {
            LaunchSettings current = LaunchSettings.Read();
            startup.Checked = current.AtSignIn;
            administrator.Checked = current.UseAdministrator;
        }

        private static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        }
        private static Button MakeButton(string text)
        {
            return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 2, 8, 2), Margin = new Padding(0, 4, 6, 0),
                UseVisualStyleBackColor = true };
        }
        private void AddRow(Game game)
        {
            var item = new ListViewItem(game.Name) { Tag = game, ToolTipText = game.Path };
            item.SubItems.Add("Waiting for game");
            list.Items.Add(item);
        }
        private void UpdateSelection()
        {
            remove.Enabled = list.SelectedItems.Count > 0;
            path.Text = remove.Enabled ? ((Game)list.SelectedItems[0].Tag).Path : "";
            path.Select(0, 0);
        }
        private void Error(string message)
        {
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        private void AddGame()
        {
            using (var dialog = new OpenFileDialog { Filter = "Windows applications (*.exe)|*.exe",
                Title = "Choose a game executable", CheckFileExists = true, RestoreDirectory = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string file = Path.GetFullPath(dialog.FileName);
                if (WindowManager.Paths.Equals(file, Application.ExecutablePath))
                {
                    Error("Choose a game executable.");
                    return;
                }
                foreach (ListViewItem existing in list.Items)
                    if (WindowManager.Paths.Equals(((Game)existing.Tag).Path, file))
                    {
                        existing.Selected = true;
                        existing.EnsureVisible();
                        return;
                    }
                Game game = new Game(Path.GetFileNameWithoutExtension(file), file);
                try { Settings.Save(games.Concat(new[] { game })); }
                catch (Exception ex) { Error(ex.Message); return; }
                games.Add(game);
                AddRow(game);
                list.Items[list.Items.Count - 1].Selected = true;
                list.Items[list.Items.Count - 1].EnsureVisible();
            }
        }
        private void RemoveGame()
        {
            if (list.SelectedItems.Count == 0) return;
            ListViewItem item = list.SelectedItems[0];
            Game game = (Game)item.Tag;
            List<string> errors = windows.Restore(game.Path);
            if (errors.Count > 0) { Error(String.Join(Environment.NewLine, errors)); return; }
            try { Settings.Save(games.Where(g => g != game)); }
            catch (Exception ex) { Error(ex.Message); return; }
            games.Remove(game);
            item.Remove();
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            UpdateSelection();
        }
        internal void UpdateStatus(Dictionary<string, string> status)
        {
            foreach (ListViewItem item in list.Items)
            {
                string text;
                if (status.TryGetValue(((Game)item.Tag).Path, out text) && item.SubItems[1].Text != text)
                    item.SubItems[1].Text = text;
            }
        }
        internal void ShowScanError(string message)
        {
            foreach (ListViewItem item in list.Items) item.SubItems[1].Text = "Scan failed · " + message;
        }
    }
}
