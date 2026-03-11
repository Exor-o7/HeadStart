// Copyright (c) HeadStart Mod. All rights reserved.
// Drop this file into your server's Mods/UserCode/ folder.
//
// ─────────────────────────────────────────────────────────────────────────────
// FEATURE 1 — Star Management
//   • /givestar <playerName> [count]   — Admin grants available stars to a player.
//   • /givexp   <playerName> <amount>  — Admin grants overall XP to a player.
//   • New players automatically receive configurable stars on their very first join.
//
// FEATURE 2 — Catch-Up XP
//   • When an abandoned player logs in, their XP is compared against the average
//     XP of all currently active, non-abandoned players.
//   • If they are below the average, they receive exactly enough XP to reach it.
//   • Catch-up can be granted multiple times (configurable) each time a player
//     returns from abandoned status.
//   • /catchup  <playerName>           — Admin forces catch-up for any player.
//
// CONFIG — Storage/Mods/HeadStart/config.txt
//   WelcomeStarCount   : stars granted to first-time joiners  (default: 1)
//   MaxHeadStartGrants  : max automatic catch-ups per player   (default: 1)
//                         Set to 0 for unlimited.
//
// PERSISTENCE — Per-player folders under Storage/Mods/HeadStart/Players/<Name>/
//   Each player gets a subfolder with marker files (welcome.granted, headstart.count)
//   to prevent duplicate grants across server restarts.
// ─────────────────────────────────────────────────────────────────────────────

