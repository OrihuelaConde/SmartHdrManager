using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SmartHdrManager
{
    /// <summary>
    /// Finds installed games in the Steam, Epic Games, GOG, Xbox (PC Game Pass), Ubisoft Connect, EA app, Rockstar and
    /// Battle.net, Amazon Games and itch.io libraries, with their real names
    /// and the executable(s) that run while playing. Read-only: only manifest files and the registry are read.
    /// </summary>
    public static class GameLibraryScanner
    {
        // Steam entries that are tools / runtimes, not games
        private static readonly HashSet<string> SteamNonGameAppIds = new()
        {
            "228980",   // Steamworks Common Redistributables
            "250820",   // SteamVR
            "431960",   // Wallpaper Engine
            "1070560",  // Steam Linux Runtime
            "1391110",  // Steam Linux Runtime - Soldier
            "1628350",  // Steam Linux Runtime - Sniper
            "1493710",  // Proton Experimental
        };

        private static readonly string[] NonGameNameParts =
        {
            "redistributable", "proton", "steam linux runtime", "dedicated server", "soundtrack", " sdk", "benchmark tool"
        };

        // Executables that are never the game itself
        private static readonly string[] ExcludedExeParts =
        {
            "unins", "uninst", "setup", "install", "redist", "vcredist", "vc_redist", "dxsetup", "directx", "dotnet",
            "crash", "report", "bugsplat", "sentry", "easyanticheat", "eac_", "battleye", "beservice", "anticheat",
            "prereq", "cleanup", "touchup", "helper", "updater", "update", "cefprocess", "webhelper", "quicksfv",
            "dxwebsetup", "physx", "ue4prereq", "ueprereq", "unitycrashhandler", "crs-handler", "cmd", "python"
        };

        // Launcher programs that only start the real game and then close (or wait in the background). A library entry
        // that points at one of these would make the app watch the launcher instead of the game (GitHub issue #1:
        // The Witcher 3 from GOG registers REDprelauncher.exe). Compared without spaces, dashes or dots.
        private static readonly HashSet<string> KnownLaunchers = new(StringComparer.OrdinalIgnoreCase)
        {
            "redprelauncher", "redlauncher", "gtavlauncher", "playgtav", "playrdr2", "rdr2launcher", "launcher",
            "startprotectedgame", "launcherpatcher", "gamelauncher", "bootstrapper"
        };

        // Extra executables of the same game (2.5.2): never added just because they score close to the main exe
        private static readonly string[] NotAGameExeParts =
        {
            "launcher", "config", "settings", "setting", "benchmark", "editor", "tool", "server", "modmanager",
            "mod_manager", "uploader", "patch", "selector", "inst", "map", "sdk", "p4v", "vcs"
        };

        // Bumped when the exe detection changes, so cached library entries are detected again once after an update
        // (2: launcher check + several exes per game, 3: extra exes must belong to the game by name – 2.5.2).
        public const int CurrentScanVersion = 3;

        // Executables found earlier, by game id: reused when the main exe still exists, so a quick refresh
        // does not have to search every install folder again (a full rescan passes null).
        [ThreadStatic] private static IReadOnlyDictionary<string, LibraryGame>? _previous;
        [ThreadStatic] private static Action<string>? _log;

        public static List<LibraryGame> Scan(IReadOnlyDictionary<string, LibraryGame>? previous, Action<string>? log = null)
        {
            _previous = previous;
            _log = log;
            var games = new List<LibraryGame>();
            Try(() => games.AddRange(ScanSteam()), "Steam", log);
            Try(() => games.AddRange(ScanEpic()), "Epic Games", log);
            Try(() => games.AddRange(ScanGog()), "GOG", log);
            Try(() => games.AddRange(ScanXbox()), "Xbox", log);

            // Launchers added in 2.5.1. Some games register with these AND with Steam (e.g. Rockstar or Ubisoft games
            // bought on Steam): skip an entry whose executable is already known from an earlier source.
            var extra = new List<LibraryGame>();
            Try(() => extra.AddRange(ScanUbisoft()), "Ubisoft Connect", log);
            Try(() => extra.AddRange(ScanEaApp()), "EA app", log);
            Try(() => extra.AddRange(ScanRockstar()), "Rockstar", log);
            Try(() => extra.AddRange(ScanBattleNet()), "Battle.net", log);
            Try(() => extra.AddRange(ScanAmazon()), "Amazon Games", log);
            Try(() => extra.AddRange(ScanItch()), "itch.io", log);
            var knownProcesses = new HashSet<string>(games.SelectMany(g => g.Processes), StringComparer.OrdinalIgnoreCase);
            foreach (var g in extra)
            {
                if (g.Processes.Count == 0 || g.Processes.Any(knownProcesses.Contains)) continue;
                games.Add(g);
                foreach (var p in g.Processes) knownProcesses.Add(p);
            }

            _previous = null;
            _log = null;

            // One entry per id, games without a detectable executable are dropped
            return games
                .Where(g => g.Processes.Count > 0)
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void Try(Action action, string source, Action<string>? log)
        {
            try { action(); }
            catch (Exception ex) { log?.Invoke($"{source} library could not be read: {ex.Message}"); }
        }

        // ------------------------------------------------------------------
        // Steam: libraryfolders.vdf -> steamapps\appmanifest_*.acf -> steamapps\common\<installdir>
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanSteam()
        {
            string? steamPath =
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ??
                Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (string.IsNullOrWhiteSpace(steamPath)) yield break;
            steamPath = steamPath.Replace('/', '\\');

            var libraries = new List<string> { steamPath };
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }

            foreach (string library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                foreach (string acf in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
                {
                    string text;
                    try { text = File.ReadAllText(acf); } catch { continue; }

                    string appId = AcfValue(text, "appid");
                    string name = AcfValue(text, "name");
                    string installDir = AcfValue(text, "installdir");
                    if (appId.Length == 0 || name.Length == 0 || installDir.Length == 0) continue;
                    if (SteamNonGameAppIds.Contains(appId) || IsNonGameName(name)) continue;

                    string folder = Path.Combine(steamapps, "common", installDir);
                    if (!Directory.Exists(folder)) continue;

                    string id = $"steam:{appId}";
                    if (TryReuse(id, name, "Steam", out var cached))
                    {
                        yield return cached;
                        continue;
                    }

                    var exes = FindGameExecutables(folder, name, installDir);
                    yield return Make(id, name, "Steam", exes);
                }
            }
        }

        private static string AcfValue(string text, string key)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Trim() : string.Empty;
        }

        // ------------------------------------------------------------------
        // Epic Games: ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item (JSON, includes the launch exe)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanEpic()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(dir)) yield break;

            foreach (string file in Directory.EnumerateFiles(dir, "*.item"))
            {
                LibraryGame? game = null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    string name = Str(root, "DisplayName");
                    string location = Str(root, "InstallLocation");
                    string launch = Str(root, "LaunchExecutable");
                    string appName = Str(root, "AppName");
                    string mainApp = Str(root, "MainGameAppName");
                    bool incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;

                    // Skip DLC / add-ons (they point at the main game) and unfinished installs
                    bool isDlc = mainApp.Length > 0 && appName.Length > 0 && !mainApp.Equals(appName, StringComparison.OrdinalIgnoreCase);
                    if (!incomplete && !isDlc && name.Length > 0 && location.Length > 0 && !IsNonGameName(name))
                    {
                        string id = $"epic:{(appName.Length > 0 ? appName : name)}";
                        string? registered = launch.Length > 0 ? Path.Combine(location, launch.Replace('/', '\\')) : null;
                        var exes = RegisteredOrFound(id, name, "Epic Games", registered, location, Path.GetFileName(location.TrimEnd('\\')));
                        game = Make(id, name, "Epic Games", exes);
                    }
                }
                catch { }

                if (game != null) yield return game;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

        // ------------------------------------------------------------------
        // GOG Galaxy: HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\<id> (gameName, exe, path, dependsOn)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanGog()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (key == null) yield break;

            foreach (string id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                if (game == null) continue;

                string name = game.GetValue("gameName") as string ?? string.Empty;
                string exe = game.GetValue("exe") as string ?? string.Empty;
                string path = game.GetValue("path") as string ?? string.Empty;
                string dependsOn = game.GetValue("dependsOn") as string ?? string.Empty;
                if (name.Length == 0 || dependsOn.Length > 0 || IsNonGameName(name)) continue;   // dependsOn = DLC

                // The registered exe can be a launcher (The Witcher 3 / Cyberpunk 2077: REDprelauncher.exe, GitHub issue #1)
                string libId = $"gog:{id}";
                var exes = RegisteredOrFound(libId, name, "GOG", exe.Length > 0 ? exe : null, path, Path.GetFileName(path.TrimEnd('\\')));
                yield return Make(libId, name, "GOG", exes);
            }
        }

        // ------------------------------------------------------------------
        // Ubisoft Connect: HKLM\SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\<id>\InstallDir
        // (display name from the "Uplay Install <id>" uninstall entry, else the folder name)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanUbisoft()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs");
            if (key == null) yield break;

            foreach (string id in key.GetSubKeyNames())
            {
                using var game = key.OpenSubKey(id);
                string dir = (game?.GetValue("InstallDir") as string ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;

                string folderName = Path.GetFileName(dir);
                string name = UninstallDisplayName($"Uplay Install {id}") ?? folderName;
                if (IsNonGameName(name)) continue;

                string libId = $"ubisoft:{id}";
                if (TryReuse(libId, name, "Ubisoft", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Ubisoft", FindGameExecutables(dir, name, folderName));
            }
        }

        // ------------------------------------------------------------------
        // EA app (and old Origin installs): uninstall entries whose uninstaller lives in "EAInstaller"
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanEaApp()
        {
            foreach (var (subKey, name, location, uninstall) in UninstallEntries())
            {
                if (uninstall.IndexOf("EAInstaller", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string dir = location.Trim().Trim('"').TrimEnd('\\');
                if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"ea:{subKey}";
                if (TryReuse(libId, name, "EA app", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "EA app", FindGameExecutables(dir, name, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Rockstar Games Launcher: HKLM\SOFTWARE\WOW6432Node\Rockstar Games\<game>\InstallFolder
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanRockstar()
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Rockstar Games");
            if (key == null) yield break;

            foreach (string sub in key.GetSubKeyNames())
            {
                // Launcher, Social Club and similar entries are not games
                if (sub.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    sub.IndexOf("social club", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                using var game = key.OpenSubKey(sub);
                string dir = (game?.GetValue("InstallFolder") as string ?? string.Empty).Trim().Trim('"').TrimEnd('\\');
                if (dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(sub)) continue;

                string libId = $"rockstar:{sub}";
                if (TryReuse(libId, sub, "Rockstar", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, sub, "Rockstar", FindGameExecutables(dir, sub, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Battle.net: uninstall entries whose uninstaller is "Battle.net … --uid=<product>" (DisplayName = game name)
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanBattleNet()
        {
            foreach (var (_, name, location, uninstall) in UninstallEntries())
            {
                var uid = Regex.Match(uninstall, @"Battle\.net.*--uid=([^\s""]+)", RegexOptions.IgnoreCase);
                if (!uid.Success) continue;
                // Public test realms and betas are separate installs of the same game
                if (name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Beta", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("PTR", StringComparison.Ordinal)) continue;
                string dir = location.Trim().Trim('"').TrimEnd('\\');
                if (name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;
                // The Battle.net app registers itself the same way
                if (name.Equals("Battle.net", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(dir).Equals("Battle.net", StringComparison.OrdinalIgnoreCase)) continue;

                string libId = $"battlenet:{uid.Groups[1].Value.ToLowerInvariant()}";
                if (TryReuse(libId, name, "Battle.net", out var cached))
                {
                    yield return cached;
                    continue;
                }
                yield return Make(libId, name, "Battle.net", FindGameExecutables(dir, name, Path.GetFileName(dir)));
            }
        }

        // ------------------------------------------------------------------
        // Amazon Games: %LocalAppData%\Amazon Games\Data\Games\Sql\GameInstallInfo.sqlite (table DbSet),
        // the launch exe from the game's fuel.json
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanAmazon()
        {
            string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Amazon Games", "Data", "Games", "Sql", "GameInstallInfo.sqlite");
            if (!File.Exists(db)) yield break;

            foreach (var row in ReadOnlySqlite.Query(db, "SELECT Id, ProductTitle, InstallDirectory FROM DbSet WHERE Installed = 1"))
            {
                string id = row[0] ?? string.Empty;
                string name = CleanName(row[1]);
                string dir = (row[2] ?? string.Empty).Replace('/', '\\').TrimEnd('\\');
                if (id.Length == 0 || name.Length == 0 || dir.Length == 0 || !Directory.Exists(dir) || IsNonGameName(name)) continue;

                string libId = $"amazon:{id}";
                if (TryReuse(libId, name, "Amazon Games", out var cached))
                {
                    yield return cached;
                    continue;
                }
                var exes = RegisteredOrFound(libId, name, "Amazon Games", FuelCommand(dir), dir, Path.GetFileName(dir));
                yield return Make(libId, name, "Amazon Games", exes);
            }
        }

        /// <summary>The exe in an Amazon game's fuel.json ("Main" › "Command"), or null.</summary>
        private static string? FuelCommand(string gameDir)
        {
            try
            {
                string fuel = Path.Combine(gameDir, "fuel.json");
                if (!File.Exists(fuel)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(fuel));
                if (!doc.RootElement.TryGetProperty("Main", out var main)) return null;
                string command = Str(main, "Command");
                return command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.Combine(gameDir, command.Replace('/', '\\')) : null;
            }
            catch
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // itch.io app: %AppData%\itch\db\butler.db – one "cave" per install, its "verdict" (JSON) lists the
        // install folder and the executables the app would launch
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanItch()
        {
            string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "itch", "db", "butler.db");
            if (!File.Exists(db)) yield break;

            const string sql = "SELECT games.id, games.title, games.classification, caves.verdict FROM caves JOIN games ON games.id = caves.game_id";
            foreach (var row in ReadOnlySqlite.Query(db, sql))
            {
                string id = row[0] ?? string.Empty;
                string name = CleanName(row[1]);
                // Tools, comics, soundtracks, assets … can be installed too
                if (id.Length == 0 || name.Length == 0 || !string.Equals(row[2], "game", StringComparison.OrdinalIgnoreCase) || IsNonGameName(name)) continue;

                string libId = $"itch:{id}";
                if (TryReuse(libId, name, "itch.io", out var cached))
                {
                    yield return cached;
                    continue;
                }

                var (dir, candidates) = ItchVerdict(row[3]);
                if (dir.Length == 0 || !Directory.Exists(dir)) continue;
                var exes = candidates.Count > 0
                    ? RegisteredOrFound(libId, name, "itch.io", candidates[0], dir, Path.GetFileName(dir))
                    : FindGameExecutables(dir, name, Path.GetFileName(dir));
                // Further native Windows candidates (e.g. a DX11 and a DX12 exe) are watched as well
                foreach (string extraExe in candidates.Skip(1))
                    if (exes.Count < 3 && !exes.Contains(extraExe, StringComparer.OrdinalIgnoreCase) && !LooksLikeLauncher(extraExe)) exes.Add(extraExe);
                yield return Make(libId, name, "itch.io", exes);
            }
        }

        /// <summary>Install folder and native Windows executables (full paths that exist) of an itch.io install's verdict.</summary>
        private static (string Dir, List<string> Exes) ItchVerdict(string? json)
        {
            var exes = new List<string>();
            if (string.IsNullOrWhiteSpace(json)) return (string.Empty, exes);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string dir = Str(root, "basePath").Replace('/', '\\').TrimEnd('\\');
                if (dir.Length == 0) return (string.Empty, exes);
                if (root.TryGetProperty("candidates", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in list.EnumerateArray())
                    {
                        string path = Str(c, "path").Replace('/', '\\');
                        // "helper" marks runtime plumbing next to the game (crash handlers, bundled Java / Python …)
                        if (Str(c, "flavor") != "windows" || Str(c, "helper").Length > 0 ||
                            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || IsExcludedExe(Path.GetFileNameWithoutExtension(path))) continue;
                        string full = Path.Combine(dir, path);
                        if (File.Exists(full)) exes.Add(full);
                    }
                }
                return (dir, exes);
            }
            catch
            {
                return (string.Empty, exes);
            }
        }

        /// <summary>A store title without trademark signs ("Game™" → "Game").</summary>
        private static string CleanName(string? name) =>
            (name ?? string.Empty).Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty).Trim();

        private static string? UninstallDisplayName(string subKey)
        {
            foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(root + "\\" + subKey);
                    if (key?.GetValue("DisplayName") is string name && name.Trim().Length > 0) return name.Trim();
                }
                catch { }
            }
            return null;
        }

        /// <summary>(subkey, DisplayName, InstallLocation, UninstallString) of installed programs (HKLM, 32 + 64 bit).</summary>
        private static IEnumerable<(string SubKey, string Name, string Location, string Uninstall)> UninstallEntries()
        {
            foreach (string root in new[] { @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                using var key = Registry.LocalMachine.OpenSubKey(root);
                if (key == null) continue;
                foreach (string sub in key.GetSubKeyNames())
                {
                    string name = string.Empty, location = string.Empty, uninstall = string.Empty;
                    try
                    {
                        using var entry = key.OpenSubKey(sub);
                        if (entry == null) continue;
                        name = (entry.GetValue("DisplayName") as string ?? string.Empty).Trim();
                        location = entry.GetValue("InstallLocation") as string ?? string.Empty;
                        uninstall = entry.GetValue("UninstallString") as string ?? string.Empty;
                    }
                    catch { continue; }
                    yield return (sub, name, location, uninstall);
                }
            }
        }

        // ------------------------------------------------------------------
        // Xbox app / PC Game Pass: <drive>:\XboxGames\<game>\Content\MicrosoftGame.config
        // ------------------------------------------------------------------
        private static IEnumerable<LibraryGame> ScanXbox()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                string root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
                if (!Directory.Exists(root)) continue;

                foreach (string gameDir in Directory.EnumerateDirectories(root))
                {
                    LibraryGame? game = null;
                    try
                    {
                        string content = Path.Combine(gameDir, "Content");
                        string config = Path.Combine(content, "MicrosoftGame.config");
                        if (!File.Exists(config)) continue;

                        var doc = XDocument.Load(config);
                        string folderName = Path.GetFileName(gameDir);
                        string? display = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals")
                            ?.Attribute("DefaultDisplayName")?.Value;
                        string name = !string.IsNullOrWhiteSpace(display) && !display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)
                            ? display
                            : folderName;

                        var exes = doc.Descendants()
                            .Where(e => e.Name.LocalName == "Executable")
                            .Select(e => e.Attribute("Name")?.Value)
                            .Where(v => !string.IsNullOrWhiteSpace(v))
                            .Select(v => Path.Combine(content, v!.Replace('/', '\\')))
                            .Where(p => !IsExcludedExe(Path.GetFileNameWithoutExtension(p)))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(3)
                            .ToList();

                        if (!IsNonGameName(name))
                            game = Make($"xbox:{folderName}", name, "Xbox", exes);
                    }
                    catch { }

                    if (game != null) yield return game;
                }
            }
        }

        // ------------------------------------------------------------------
        // Exe locations Windows already knows about (no disk search, only registry reads):
        //  - Game Bar's list of games you have played (GameConfigStore, "MatchedExeFullPath")
        //  - the Explorer program-name cache (MuiCache, value names are full exe paths)
        //  - the Program Compatibility Assistant list of programs that were started
        //  - "DisplayIcon" of installed programs (Apps & features)
        // Returns process name (lower case, no .exe) -> full path, only for files that still exist.
        // ------------------------------------------------------------------
        public static Dictionary<string, string> FindKnownExePaths()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                path = path.Trim().Trim('"');
                int comma = path.LastIndexOf(".exe,", StringComparison.OrdinalIgnoreCase);   // "C:\x\game.exe,0"
                if (comma >= 0) path = path.Substring(0, comma + 4);
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
                if (name.Length == 0 || result.ContainsKey(name)) return;
                try { if (File.Exists(path)) result[name] = path; } catch { }
            }

            // Game Bar / Game Mode: one subkey per game that was played
            try
            {
                using var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
                if (store != null)
                {
                    foreach (string sub in store.GetSubKeyNames())
                    {
                        using var game = store.OpenSubKey(sub);
                        Add(game?.GetValue("MatchedExeFullPath") as string);
                    }
                }
            }
            catch { }

            // Explorer's MuiCache: value names like "C:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe.FriendlyAppName"
            try
            {
                using var mui = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache");
                if (mui != null)
                {
                    foreach (string valueName in mui.GetValueNames())
                    {
                        int dot = valueName.LastIndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                        if (dot > 0) Add(valueName.Substring(0, dot + 4));
                    }
                }
            }
            catch { }

            // Program Compatibility Assistant: value names are full paths of started programs
            try
            {
                using var pca = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store");
                if (pca != null)
                    foreach (string valueName in pca.GetValueNames()) Add(valueName);
            }
            catch { }

            // Installed programs (machine-wide and per user, 64- and 32-bit)
            foreach (var (hive, keyPath) in new[]
            {
                (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall")
            })
            {
                try
                {
                    using var uninstall = hive.OpenSubKey(keyPath);
                    if (uninstall == null) continue;
                    foreach (string sub in uninstall.GetSubKeyNames())
                    {
                        using var app = uninstall.OpenSubKey(sub);
                        Add(app?.GetValue("DisplayIcon") as string);
                    }
                }
                catch { }
            }

            return result;
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------
        private static bool TryReuse(string id, string name, string source, out LibraryGame game)
        {
            game = null!;
            var prev = _previous;
            if (prev == null || !prev.TryGetValue(id, out var old) || old.Processes.Count == 0) return false;
            if (string.IsNullOrEmpty(old.ExePath) || !File.Exists(old.ExePath)) return false;
            if (old.ScanVersion < CurrentScanVersion) return false;   // found by an older version: detect once more

            game = new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = new List<string>(old.Processes),
                ExePath = old.ExePath,
                ScanVersion = old.ScanVersion
            };
            return true;
        }

        private static LibraryGame Make(string id, string name, string source, List<string> exePaths)
        {
            return new LibraryGame
            {
                Id = id,
                Name = name.Trim(),
                Source = source,
                Processes = exePaths
                    .Select(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
                    .Where(p => p.Length > 0)
                    .Distinct()
                    .ToList(),
                ExePath = exePaths.FirstOrDefault(File.Exists),
                ScanVersion = CurrentScanVersion
            };
        }

        /// <summary>
        /// True for programs that only start the real game: names containing "launcher" and a few known stubs
        /// (REDprelauncher, PlayGTAV, start_protected_game, …).
        /// </summary>
        public static bool LooksLikeLauncher(string exePathOrName)
        {
            string key = Normalize(Path.GetFileNameWithoutExtension(exePathOrName));
            return key.Contains("launcher") || KnownLaunchers.Contains(key);
        }

        /// <summary>
        /// Executables of a library game whose launcher registers an exe (GOG, Epic). If that exe is a launcher, the
        /// install folder is searched for the real game instead, so the app watches the program that actually runs
        /// while playing. The launcher is only kept when nothing else is found.
        /// </summary>
        private static List<string> RegisteredOrFound(string id, string name, string source, string? registeredExe, string folder, string installDir)
        {
            bool folderOk = folder.Length > 0 && Directory.Exists(folder);
            var exes = new List<string>();

            if (!string.IsNullOrEmpty(registeredExe) && File.Exists(registeredExe))
            {
                if (folderOk && LooksLikeLauncher(registeredExe))
                {
                    var found = FindGameExecutables(folder, name, installDir).Where(e => !LooksLikeLauncher(e)).ToList();
                    if (found.Count > 0)
                    {
                        // Logged once, not at every start (unchanged result = same processes as last time)
                        var names = found.Select(e => Path.GetFileNameWithoutExtension(e).ToLowerInvariant()).Distinct().ToList();
                        bool known = _previous != null && _previous.TryGetValue(id, out var old) &&
                                     names.All(n => old.Processes.Contains(n, StringComparer.OrdinalIgnoreCase));
                        if (!known)
                            _log?.Invoke($"{source}: '{name}' registers {Path.GetFileName(registeredExe)}, which is a launcher – " +
                                         $"watching {string.Join(", ", names.Select(n => n + ".exe"))} instead.");
                        return found;
                    }
                }
                exes.Add(registeredExe);
                if (folderOk) AddShippingCounterpart(exes, folder);
                return exes;
            }

            return folderOk ? FindGameExecutables(folder, name, installDir) : exes;
        }

        public static bool IsExcludedExeName(string fileNameWithoutExt) => IsExcludedExe(fileNameWithoutExt);

        private static bool IsNonGameName(string name)
        {
            string n = " " + name.ToLowerInvariant();
            return NonGameNameParts.Any(n.Contains);
        }

        private static bool IsExcludedExe(string fileNameWithoutExt)
        {
            string n = fileNameWithoutExt.ToLowerInvariant();
            return ExcludedExeParts.Any(n.Contains);
        }

        private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        /// <summary>
        /// Picks the most likely game executable(s) in an install folder: names similar to the game,
        /// Unreal "-Win64-Shipping" binaries (the process that actually runs), and large files score higher;
        /// launchers, installers, crash reporters etc. are ignored.
        /// </summary>
        public static List<string> FindGameExecutables(string folder, string gameName, string installDir)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };
            string nameKey = Normalize(gameName);
            string dirKey = Normalize(installDir);

            var candidates = new List<(string Path, double Score)>();
            foreach (string exe in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                string file = Path.GetFileNameWithoutExtension(exe);
                if (IsExcludedExe(file)) continue;

                long size;
                try { size = new FileInfo(exe).Length; } catch { continue; }
                if (size < 300 * 1024) continue;

                string key = Normalize(file);
                double score = Math.Log10(size);   // ~5.5 (300 KB) .. ~8.5 (300 MB)

                if (key.Length > 0 && (key == nameKey || key == dirKey)) score += 6;
                else if (key.Length >= 3 && (nameKey.Contains(key) || dirKey.Contains(key) || key.Contains(dirKey))) score += 3;

                if (file.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith("-WinGDK-Shipping", StringComparison.OrdinalIgnoreCase)) score += 5;
                if (key.Contains("launcher")) score -= 4;
                if (key.Contains("server") || key.Contains("editor") || key.Contains("tool")) score -= 3;

                int depth = exe.Substring(folder.Length).Count(c => c == '\\');
                score -= Math.Max(0, depth - 1) * 0.3;

                candidates.Add((exe, score));
            }

            var ordered = candidates.OrderByDescending(c => c.Score).ToList();
            var result = ordered.Take(1).Select(c => c.Path).ToList();
            if (result.Count == 0) return result;
            AddShippingCounterpart(result, folder);

            // Some games ship separate exes, e.g. for DX11 / DX12 / Vulkan: also watch other exes that score almost as
            // well as the best one (at most 3 names in total). Only exes that clearly belong to the game: the name
            // matches the game / folder name, or starts like the main exe ("Game_DX12" next to "Game"). Tools,
            // launchers, installers and patchers are never added (e.g. a bundled "p4vinst64.exe" next to "OLGame.exe").
            double best = ordered[0].Score;
            string bestKey = Normalize(Path.GetFileNameWithoutExtension(ordered[0].Path));
            var names = new HashSet<string>(result.Select(p => Path.GetFileNameWithoutExtension(p)), StringComparer.OrdinalIgnoreCase);
            foreach (var (path, score) in ordered.Skip(1))
            {
                if (names.Count >= 3 || score < best - 1.0) break;
                string file = Path.GetFileNameWithoutExtension(path);
                string lower = file.ToLowerInvariant();
                if (names.Contains(file) || NotAGameExeParts.Any(lower.Contains) || LooksLikeLauncher(file)) continue;

                string key = Normalize(file);
                bool namedLikeGame = key.Length >= 3 && (key == nameKey || key == dirKey || nameKey.Contains(key) || dirKey.Contains(key));
                bool sameStem = key.Length >= 4 && bestKey.Length >= 4 && (key.StartsWith(bestKey) || bestKey.StartsWith(key) ||
                                CommonPrefixLength(key, bestKey) >= Math.Max(4, Math.Min(key.Length, bestKey.Length) - 2));
                if (!namedLikeGame && !sameStem) continue;

                result.Add(path);
                names.Add(file);
            }
            return result;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        /// <summary>Unreal games: "Game.exe" only starts "Game-Win64-Shipping.exe" (the real process) and the other way round.</summary>
        private static void AddShippingCounterpart(List<string> exes, string folder)
        {
            try
            {
                string main = Path.GetFileNameWithoutExtension(exes[0]);
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true };

                if (main.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase)) return;

                string? shipping = Directory.EnumerateFiles(folder, main + "-Win64-Shipping.exe", options).FirstOrDefault()
                                   ?? Directory.EnumerateFiles(folder, main + "-WinGDK-Shipping.exe", options).FirstOrDefault();
                if (shipping != null && !exes.Contains(shipping, StringComparer.OrdinalIgnoreCase))
                    exes.Add(shipping);
            }
            catch { }
        }
    }
}
