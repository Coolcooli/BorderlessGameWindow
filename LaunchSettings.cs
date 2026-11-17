using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows.Forms;

namespace BorderlessGameWindow
{
    // The registered task is the persistent source of truth for administrator
    // mode, including installations configured before this setting was added.
    internal sealed class LaunchSettings
    {
        private const string TaskName = "Borderless Game Window";
        internal const string TaskArgument = "--scheduled";
        internal const string ConfigureArgument = "--configure-launch";
        internal bool UseAdministrator { get; private set; }
        internal bool AtSignIn { get; private set; }

        private static string CurrentSid
        {
            get { using (var id = WindowsIdentity.GetCurrent()) return id.User.Value; }
        }

        internal static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        private static dynamic Scheduler()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
            service.Connect();
            return service;
        }

        private static dynamic MatchingTask(dynamic folder, bool requireAvailableName)
        {
            dynamic task;
            try { task = folder.GetTask(TaskName); }
            catch (FileNotFoundException) { return null; }
            catch (COMException ex) { if (ex.ErrorCode == unchecked((int)0x80070002)) return null; throw; }
            dynamic def = task.Definition;
            if (def.Actions.Count == 1 && def.Actions.Item(1).Type == 0)
            {
                dynamic action = def.Actions.Item(1);
                string args = action.Arguments;
                string principal = def.Principal.UserId;
                if (String.Equals(action.Path, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
                    (args == "--background" || args == TaskArgument) && def.Principal.LogonType == 3 &&
                    IsCurrentUser(principal))
                    return task;
            }
            if (requireAvailableName)
                throw new InvalidOperationException("A different installation already uses the Borderless Game Window task. Open the installed app to change administrator mode.");
            return null;
        }

        private static bool IsCurrentUser(string principal)
        {
            if (String.IsNullOrWhiteSpace(principal)) return false;
            if (principal.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
                return String.Equals(principal, CurrentSid, StringComparison.OrdinalIgnoreCase);
            try { return new NTAccount(principal).Translate(typeof(SecurityIdentifier)).Value == CurrentSid; }
            catch (IdentityNotMappedException) { return false; }
        }

        internal static LaunchSettings Read()
        {
            var settings = new LaunchSettings { AtSignIn = Settings.RegularStartupEnabled() };
            dynamic task;
            try { task = MatchingTask(Scheduler().GetFolder(@"\"), false); }
            catch (UnauthorizedAccessException) { return settings; } // task belongs to another account
            catch (COMException ex)
            {
                if (ex.ErrorCode == unchecked((int)0x80041315) || ex.ErrorCode == unchecked((int)0x800706BA) ||
                    ex.ErrorCode == unchecked((int)0x80070422)) return settings; // scheduler unavailable
                throw;
            }
            if (task == null || task.Definition.Principal.RunLevel != 1) return settings;
            settings.UseAdministrator = true;
            settings.AtSignIn = false;
            foreach (dynamic trigger in task.Definition.Triggers)
                if (task.Enabled && trigger.Type == 9 && trigger.Enabled) settings.AtSignIn = true;
            return settings;
        }

        internal static void StartTask()
        {
            dynamic task = MatchingTask(Scheduler().GetFolder(@"\"), true);
            if (task == null) throw new InvalidOperationException("The app's launch task is missing.");
            task.Run(null);
        }

        internal static bool Configure(bool administrator, bool signIn)
        {
            // Also check before showing UAC so a portable copy can explain where
            // it needs to be installed without first asking for permission.
            CheckInstalledLocation();
            if (IsElevated())
            {
                Register(administrator, signIn);
                return true;
            }
            string args = String.Join(" ", ConfigureArgument, administrator ? "on" : "off",
                signIn ? "signin" : "manual", CurrentSid);
            var start = new ProcessStartInfo(Application.ExecutablePath, args) {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Application.StartupPath
            };
            try
            {
                using (Process child = Process.Start(start))
                {
                    child.WaitForExit();
                    return child.ExitCode == 0; // The configuring instance reports its own errors.
                }
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode == 1223) return false; throw; }
        }

        internal static void ConfigureFromArguments(string[] args)
        {
            if (args.Length != 4 || (args[1] != "on" && args[1] != "off") ||
                (args[2] != "signin" && args[2] != "manual") || args[3] != CurrentSid || !IsElevated())
                throw new InvalidOperationException("Approve the change with the same Windows administrator account that runs the app.");
            CheckInstalledLocation();
            Register(args[1] == "on", args[2] == "signin");
        }

        private static void Register(bool administrator, bool signIn)
        {
            dynamic service = Scheduler();
            dynamic folder = service.GetFolder(@"\");
            dynamic existing = MatchingTask(folder, true);
            dynamic def = service.NewTask(0);
            string sid = CurrentSid;
            def.RegistrationInfo.Description = "Start Borderless Game Window for this Windows user.";
            def.Principal.UserId = sid;
            def.Principal.LogonType = 3; // interactive token, current user's desktop
            def.Principal.RunLevel = administrator ? 1 : 0;
            if (administrator && signIn)
            {
                dynamic trigger = def.Triggers.Create(9); // at logon
                trigger.UserId = sid;
            }
            dynamic action = def.Actions.Create(0);
            action.Path = Application.ExecutablePath;
            action.Arguments = TaskArgument;
            action.WorkingDirectory = Application.StartupPath;
            def.Settings.ExecutionTimeLimit = "PT0S";
            def.Settings.DisallowStartIfOnBatteries = false;
            def.Settings.StopIfGoingOnBatteries = false;
            // A second invocation delivers the existing settings-window signal.
            // The application mutex still admits only one monitoring instance.
            def.Settings.MultipleInstances = 0;
            string acl = "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;" + sid + ")";
            int flags = 0x10 | (existing == null ? 2 : 4);
            // Change the ordinary Run entry first and restore it if registration fails.
            bool previous = Settings.RegularStartupEnabled();
            Settings.SetRegularStartup(!administrator && signIn);
            try { folder.RegisterTaskDefinition(TaskName, def, flags, sid, null, 3, acl); }
            catch { Settings.SetRegularStartup(previous); throw; }
        }

        // A passwordless elevated launch must point to administrator-controlled
        // code. Check the application directory, its contents and its ancestors.
        private static void CheckInstalledLocation()
        {
            var exe = new FileInfo(Application.ExecutablePath);
            var dir = exe.Directory;
            if (dir == null || new DriveInfo(dir.Root.FullName).DriveType != DriveType.Fixed)
                throw LocationError();
            const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete |
                FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            CheckAccess(dir, write);
            foreach (FileSystemInfo item in dir.EnumerateFileSystemInfos())
                CheckAccess(item, write);
            for (DirectoryInfo parent = dir.Parent; parent != null; parent = parent.Parent)
            {
                FileSystemRights replace = FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if (parent.Parent != null) replace |= FileSystemRights.Delete | FileSystemRights.WriteAttributes;
                CheckAccess(parent, replace);
            }
        }

        private static void CheckAccess(FileSystemInfo item, FileSystemRights unsafeRights)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw LocationError();
            FileSystemSecurity acl = item is DirectoryInfo
                ? (FileSystemSecurity)((DirectoryInfo)item).GetAccessControl() : ((FileInfo)item).GetAccessControl();
            if (!TrustedOwner(acl.GetOwner(typeof(SecurityIdentifier)).Value)) throw LocationError();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Deny ||
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || TrustedOwner(rule.IdentityReference.Value)) continue;
                uint rights = unchecked((uint)rule.FileSystemRights);
                if ((rights & ((uint)unsafeRights | 0x50000000U)) != 0) throw LocationError(); // generic write/all
            }
        }

        private static bool TrustedOwner(string sid)
        {
            return sid == "S-1-5-18" || sid == "S-1-5-32-544" ||
                sid == "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
        }
        private static Exception LocationError()
        {
            return new InvalidOperationException("To enable administrator mode, install this app in its own administrator-protected folder under Program Files, then open that copy.");
        }
    }
}