namespace Eco.Mods.HeadStart
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Eco.Core.Plugins.Interfaces;
    using Eco.Core.Utils;
    using Eco.Gameplay.Players;
    using Eco.Gameplay.Systems.Messaging.Chat.Commands;
    using Eco.Shared.Localization;

    // ─────────────────────────────────────────────────────────────────────────
    // Configuration — loaded from Storage/Mods/HeadStart/config.json
    // ─────────────────────────────────────────────────────────────────────────
    public class HeadStartConfig
    {
        /// <summary>Number of stars granted to first-time joiners. Default: 1.</summary>
        public int WelcomeStarCount { get; set; } = 1;

        /// <summary>
        /// Maximum number of times a player can receive automatic catch-up XP.
        /// Each time they return from abandoned status counts as one use.
        /// Set to 0 for unlimited. Default: 1.
        /// </summary>
        public int MaxHeadStartGrants { get; set; } = 1;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Plugin registration — ECO discovers this class at server startup.
    // ─────────────────────────────────────────────────────────────────────────
    public class HeadStartPlugin : IModKitPlugin, IInitializablePlugin, IShutdownablePlugin
    {
        // ── IServerPlugin ──────────────────────────────────────────────────────
        public string GetStatus()   => "HeadStart mod active.";
        public string GetCategory() => "Mods";
        public override string ToString() => "HeadStart";

        // ── Star XP table ──────────────────────────────────────────────────────
        // Cumulative XP required to reach each star level (1-based).
        // After star 8 each additional star costs 2 000 XP.
        private static readonly float[] _starCumulativeXP =
        {
            0f,    // star 1  (starting / free)
            25f,   // star 2  (+25)
            100f,  // star 3  (+75)
            250f,  // star 4  (+150)
            500f,  // star 5  (+250)
            1000f, // star 6  (+500)
            2000f, // star 7  (+1 000)
            4000f, // star 8  (+2 000)
        };

        /// <summary>Returns the cumulative XP needed to reach star <paramref name="star"/>.</summary>
        private static float CumulativeXPForStar(int star)
        {
            if (star <= 1) return 0f;
            if (star <= 8) return _starCumulativeXP[star - 1];
            return _starCumulativeXP[7] + (star - 8) * 2000f;
        }

        /// <summary>
        /// Computes a player's total lifetime XP: cumulative XP for their current
        /// star level + the partial XP they've earned toward the next star.
        /// <c>user.UserXP.XP</c> only returns current-level XP, so we must add
        /// the cumulative base for their <c>TotalStarsEarned</c>.
        /// </summary>
        internal static float GetTotalXP(User user)
            => CumulativeXPForStar(user.UserXP.TotalStarsEarned) + user.UserXP.XP;

        // ── Configuration ──────────────────────────────────────────────────
        private static HeadStartConfig _config = new();
        private static readonly string _configPath =
            Path.Combine("Storage", "Mods", "HeadStart", "config.txt");

        // Track users who have already received welcome star (boolean).
        // Track how many times each user has received head-start catch-up (count).
        private static readonly HashSet<string> _welcomeStarGranted = new();
        private static readonly Dictionary<string, int> _headStartGrantCount = new();
        private static readonly object _lock = new();

        // Per-player folder root: Storage/Mods/HeadStart/Players/<PlayerName>/
        private static readonly string _playersDir =
            Path.Combine("Storage", "Mods", "HeadStart", "Players");
        private static readonly string _logPath =
            Path.Combine("Storage", "Mods", "HeadStart", "headstart.log");

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public void Initialize(TimedTask timer)
        {
            // Load config (creates default if missing).
            LoadConfig();
            // Load previously granted sets from per-player folders.
            LoadAllGrants();

            // Feature 1: NewUserJoinedEvent fires once per player.
            UserManager.NewUserJoinedEvent.Add(OnNewUserJoined);
            // Feature 2: head-start XP runs on every login for abandoned players.
            UserManager.OnUserLoggedIn.Add(OnUserLoggedIn);

            LogMessage($"Plugin initialized. Config: WelcomeStarCount={_config.WelcomeStarCount}, MaxHeadStartGrants={_config.MaxHeadStartGrants}");
        }

        public Task ShutdownAsync()
        {
            UserManager.NewUserJoinedEvent.Remove(OnNewUserJoined);
            UserManager.OnUserLoggedIn.Remove(OnUserLoggedIn);
            LogMessage("Plugin shut down.");
            return Task.CompletedTask;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Config — Storage/Mods/HeadStart/config.txt
        // Format:  Key=Value  (one per line, # for comments)
        // ─────────────────────────────────────────────────────────────────────
        private static void LoadConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                if (File.Exists(_configPath))
                {
                    _config = new HeadStartConfig();
                    foreach (string raw in File.ReadAllLines(_configPath))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;
                        string key   = line.Substring(0, eq).Trim();
                        string value = line.Substring(eq + 1).Trim();
                        if (key.Equals("WelcomeStarCount", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(value, out int wsc))
                            _config.WelcomeStarCount = wsc;
                        else if (key.Equals("MaxHeadStartGrants", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(value, out int mhsg))
                            _config.MaxHeadStartGrants = mhsg;
                    }
                }
                else
                {
                    _config = new HeadStartConfig();
                    SaveConfig();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"WARN  Error loading config, using defaults: {ex.Message}");
                _config = new HeadStartConfig();
            }
        }

        private static void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
                File.WriteAllText(_configPath,
                    $"# HeadStart Mod Configuration{Environment.NewLine}" +
                    $"# Lines starting with # are comments.{Environment.NewLine}" +
                    $"{Environment.NewLine}" +
                    $"# Number of stars granted to first-time joiners (default: 1){Environment.NewLine}" +
                    $"WelcomeStarCount={_config.WelcomeStarCount}{Environment.NewLine}" +
                    $"{Environment.NewLine}" +
                    $"# Max automatic head-start catch-ups per player (default: 1, 0 = unlimited){Environment.NewLine}" +
                    $"MaxHeadStartGrants={_config.MaxHeadStartGrants}{Environment.NewLine}");
            }
            catch (Exception ex)
            {
                LogMessage($"WARN  Error saving config: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Logging helper — writes to Storage/Mods/HeadStart/headstart.log
        // ─────────────────────────────────────────────────────────────────────
        internal static void LogMessage(string message)
        {
            try
            {
                string entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, entry);
            }
            catch { /* last-resort: don't crash the server over logging */ }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Per-player folder persistence
        // ─────────────────────────────────────────────────────────────────────
        private static string SanitizeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        private static string GetPlayerDir(User user)
            => Path.Combine(_playersDir, SanitizeName(user.Name));

        /// <summary>Scan all per-player folders at startup and populate in-memory caches.</summary>
        private static void LoadAllGrants()
        {
            lock (_lock)
            {
                _welcomeStarGranted.Clear();
                _headStartGrantCount.Clear();

                if (!Directory.Exists(_playersDir)) return;

                foreach (string playerDir in Directory.GetDirectories(_playersDir))
                {
                    // Welcome — boolean marker file.
                    string welcomePath = Path.Combine(playerDir, "welcome.granted");
                    if (File.Exists(welcomePath))
                    {
                        try
                        {
                            string id = File.ReadAllLines(welcomePath).FirstOrDefault()?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(id))
                                _welcomeStarGranted.Add(id);
                        }
                        catch (Exception ex) { LogMessage($"WARN  Error reading {welcomePath}: {ex.Message}"); }
                    }

                    // Head-start — count file.  Line 1 = StrangeId, Line 4 = count.
                    string headStartPath = Path.Combine(playerDir, "headstart.count");
                    if (File.Exists(headStartPath))
                    {
                        try
                        {
                            string[] lines = File.ReadAllLines(headStartPath);
                            string id = lines.Length > 0 ? lines[0].Trim() : "";
                            int count = lines.Length > 3 && int.TryParse(lines[3].Trim(), out int c) ? c : 1;
                            if (!string.IsNullOrWhiteSpace(id))
                                _headStartGrantCount[id] = count;
                        }
                        catch (Exception ex) { LogMessage($"WARN  Error reading {headStartPath}: {ex.Message}"); }
                    }

                    // Migrate old catchup.granted → headstart.count (count = 1).
                    string oldPath = Path.Combine(playerDir, "catchup.granted");
                    if (File.Exists(oldPath) && !File.Exists(headStartPath))
                    {
                        try
                        {
                            string[] lines = File.ReadAllLines(oldPath);
                            string id = lines.Length > 0 ? lines[0].Trim() : "";
                            string name = lines.Length > 1 ? lines[1].Trim() : "unknown";
                            if (!string.IsNullOrWhiteSpace(id))
                            {
                                _headStartGrantCount[id] = 1;
                                File.WriteAllText(headStartPath,
                                    $"{id}{Environment.NewLine}{name}{Environment.NewLine}{DateTime.UtcNow:O}{Environment.NewLine}1");
                                File.Delete(oldPath);
                                LogMessage($"Migrated old catchup.granted → headstart.count for {name}");
                            }
                        }
                        catch (Exception ex) { LogMessage($"WARN  Error migrating {oldPath}: {ex.Message}"); }
                    }

                    // Migrate old catchup.count → headstart.count.
                    string oldCountPath = Path.Combine(playerDir, "catchup.count");
                    if (File.Exists(oldCountPath) && !File.Exists(headStartPath))
                    {
                        try
                        {
                            File.Move(oldCountPath, headStartPath);
                            string[] lines = File.ReadAllLines(headStartPath);
                            string id = lines.Length > 0 ? lines[0].Trim() : "";
                            int count = lines.Length > 3 && int.TryParse(lines[3].Trim(), out int c) ? c : 1;
                            if (!string.IsNullOrWhiteSpace(id))
                                _headStartGrantCount[id] = count;
                            LogMessage($"Migrated old catchup.count → headstart.count in {playerDir}");
                        }
                        catch (Exception ex) { LogMessage($"WARN  Error migrating {oldCountPath}: {ex.Message}"); }
                    }
                }

                LogMessage($"Loaded grants — welcome: {_welcomeStarGranted.Count}, head-start entries: {_headStartGrantCount.Count}");
            }
        }

        private static void SaveWelcomeGrant(User user)
        {
            try
            {
                string dir = GetPlayerDir(user);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "welcome.granted"),
                    $"{user.StrangeId}{Environment.NewLine}{user.Name}{Environment.NewLine}{DateTime.UtcNow:O}");
            }
            catch (Exception ex)
            {
                LogMessage($"WARN  Error saving welcome grant for {user.Name}: {ex.Message}");
            }
        }

        private static void SaveHeadStartGrant(User user, int count)
        {
            try
            {
                string dir = GetPlayerDir(user);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "headstart.count"),
                    $"{user.StrangeId}{Environment.NewLine}{user.Name}{Environment.NewLine}{DateTime.UtcNow:O}{Environment.NewLine}{count}");
            }
            catch (Exception ex)
            {
                LogMessage($"WARN  Error saving head-start grant for {user.Name}: {ex.Message}");
            }
        }

        private static void RemoveGrantFile(string strangeId, string fileName)
        {
            try
            {
                if (!Directory.Exists(_playersDir)) return;
                foreach (string playerDir in Directory.GetDirectories(_playersDir))
                {
                    string filePath = Path.Combine(playerDir, fileName);
                    if (!File.Exists(filePath)) continue;
                    string firstLine = File.ReadAllLines(filePath).FirstOrDefault()?.Trim() ?? "";
                    if (firstLine == strangeId)
                    {
                        File.Delete(filePath);
                        LogMessage($"Removed {fileName} for StrangeId {strangeId} in {playerDir}");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"WARN  Error removing {fileName}: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Async event wrappers — wait for UserXP to be ready, then act.
        // ─────────────────────────────────────────────────────────────────────
        private static void OnNewUserJoined(User user)
        {
            // Run on a background thread so the event handler returns immediately
            // and we can safely await UserXP initialisation.
            Task.Run(async () =>
            {
                try
                {
                    await WaitForUserXP(user);
                    GrantWelcomeStar(user);
                }
                catch (Exception ex)
                {
                    LogMessage($"ERROR OnNewUserJoined for {user.Name}: {ex}");
                }
            });
        }

        private static void OnUserLoggedIn(User user)
        {
            // Capture abandoned status NOW, before the async delay.
            // ECO clears the abandoned flag once the player logs in, so by
            // the time WaitForUserXP finishes it would already be false.
            bool wasAbandoned = user.IsAbandoned;

            Task.Run(async () =>
            {
                try
                {
                    await WaitForUserXP(user);
                    // GrantWelcomeStar is idempotent (_welcomeStarGranted guards it),
                    // so calling it on every login safely covers players who joined
                    // before this mod was installed.
                    GrantWelcomeStar(user);
                    RunHeadStart(user, wasAbandoned: wasAbandoned, isNewPlayer: false);
                }
                catch (Exception ex)
                {
                    LogMessage($"ERROR OnUserLoggedIn for {user.Name}: {ex}");
                }
            });
        }

        /// <summary>Polls until user.UserXP is non-null (up to 10 s).</summary>
        private static async Task WaitForUserXP(User user)
        {
            for (int i = 0; i < 20 && user.UserXP == null; i++)
                await Task.Delay(500);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Feature 1 — Welcome star on very first login
        // ─────────────────────────────────────────────────────────────────────
        private static void GrantWelcomeStar(User user)
        {
            string userId = user.StrangeId;

            // Atomic check-and-reserve to prevent double grants when
            // NewUserJoinedEvent and OnUserLoggedIn fire concurrently.
            lock (_lock)
            {
                if (_welcomeStarGranted.Contains(userId)) return;
                _welcomeStarGranted.Add(userId);
            }

            if (user.UserXP == null)
            {
                // Roll back the reservation so it can be retried on next login.
                lock (_lock) { _welcomeStarGranted.Remove(userId); }
                LogMessage($"WARN  Cannot grant welcome star to {user.Name}: UserXP is still null after waiting.");
                return;
            }

            // Grant configurable number of stars.
            int starCount = _config.WelcomeStarCount;
            if (starCount > 0)
                user.UserXP.AddStars(starCount);

            SaveWelcomeGrant(user);

            LogMessage($"Granted {starCount} welcome star(s) to {user.Name} (StrangeId: {userId}).");
            user.MsgLocStr($"Welcome! You've been granted {starCount} star(s) to help you get started.");

            // Also run head-start catch-up for new players — they aren't flagged abandoned yet.
            try { RunHeadStart(user, wasAbandoned: false, isNewPlayer: true); }
            catch (Exception ex) { LogMessage($"ERROR head-start during welcome for {user.Name}: {ex}"); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Feature 2 — Head-start XP on login for abandoned players
        // ─────────────────────────────────────────────────────────────────────
        private static void RunHeadStart(User user, bool wasAbandoned = false, bool isNewPlayer = false)
        {
            // For returning players, only process those who were in the abandoned
            // demographic at the moment they logged in (before ECO clears the flag).
            if (!isNewPlayer && !wasAbandoned) return;

            string userId = user.StrangeId;

            // Atomic check: has this player used all their allowed head-start grants?
            int currentCount;
            lock (_lock)
            {
                _headStartGrantCount.TryGetValue(userId, out currentCount);
                int max = _config.MaxHeadStartGrants;
                if (max > 0 && currentCount >= max) return; // 0 = unlimited
                _headStartGrantCount[userId] = currentCount + 1;
            }

            if (user.UserXP == null)
            {
                // Roll back the increment so it can be retried.
                lock (_lock)
                {
                    if (currentCount == 0) _headStartGrantCount.Remove(userId);
                    else _headStartGrantCount[userId] = currentCount;
                }
                LogMessage($"WARN  Cannot run head-start for {user.Name}: UserXP is null.");
                return;
            }

            // Collect active, non-abandoned players to build the XP baseline.
            var activePlayers = UserManager.Users
                .Where(u => u.IsActive && !u.IsAbandoned && u != user && u.UserXP != null)
                .ToList();

            if (activePlayers.Count == 0)
            {
                // Roll back — no baseline to compare against.
                lock (_lock)
                {
                    if (currentCount == 0) _headStartGrantCount.Remove(userId);
                    else _headStartGrantCount[userId] = currentCount;
                }
                LogMessage($"No active players to compare for {user.Name} — skipping head-start.");
                return;
            }

            float averageTotalXP = activePlayers.Average(u => GetTotalXP(u));
            float currentTotalXP = GetTotalXP(user);

            if (currentTotalXP >= averageTotalXP)
            {
                // Roll back — player is already at or above average.
                lock (_lock)
                {
                    if (currentCount == 0) _headStartGrantCount.Remove(userId);
                    else _headStartGrantCount[userId] = currentCount;
                }
                LogMessage($"{user.Name} already at/above average total XP ({currentTotalXP:F0} >= {averageTotalXP:F0}).");
                return;
            }

            // Grant exactly enough XP to reach the active-player average total XP.
            float boost = averageTotalXP - currentTotalXP;
            user.UserXP.AddExperience(boost);

            int newCount = currentCount + 1;
            SaveHeadStartGrant(user, newCount);

            string usesInfo = _config.MaxHeadStartGrants > 0
                ? $" (grant {newCount}/{_config.MaxHeadStartGrants})"
                : $" (grant #{newCount})";

            string intro = isNewPlayer
                ? "Welcome! The server has other active players, so you've been given catch-up XP to help you keep up."
                : "You've been away for a while!";

            LogMessage($"Head-start for {user.Name}: {boost:F0} XP (avg total {averageTotalXP:F0}){usesInfo}.");
            user.MsgLocStr(
                $"{intro} You received {boost:F0} catch-up XP " +
                $"to bring you up to the server average ({averageTotalXP:F0} total XP).");
        }

        internal static bool ResetWelcome(string userId)
        {
            lock (_lock) { if (!_welcomeStarGranted.Remove(userId)) return false; }
            RemoveGrantFile(userId, "welcome.granted");
            LogMessage($"Reset welcome grant for StrangeId {userId}.");
            return true;
        }

        internal static bool ResetHeadStart(string userId)
        {
            lock (_lock) { if (!_headStartGrantCount.Remove(userId)) return false; }
            RemoveGrantFile(userId, "headstart.count");
            LogMessage($"Reset head-start grant count for StrangeId {userId}.");
            return true;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Admin chat commands
    // ─────────────────────────────────────────────────────────────────────────
    [ChatCommandHandler]
    public class HeadStartCommands
    {
        /// <summary>
        /// Grants a number of usable stars to the target player.
        /// Usage: /givestar &lt;playerName&gt; [count]
        /// </summary>
        [ChatCommand("Grant stars to a player", ChatAuthorizationLevel.Admin)]
        public static void GiveStar(User callingUser, string targetPlayerName, int count = 1)
        {
            var target = UserManager.FindUserByName(targetPlayerName);
            if (target == null)
            {
                callingUser.ErrorLocStr($"Player '{targetPlayerName}' not found.");
                return;
            }

            if (count <= 0)
            {
                callingUser.ErrorLocStr("Star count must be at least 1.");
                return;
            }

            if (target.UserXP == null)
            {
                callingUser.ErrorLocStr($"{target.Name}'s XP data is not loaded yet. Try again shortly.");
                return;
            }

            target.UserXP.AddStars(count);
            callingUser.MsgLocStr($"Granted {count} star(s) to {target.Name}.");
            target.MsgLocStr($"An admin has granted you {count} star(s)!");
        }

        /// <summary>
        /// Grants a specific amount of overall XP to the target player.
        /// Usage: /givexp &lt;playerName&gt; &lt;amount&gt;
        /// </summary>
        [ChatCommand("Grant XP to a player", ChatAuthorizationLevel.Admin)]
        public static void GiveXP(User callingUser, string targetPlayerName, float amount)
        {
            var target = UserManager.FindUserByName(targetPlayerName);
            if (target == null)
            {
                callingUser.ErrorLocStr($"Player '{targetPlayerName}' not found.");
                return;
            }

            if (amount <= 0f)
            {
                callingUser.ErrorLocStr("XP amount must be positive.");
                return;
            }

            if (target.UserXP == null)
            {
                callingUser.ErrorLocStr($"{target.Name}'s XP data is not loaded yet. Try again shortly.");
                return;
            }

            target.UserXP.AddExperience(amount);
            callingUser.MsgLocStr($"Granted {amount:F0} XP to {target.Name}.");
            target.MsgLocStr($"An admin has granted you {amount:F0} XP!");
        }

        /// <summary>
        /// Manually runs the catch-up calculation for any player, regardless of
        /// whether they are abandoned.  Useful for testing or edge cases.
        /// Usage: /catchup &lt;playerName&gt;
        /// </summary>
        [ChatCommand("Force catch-up XP for a player", ChatAuthorizationLevel.Admin)]
        public static void CatchUp(User callingUser, string targetPlayerName)
        {
            var target = UserManager.FindUserByName(targetPlayerName);
            if (target == null)
            {
                callingUser.ErrorLocStr($"Player '{targetPlayerName}' not found.");
                return;
            }

            // Compare against ALL active players (abandoned flag ignored here so
            // admins can trigger it manually for any lagging player).
            var activePlayers = UserManager.Users
                .Where(u => u.IsActive && u != target)
                .ToList();

            if (activePlayers.Count == 0)
            {
                callingUser.ErrorLocStr("No active players found to calculate an average from.");
                return;
            }

            if (target.UserXP == null)
            {
                callingUser.ErrorLocStr($"{target.Name}'s XP data is not loaded yet. Try again shortly.");
                return;
            }

            float averageTotalXP = activePlayers.Average(u => HeadStartPlugin.GetTotalXP(u));
            float currentTotalXP = HeadStartPlugin.GetTotalXP(target);

            if (currentTotalXP >= averageTotalXP)
            {
                callingUser.MsgLocStr(
                    $"{target.Name} already has {currentTotalXP:F0} total XP — at or above the active " +
                    $"average of {averageTotalXP:F0} total XP. No XP granted.");
                return;
            }

            float boost = averageTotalXP - currentTotalXP;
            target.UserXP.AddExperience(boost);

            HeadStartPlugin.LogMessage($"Admin catch-up for {target.Name}: {boost:F0} XP (avg total {averageTotalXP:F0}).");

            callingUser.MsgLocStr(
                $"Catch-up applied: {target.Name} received {boost:F0} XP " +
                $"(server average: {averageTotalXP:F0} total XP).");
            target.MsgLocStr(
                $"An admin applied catch-up: you received {boost:F0} XP " +
                $"to reach the server average ({averageTotalXP:F0} total XP).");
        }

        /// <summary>
        /// Resets the welcome-star grant for a player so it fires again on next join.
        /// Usage: /resetwelcome &lt;playerName&gt;
        /// </summary>
        [ChatCommand("Reset welcome star grant for a player", ChatAuthorizationLevel.Admin)]
        public static void ResetWelcome(User callingUser, string targetPlayerName)
        {
            var target = UserManager.FindUserByName(targetPlayerName);
            if (target == null) { callingUser.ErrorLocStr($"Player '{targetPlayerName}' not found."); return; }
            bool removed = HeadStartPlugin.ResetWelcome(target.StrangeId);
            callingUser.MsgLocStr(removed
                ? $"Welcome star reset for {target.Name}. They will receive it again on next join."
                : $"{target.Name} has not received a welcome star yet.");
        }

        /// <summary>
        /// Resets the head-start grant for a player so it fires again on next login.
        /// Usage: /resetcatchup &lt;playerName&gt;
        /// </summary>
        [ChatCommand("Reset catch-up XP grant for a player", ChatAuthorizationLevel.Admin)]
        public static void ResetCatchUp(User callingUser, string targetPlayerName)
        {
            var target = UserManager.FindUserByName(targetPlayerName);
            if (target == null) { callingUser.ErrorLocStr($"Player '{targetPlayerName}' not found."); return; }
            bool removed = HeadStartPlugin.ResetHeadStart(target.StrangeId);
            callingUser.MsgLocStr(removed
                ? $"Catch-up reset for {target.Name}. They will receive it again on next login."
                : $"{target.Name} has not received catch-up XP yet.");
        }
    }
}
