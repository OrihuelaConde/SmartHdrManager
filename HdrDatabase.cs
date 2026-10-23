using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SmartHdrManager
{
    // =========================================================================
    // HDR GAMES LIST: which games support HDR, from PCGamingWiki (CC BY-NC-SA 3.0).
    // Off by default (Settings › HDR games list). When on, the app downloads hdr-games.json from the release "hdr-db"
    // on github.com at most once a week – built by hdr-database.ps1 and the "HDR games list" workflow – and matches
    // it against the games on this PC. Nothing is sent: the whole list is downloaded, the matching happens here.
    // The list only marks and suggests games; HDR is never turned on for a game without you.
    // =========================================================================

    /// <summary>Values of PCGamingWiki's Video.HDR field that the list contains.</summary>
    public static class HdrSupport
    {
        public const string Native = "true";       // the game has an HDR option
        public const string Limited = "limited";   // HDR with restrictions (e.g. only some modes, known issues)
        public const string Hackable = "hackable"; // HDR with a mod or a config tweak (see the game's wiki page)

        public static string Describe(string value) => value switch
        {
            Native => "HDR in game",
            Limited => "limited HDR",
            Hackable => "HDR with a mod",
            _ => "HDR"
        };
    }

    /// <summary>A game from the list that matches a game on this PC.</summary>
    public sealed class HdrMatch
    {
        public string Name { get; init; } = string.Empty;        // page name on PCGamingWiki
        public string Support { get; init; } = string.Empty;     // HdrSupport value
        public bool ByName { get; init; }                        // matched by name, not by Steam / GOG id
        internal bool HasSteamIds { get; init; }                 // the wiki lists Steam ids for it
        internal bool HasGogIds { get; init; }                   // the wiki lists GOG ids for it

        // "/" stays a path separator in wiki titles ("Fate/Samurai Remnant")
        public string PageUrl => "https://www.pcgamingwiki.com/wiki/" + Uri.EscapeDataString(Name.Replace(' ', '_')).Replace("%2F", "/");
    }

    public sealed class HdrDatabase
    {
        public const string DownloadUrl = "https://github.com/VantaCode420/SmartHdrManager/releases/download/hdr-db/hdr-games.json";
        public const string Attribution = "Data: PCGamingWiki (CC BY-NC-SA 3.0)";

        /// <summary>The last downloaded list, next to config.json.</summary>
        public static readonly string CachePath = Path.Combine(Path.GetDirectoryName(HdrEngine.ConfigPath)!, "hdr-games.json");

        private const int MaxBytes = 16 * 1024 * 1024;

        private readonly Dictionary<string, HdrMatch> _steam = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HdrMatch> _gog = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HdrMatch> _names = new(StringComparer.Ordinal);

        public int Count { get; private set; }
        public string Generated { get; private set; } = string.Empty;   // "2026-10-23", when the list was built

        private HdrDatabase() { }

        /// <summary>Same rule as KeyOf in hdr-database.ps1: lower case, no "(2016)"-style suffix, letters and digits only.</summary>
        public static string KeyOf(string name)
        {
            string n = Regex.Replace(name ?? string.Empty, @"\s*\([^)]*\)\s*$", string.Empty);
            return Regex.Replace(n.ToLowerInvariant(), @"[^\p{L}\p{N}]", string.Empty);
        }

        /// <summary>Reads the list. Throws when it is not a valid hdr-games.json.</summary>
        public static HdrDatabase Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The HDR games list has an unknown format.");

            var db = new HdrDatabase { Generated = Text(root, "generated") };
            // The same name twice (e.g. "Doom (1993)" and "Doom (2016)" are both "doom"): no match by name for it
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var game in games.EnumerateArray())
            {
                string name = Text(game, "name");
                string support = Text(game, "hdr");
                if (name.Length == 0 || support.Length == 0) continue;
                var match = new HdrMatch { Name = name, Support = support };
                db.Count++;

                var steamIds = Ids(game, "steam").ToList();
                var gogIds = Ids(game, "gog").ToList();
                foreach (string id in steamIds) db._steam.TryAdd(id, match);
                foreach (string id in gogIds) db._gog.TryAdd(id, match);

                string key = Text(game, "key");
                if (key.Length == 0) key = KeyOf(name);
                if (key.Length == 0 || ambiguous.Contains(key)) continue;
                var byName = new HdrMatch { Name = name, Support = support, ByName = true, HasSteamIds = steamIds.Count > 0, HasGogIds = gogIds.Count > 0 };
                if (!db._names.TryAdd(key, byName))
                {
                    db._names.Remove(key);
                    ambiguous.Add(key);
                }
            }
            if (db.Count == 0) throw new InvalidDataException("The HDR games list is empty.");
            return db;
        }

        /// <summary>The list downloaded earlier, or null when there is none (or it cannot be read).</summary>
        public static HdrDatabase? LoadCached(Action<string>? log = null)
        {
            try
            {
                return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath)) : null;
            }
            catch (Exception ex)
            {
                log?.Invoke($"The saved HDR games list could not be read: {ex.Message}");
                return null;
            }
        }

        /// <summary>Downloads the current list from GitHub and saves it. Throws with a readable message when that fails.</summary>
        public static async Task<HdrDatabase> DownloadAsync(CancellationToken ct)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = MaxBytes };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SmartHdrManager/{Updater.CurrentVersionText}");
            using var response = await http.GetAsync(DownloadUrl, ct).ConfigureAwait(false);
            int status = (int)response.StatusCode;
            if (status == 404) throw new InvalidOperationException("The HDR games list is not published on GitHub yet.");
            if (status == 403 || status == 429) throw new InvalidOperationException("GitHub refused the request for now (too many requests). Try again later.");
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub answered with error {status}.");
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var db = Parse(json);   // never replace a good list with a broken download
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            string temp = CachePath + ".tmp";
            await File.WriteAllTextAsync(temp, json, ct).ConfigureAwait(false);
            File.Move(temp, CachePath, overwrite: true);
            return db;
        }

        // Store suffixes of Xbox / Microsoft Store titles that the wiki does not use ("Frostpunk 2: PC Edition")
        private static readonly Regex StoreSuffix = new(@"\s*[-–:]?\s*(PC Edition|Windows Edition|Windows 10 Edition|for Windows 10|for Windows|\(PC\)|\(Windows\)|\(Win\))\s*$",
                                                        RegexOptions.IgnoreCase);

        /// <summary>The list's entry for a game on this PC: by Steam or GOG id first, otherwise by name.</summary>
        public HdrMatch? Find(LibraryGame game)
        {
            string source = string.Empty;
            int colon = game.Id.IndexOf(':');
            if (colon > 0)
            {
                source = game.Id.Substring(0, colon);
                string id = game.Id.Substring(colon + 1);
                if (source == "steam" && _steam.TryGetValue(id, out var steam)) return steam;
                if (source == "gog" && _gog.TryGetValue(id, out var gog)) return gog;
            }

            string key = KeyOf(game.Name);
            if (key.Length == 0) return null;
            if (!_names.TryGetValue(key, out var byName))
            {
                string shortKey = KeyOf(StoreSuffix.Replace(game.Name, string.Empty));
                if (shortKey.Length == 0 || shortKey == key || !_names.TryGetValue(shortKey, out byName)) return null;
            }

            // A Steam / GOG game whose id the wiki does not list for that game is another game with the same name
            // (e.g. Prey from 2006 and "Prey (2017)")
            if ((source == "steam" && byName.HasSteamIds) || (source == "gog" && byName.HasGogIds)) return null;
            return byName;
        }

        private static string Text(JsonElement e, string property) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString()?.Trim() ?? string.Empty
                : string.Empty;

        private static IEnumerable<string> Ids(JsonElement e, string property)
        {
            if (!e.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
            return list.EnumerateArray()
                       .Where(v => v.ValueKind == JsonValueKind.String)
                       .Select(v => v.GetString()?.Trim() ?? string.Empty)
                       .Where(v => v.Length > 0)
                       .ToList();
        }
    }
}
