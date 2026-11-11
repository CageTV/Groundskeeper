using System.Diagnostics;
using Microsoft.Win32;

namespace LandscapeHybrid;

/// <summary>An MO2 instance: the folder holding its ModOrganizer.ini and what that file says.</summary>
public sealed record Mo2InstanceInfo(string Name, string Path, string ModsDirectory, string GamePath, string SelectedProfile);

/// <summary>
/// Works out which Mod Organizer 2 instance Groundskeeper belongs to, so the window can fill in the instance, profile and a
/// default output folder by itself. Two ways, in order: the exe sits inside the instance (a portable instance, or the tool installed
/// as an MO2 mod under &lt;instance&gt;\mods), or the process was started by ModOrganizer.exe (then that MO2's open instance is used:
/// the ini next to it for a portable MO2, else the instance named in the registry).
/// </summary>
public static class Mo2InstanceFinder
{
    public const string OutputModFolderName = "Groundskeeper Output";

    public static Mo2InstanceInfo? Detect()
    {
        try { return FromExeLocation() ?? FromLaunchingOrganizer(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The default output mod folder for an instance: &lt;mods&gt;\Groundskeeper Output.</summary>
    public static string DefaultOutputFolder(string instancePath)
    {
        var layout = Mo2Layout.Locate(instancePath);
        var mods = layout?.ModsDir ?? System.IO.Path.Combine(instancePath, "mods");
        return System.IO.Path.Combine(mods, OutputModFolderName);
    }

    static Mo2InstanceInfo? FromExeLocation()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var ini = System.IO.Path.Combine(d.FullName, "ModOrganizer.ini");
            if (File.Exists(ini)) return Read(ini, d.Name, d.FullName);
        }
        return null;
    }

    static Mo2InstanceInfo? FromLaunchingOrganizer()
    {
        var organizer = LaunchingOrganizer();
        if (organizer == null) return null;
        var dir = System.IO.Path.GetDirectoryName(organizer)!;
        var portable = System.IO.Path.Combine(dir, "ModOrganizer.ini");
        if (File.Exists(portable)) return Read(portable, System.IO.Path.GetFileName(dir), dir);

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Mod Organizer Team\Mod Organizer");
        if (key?.GetValue("CurrentInstance") is not string name || name.Length == 0) return null;
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer", name);
        var ini = System.IO.Path.Combine(root, "ModOrganizer.ini");
        return File.Exists(ini) ? Read(ini, name, root) : null;
    }

    // Walks up the parent processes (a few levels: MO2 may start us through a helper) looking for ModOrganizer.exe.
    static string? LaunchingOrganizer()
    {
        var pid = Environment.ProcessId;
        for (var hop = 0; hop < 4; hop++)
        {
            pid = ParentProcessId(pid);
            if (pid <= 0) return null;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.ProcessName.Equals("ModOrganizer", StringComparison.OrdinalIgnoreCase)) return p.MainModule?.FileName;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
        }
        return null;
    }

    static int ParentProcessId(int pid)
    {
        try
        {
            using var h = Process.GetProcessById(pid);
            var pbi = new PROCESS_BASIC_INFORMATION();
            return NtQueryInformationProcess(h.Handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _) == 0 ? (int)pbi.InheritedFromUniqueProcessId : -1;
        }
        catch { return -1; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref PROCESS_BASIC_INFORMATION info, int size, out int returned);

    public static Mo2InstanceInfo? Read(string iniPath, string name, string instanceDir)
    {
        if (!File.Exists(iniPath)) return new Mo2InstanceInfo(name, instanceDir, System.IO.Path.Combine(instanceDir, "mods"), "", "");
        string Get(string key)
        {
            foreach (var line in File.ReadLines(iniPath))
            {
                if (!line.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) continue;
                var v = line[(key.Length + 1)..].Trim().Trim('"');
                if (v.StartsWith("@ByteArray(") && v.EndsWith(')')) v = v[11..^1];
                return v.Replace(@"\\", @"\");
            }
            return "";
        }
        var baseDir = Get("base_directory");
        var mods = Get("mod_directory");
        // an unset mod_directory means <base directory>\mods, and an unset base directory means the instance folder itself
        var root = baseDir.Length > 0 ? baseDir.Replace("%BASE_DIR%", instanceDir) : instanceDir;
        mods = mods.Length > 0 ? mods.Replace("%BASE_DIR%", root) : System.IO.Path.Combine(root, "mods");
        return new Mo2InstanceInfo(name, instanceDir, mods, Get("gamePath"), Get("selected_profile"));
    }
}
