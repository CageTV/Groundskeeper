// MO2 layouts users reported: the folder holding ModOrganizer.ini is not the folder holding profiles / mods, so typing either one
// used to fail ("Pick your MO2 instance folder" or "no profiles"). Each case below builds the layout on disk and asks Mo2Layout.Locate.
using LandscapeHybrid;
using SeamFinder.Core;

var root = Path.Combine(Path.GetTempPath(), "mo2-layout-check-" + Guid.NewGuid().ToString("N"));
var fails = 0;
void Check(string name, bool ok, string detail = "") { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name} {(ok ? "" : detail)}"); if (!ok) fails++; }
void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }

// ---- user 1/3: global instance (ini under the "AppData" root), base folder elsewhere, MO2 program folder has no ini
var appData = Path.Combine(root, "AppData", "ModOrganizer");
var baseDir = Path.Combine(root, "MO2-Q", "SkyrimSE");
var mo2Prog = Path.Combine(root, "MO2");
var game = Path.Combine(root, "Skyrim Special Edition");
Write(Path.Combine(appData, "SkyrimSE", "ModOrganizer.ini"),
    "[General]\r\ngamePath=@ByteArray(" + game.Replace(Path.DirectorySeparatorChar.ToString(), "\\") + ")\r\nselected_profile=@ByteArray(2025)\r\n[Settings]\r\nbase_directory=" + baseDir.Replace(Path.DirectorySeparatorChar, '/') + "\r\n");
Directory.CreateDirectory(Path.Combine(baseDir, "profiles", "2025")); Directory.CreateDirectory(Path.Combine(baseDir, "profiles", "other"));
Directory.CreateDirectory(Path.Combine(baseDir, "mods")); Directory.CreateDirectory(Path.Combine(baseDir, "overwrite"));
Write(Path.Combine(mo2Prog, "ModOrganizer.exe"), "x");

var a = Mo2Layout.Locate(baseDir, appData);
Check("base folder -> finds the global ini", a is { IniPath.Length: > 0 } && a.ProfilesDir == baseDir + @"\profiles" && a.GamePath == game && a.SelectedProfile == "2025", a?.ToString() ?? "null");
Check("base folder -> profiles listed", a != null && a.ProfileNames().SequenceEqual(new[] { "2025", "other" }));
Check("base folder, trailing slash and quotes", Mo2Layout.Locate("\"" + baseDir + "\\\"", appData)?.ModsDir == baseDir + @"\mods");
var b = Mo2Layout.Locate(Path.Combine(appData, "SkyrimSE"), appData);
Check("ini folder -> profiles come from base_directory", b != null && b.ProfilesDir == baseDir + @"\profiles" && b.ProfileNames().Count() == 2, b?.ToString() ?? "null");
Check("path to the ini itself", Mo2Layout.Locate(Path.Combine(appData, "SkyrimSE", "ModOrganizer.ini"), appData)?.BaseDir == baseDir);
var c = Mo2Layout.Locate(mo2Prog, appData);
Check("MO2 program folder (no ini beside the exe) -> the only global instance", c?.ProfilesDir == baseDir + @"\profiles", c?.ToString() ?? "null");
Check("mods folder typed -> same instance", Mo2Layout.Locate(Path.Combine(baseDir, "mods"), appData)?.ProfilesDir == baseDir + @"\profiles");

// ---- two instances share a parent: typing the parent is ambiguous, an exact base is not
var base2 = Path.Combine(root, "MO2-Q", "Other");
Write(Path.Combine(appData, "Other", "ModOrganizer.ini"), "[Settings]\r\nbase_directory=" + base2.Replace(Path.DirectorySeparatorChar, '/') + "\r\n");
Directory.CreateDirectory(Path.Combine(base2, "profiles", "p"));
Check("parent of two bases is ambiguous -> null", Mo2Layout.Locate(Path.Combine(root, "MO2-Q"), appData) == null);
Check("exact base still unique", Mo2Layout.Locate(base2, appData)?.BaseDir == base2);

// ---- portable instance with separately moved folders (%BASE_DIR% expansion, overwrite outside)
var port = Path.Combine(root, "Portable"); var ow = Path.Combine(root, "elsewhere", "ow"); var pr = Path.Combine(root, "elsewhere", "prof");
Write(Path.Combine(port, "ModOrganizer.ini"), "[Settings]\r\nmod_directory=%BASE_DIR%/modsX\r\noverwrite_directory=" + ow.Replace(Path.DirectorySeparatorChar, '/') + "\r\nprofiles_directory=" + pr.Replace(Path.DirectorySeparatorChar, '/') + "\r\n");
Directory.CreateDirectory(Path.Combine(pr, "pp"));
var d = Mo2Layout.Locate(port, Path.Combine(root, "none"));
Check("portable: moved mods/overwrite/profiles", d != null && d.ModsDir == port + @"\modsX" && d.OverwriteDir == ow && d.ProfilesDir == pr && d.ProfileNames().SequenceEqual(new[] { "pp" }), d?.ToString() ?? "null");

// ---- no ini anywhere: bare base folder still works for profiles/mods
var bare = Path.Combine(root, "Bare"); Directory.CreateDirectory(Path.Combine(bare, "profiles", "x"));
var e = Mo2Layout.Locate(bare, Path.Combine(root, "none"));
Check("no ini: bare base folder", e != null && e.IniPath == "" && e.GamePath == "" && e.ProfileNames().SequenceEqual(new[] { "x" }), e?.ToString() ?? "null");
Check("nonsense folder -> null", Mo2Layout.Locate(Path.Combine(root, "does-not-exist"), appData) == null && Mo2Layout.Locate("", appData) == null);

// ---- the resolver reads plugins from a mods folder and an overwrite folder that are NOT under one instance folder
var gdata = Path.Combine(root, "game", "Data"); Write(Path.Combine(gdata, "Skyrim.esm"), "x");
var prof = Path.Combine(root, "R", "profiles", "p"); var mods = Path.Combine(root, "R", "mods"); var over = Path.Combine(root, "R", "ow-elsewhere");
Write(Path.Combine(prof, "plugins.txt"), "*ModA.esp\r\n*FromOverwrite.esp\r\n"); Write(Path.Combine(prof, "loadorder.txt"), "Skyrim.esm\r\nModA.esp\r\nFromOverwrite.esp\r\n");
Write(Path.Combine(prof, "modlist.txt"), "+ModA\r\n"); Write(Path.Combine(mods, "ModA", "ModA.esp"), "x"); Write(Path.Combine(over, "FromOverwrite.esp"), "x");
var r = Mo2Resolver.ResolveFromFolders(prof, mods, over, gdata);
Check("resolver: mod plugin + overwrite plugin from custom folders", r.LoadOrder.Select(p => p.FileName).SequenceEqual(new[] { "Skyrim.esm", "ModA.esp", "FromOverwrite.esp" })
    && r.LoadOrder[2].FilePath == Path.Combine(over, "FromOverwrite.esp"), string.Join(",", r.LoadOrder.Select(p => p.FilePath)));
Write(Path.Combine(over, "meshes", "a.nif"), "x");
Check("resolver: loose asset found in the custom overwrite folder", r.ResolveDataFile("meshes/a.nif") == Path.Combine(over, "meshes", "a.nif"), r.ResolveDataFile("meshes/a.nif") ?? "null");

try { Directory.Delete(root, true); } catch { }
Console.WriteLine(fails == 0 ? "ALL PASS" : $"{fails} FAILED");
return fails == 0 ? 0 : 1;
