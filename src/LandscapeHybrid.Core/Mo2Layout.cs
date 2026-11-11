using Microsoft.Win32;

namespace LandscapeHybrid;

/// <summary>
/// Where a Mod Organizer 2 instance keeps its parts. MO2 lets the settings file (ModOrganizer.ini) sit in one folder (a global
/// instance lives under %LOCALAPPDATA%\ModOrganizer\&lt;name&gt;) while the profiles, mods and overwrite folders live in another
/// ("base directory", or each moved separately: <c>base_directory</c>, <c>mod_directory</c>, <c>profiles_directory</c>,
/// <c>overwrite_directory</c> in the ini). <see cref="Locate"/> takes whichever folder the user typed and works out all of it.
/// </summary>
/// <param name="IniPath">The ModOrganizer.ini, or "" when only the folders were found.</param>
/// <param name="BaseDir">Folder holding profiles / mods / overwrite (the user's "base folder").</param>
/// <param name="GamePath">The game folder the ini records, or "" when unknown.</param>
/// <param name="SelectedProfile">The profile the ini says is open, or "".</param>
/// <param name="Note">What was matched, e.g. "ini found under %LOCALAPPDATA%": for the log and the status line.</param>
public sealed record Mo2Layout(string IniPath, string BaseDir, string ModsDir, string ProfilesDir, string OverwriteDir, string GamePath, string SelectedProfile, string Note)
{
    public string InstanceName => Path.GetFileName(Path.GetDirectoryName(IniPath)?.TrimEnd('\\', '/') ?? BaseDir.TrimEnd('\\', '/'));

    public string ProfileDirectory(string profile) => Path.Combine(ProfilesDir, profile);

    public IEnumerable<string> ProfileNames() =>
        Directory.Exists(ProfilesDir)
            ? Directory.GetDirectories(ProfilesDir).Select(Path.GetFileName).OfType<string>().OrderBy(n => n)
            : Enumerable.Empty<string>();

    public const string NotFoundMessage =
        "Pick your MO2 instance folder: either the folder holding ModOrganizer.ini, or the base folder that holds your profiles and mods folders. " +
        "(If MO2 has no ModOrganizer.ini next to ModOrganizer.exe, it is under %LOCALAPPDATA%\\ModOrganizer\\<instance name>.)";

    /// <summary>
    /// Finds the layout for a folder the user typed or browsed to. Accepts, in this order:
    /// a folder with ModOrganizer.ini (the ini decides where the rest is), the ModOrganizer.ini or ModOrganizer.exe itself,
    /// a global-instance ini whose base / mods / profiles folder is this folder (or sits under it), the MO2 program folder
    /// (the ini of the instance MO2 last had open), and finally a bare folder that has a <c>profiles</c> folder in it.
    /// Returns null when none of that fits.
    /// </summary>
    public static Mo2Layout? Locate(string? typed, string? globalInstancesRoot = null)
    {
        if (string.IsNullOrWhiteSpace(typed)) return null;
        string folder;
        try
        {
            folder = typed.Trim().Trim('"');
            if (File.Exists(folder)) folder = Path.GetDirectoryName(folder)!; // the ini or the exe: use its folder
            if (!Directory.Exists(folder)) return null;
            folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException) { return null; }

        // 1. the folder holds the ini
        var here = Path.Combine(folder, "ModOrganizer.ini");
        if (File.Exists(here)) return FromIni(here, "the instance folder");

        var root = globalInstancesRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        var globals = GlobalInstances(root).ToList();

        // 2. a global instance whose base / mods / profiles / overwrite folder is this folder (or this folder is inside the base)
        var matches = globals.Where(g => Under(folder, g.BaseDir) || Under(g.BaseDir, folder) || SameDir(folder, g.ModsDir) || SameDir(folder, g.ProfilesDir) || SameDir(folder, g.OverwriteDir)).ToList();
        // several instances fit (e.g. a drive root was given): only an exact base-folder match is safe to take
        if (matches.Count > 1) matches = matches.Where(g => SameDir(folder, g.BaseDir)).ToList();
        if (matches.Count == 1) return matches[0] with { Note = $"the instance's ModOrganizer.ini is in {Path.GetDirectoryName(matches[0].IniPath)} (found from the base folder you gave)" };

        // 3. the MO2 program folder (ModOrganizer.exe, no ini next to it): the instance MO2 last had open, or the only one there is
        if (File.Exists(Path.Combine(folder, "ModOrganizer.exe")))
        {
            var current = CurrentGlobalInstanceName();
            var pick = globals.FirstOrDefault(g => current != null && Path.GetFileName(Path.GetDirectoryName(g.IniPath)!).Equals(current, StringComparison.OrdinalIgnoreCase))
                       ?? (globals.Count == 1 ? globals[0] : null);
            if (pick != null) return pick with { Note = $"MO2 program folder given: using the instance {pick.InstanceName} (its ModOrganizer.ini is in {Path.GetDirectoryName(pick.IniPath)})" };
        }

        // 4. no ini anywhere: a base folder is enough to read profiles, mods and overwrite
        if (Directory.Exists(Path.Combine(folder, "profiles")))
            return new Mo2Layout("", folder, Path.Combine(folder, "mods"), Path.Combine(folder, "profiles"), Path.Combine(folder, "overwrite"), "", "",
                "no ModOrganizer.ini found: using the folder as the base folder. Fill in the game folder yourself.");
        return null;
    }

