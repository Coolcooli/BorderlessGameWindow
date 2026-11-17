using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml.Linq;
using Microsoft.Win32;

namespace BorderlessGameWindow
{
    internal sealed class Game
    {
        internal readonly string Name;
        internal readonly string Path;
        internal Game(string name, string path) { Name = name; Path = path; }
    }

    internal static class Settings
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BorderlessGameWindow");
        private static readonly string FilePath = Path.Combine(Folder, "games.xml");
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunName = "BorderlessGameWindow";

        internal static List<Game> Load()
        {
            if (!File.Exists(FilePath))
            {
                List<Game> defaults = new List<Game>();
                if (!LaunchSettings.Read().UseAdministrator) SetRegularStartup(true);
                Save(defaults);
                return defaults;
            }
            XElement root = XElement.Load(FilePath);
            if (root.Name != "games") throw new InvalidDataException("Invalid game list in " + FilePath);
            var games = new List<Game>();
            foreach (XElement entry in root.Elements("game"))
            {
                string path = (string)entry.Attribute("path");
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                    throw new InvalidDataException("Invalid executable path in " + FilePath);
                path = Path.GetFullPath(path);
                string name = (string)entry.Attribute("name");
                if (!games.Any(g => WindowManager.Paths.Equals(g.Path, path)))
                    games.Add(new Game(String.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name, path));
            }
            if (RegularStartupEnabled()) SetRegularStartup(true); // keep an existing ordinary startup path current
            return games;
        }

        internal static void Save(IEnumerable<Game> games)
        {
            Directory.CreateDirectory(Folder);
            string temp = Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                new XElement("games", games.Select(g => new XElement("game",
                    new XAttribute("name", g.Name), new XAttribute("path", g.Path)))).Save(temp);
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        internal static bool RegularStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                return key != null && key.GetValue(RunName) != null;
        }
        internal static void SetRegularStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (!enabled) key.DeleteValue(RunName, false);
                else
                {
                    string command = "\"" + Application.ExecutablePath + "\" --background";
                    if (!String.Equals(command, key.GetValue(RunName) as string, StringComparison.Ordinal))
                        key.SetValue(RunName, command, RegistryValueKind.String);
                }
            }
        }
    }
}