    /// <summary>Reads one ModOrganizer.ini and resolves every folder it names (%BASE_DIR% expanded, defaults filled in).</summary>
    public static Mo2Layout FromIni(string iniPath, string note)
    {
        var instanceDir = Path.GetDirectoryName(Path.GetFullPath(iniPath))!;
        var values = ReadIni(iniPath);
        string Get(string key) => values.TryGetValue(key, out var v) ? v : "";

        string Resolve(string v, string baseDir, string fallback)
        {
            if (v.Length == 0) return fallback;
            v = v.Replace("%BASE_DIR%", baseDir, StringComparison.OrdinalIgnoreCase);
            try { return Path.GetFullPath(Path.IsPathRooted(v) ? v : Path.Combine(baseDir, v)).TrimEnd('\\', '/'); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { return fallback; }
        }

        var baseDir = Resolve(Get("base_directory"), instanceDir, instanceDir);
        return new Mo2Layout(
            iniPath, baseDir,
            Resolve(Get("mod_directory"), baseDir, Path.Combine(baseDir, "mods")),
            Resolve(Get("profiles_directory"), baseDir, Path.Combine(baseDir, "profiles")),
            Resolve(Get("overwrite_directory"), baseDir, Path.Combine(baseDir, "overwrite")),
            Get("gamePath"), Get("selected_profile"), note);
    }

    static IEnumerable<Mo2Layout> GlobalInstances(string root)
    {
        if (!Directory.Exists(root)) yield break;
        IEnumerable<string> dirs;
        try { dirs = Directory.GetDirectories(root).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in dirs)
        {
            var ini = Path.Combine(d, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            Mo2Layout layout;
            try { layout = FromIni(ini, "global instance"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            yield return layout;
        }
    }

    static string? CurrentGlobalInstanceName()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Mod Organizer Team\Mod Organizer");
            return key?.GetValue("CurrentInstance") as string is { Length: > 0 } n ? n : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    static bool SameDir(string a, string b) => a.Length > 0 && b.Length > 0 &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="child"/> is <paramref name="parent"/> or a folder inside it.</summary>
    static bool Under(string child, string parent)
    {
        if (child.Length == 0 || parent.Length == 0) return false;
        var c = child.TrimEnd('\\', '/') + "\\";
        var p = parent.TrimEnd('\\', '/') + "\\";
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    // key=value lines of the ini (sections do not matter for the keys used here); @ByteArray(...) unwrapped, doubled backslashes undone.
    static Dictionary<string, string> ReadIni(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0 || line[0] is '[' or ';' or '#') continue;
            var key = line[..eq].Trim();
            if (d.ContainsKey(key)) continue;
            var v = line[(eq + 1)..].Trim().Trim('"');
            if (v.StartsWith("@ByteArray(") && v.EndsWith(')')) v = v[11..^1];
            d[key] = v.Replace(@"\\", @"\");
        }
        return d;
    }
}
