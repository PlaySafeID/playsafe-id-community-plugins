// ===========================================================================
// PlaySafe ID — Community Server Plugin for Rust (Oxide/uMod)
// Version: 2.0.0
// ===========================================================================
//
// DISCLAIMER
//
// This plugin is provided as an optional community resource and is NOT
// an official PlaySafe ID product. PlaySafe ID Ltd. ("PlaySafe ID") does
// not warrant, endorse, guarantee, or assume responsibility for this
// plugin. It is provided "as is" without warranty of any kind, either
// express or implied, including but not limited to the implied warranties
// of merchantability, fitness for a particular purpose, or
// non-infringement.
//
// PlaySafe ID is not responsible for the creation, maintenance, support,
// or ongoing development of this plugin. PlaySafe ID shall not be held
// responsible or liable for any alteration, modification, redistribution,
// or use of this plugin by any party, including but not limited to server
// owners, administrators, developers, or any other third party.
//
// By using this plugin, you acknowledge and agree that:
//
//   1. You use this plugin entirely at your own risk.
//   2. PlaySafe ID is not liable for any damages, losses, disruptions,
//      or issues arising from the use, misuse, modification, or
//      inability to use this plugin.
//   3. You are solely responsible for reviewing, testing, and validating
//      this plugin before deploying it on any server or environment.
//   4. Any modifications you make to this plugin are your own
//      responsibility. PlaySafe ID bears no liability for the
//      consequences of altered code.
//   5. This plugin interacts with the PlaySafe ID Community API, which
//      is subject to its own terms of service and usage policies.
//      Access to and use of the API is governed separately.
//
// This disclaimer applies regardless of whether the plugin is used in
// its original form or has been modified. If you do not agree with these
// terms, do not use this plugin.
//
// ===========================================================================
//
// Integrates PlaySafe ID verification into Rust community servers.
//
// Features:
//   - Validates player status on connect via the PlaySafe ID Community API
//   - Kicks players who are not ACTIVE (PERM, TEMP, LOCKED, UNVERIFIED, or not found)
//   - Periodic re-check of all connected players
//   - Configurable Oxide group assignment on verification success/failure
//   - Fallback mode: if the PlaySafe ID API is unreachable, players with the
//     configured Oxide group are allowed in; those without are denied
//   - Admin/whitelist SteamID bypass
//   - Silent kicks (no server chat announcement)
//   - Automatic reporting of anti-cheat bans (EAC, VAC, DMA) to PlaySafe ID
//   - Admin commands (/psidban, /psidunban) for explicit ban management
//
// Constraints (community guideline compliance):
//   - Does NOT modify any game UI
//   - Does NOT inject messages into the loading screen
// ===========================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("PlaySafeID", "PlaySafe ID", "2.0.0")]
    [Description("Enforces PlaySafe ID verification for all players on a Rust community server.")]
    public class PlaySafeID : RustPlugin
    {
        // ===================================================================
        // CONFIGURATION
        // ===================================================================
        #region Configuration

        private PluginConfig _config;

        private class PluginConfig
        {
            [JsonProperty("PlaySafe ID Community API Key (stored in plain text — restrict file permissions on oxide/config/)")]
            public string ApiKey { get; set; } = "YOUR_COMMUNITY_API_KEY";

            [JsonProperty("Game Code (must match the code registered with PlaySafe ID)")]
            public string GameCode { get; set; } = "RUST";

            [JsonProperty("Periodic Re-check Interval (seconds)")]
            public float RecheckIntervalSeconds { get; set; } = 7200f;

            [JsonProperty("API Request Timeout (seconds)")]
            public float TimeoutSeconds { get; set; } = 10f;

            [JsonProperty("Kick Message — Not Verified")]
            public string KickMessageNotVerified { get; set; } =
                "You do not have an Active PlaySafe ID. Please register first before joining this server: playsafeid.com";

            [JsonProperty("Kick Message — API Unreachable (fallback deny)")]
            public string KickMessageApiDown { get; set; } =
                "PlaySafe ID verification is temporarily unavailable. Please try again shortly.";

            [JsonProperty("Oxide Group — Assign on ACTIVE status")]
            public string OxideGroupVerified { get; set; } = "playsafeid.verified";

            [JsonProperty("Remove Oxide Group on non-ACTIVE status")]
            public bool RemoveGroupOnFail { get; set; } = true;

            [JsonProperty("Fallback Mode — Allow players with Oxide group when API is down")]
            public bool FallbackAllowByGroup { get; set; } = true;

            [JsonProperty("Whitelisted SteamIDs (bypass all checks)")]
            public List<string> WhitelistedSteamIds { get; set; } = new List<string>();

            [JsonProperty("Allow Oxide/Server Admins and Moderators to bypass checks")]
            public bool AllowAdminsAndModeratorsBypass { get; set; } = true;

            [JsonProperty("Log events to server console")]
            public bool LogEvents { get; set; } = true;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
            PrintWarning("Default configuration created. Set your Community API Key in oxide/config/PlaySafeID.json");
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception();
            }
            catch
            {
                PrintWarning("Invalid or missing configuration — regenerating defaults.");
                LoadDefaultConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        // ===================================================================
        // CONSTANTS
        // ===================================================================
        #region Constants

        private const string API_BASE = "https://community.playsafeid.com/v1/community";
        private const string PLATFORM = "STEAM";

        #endregion

        // ===================================================================
        // LIFECYCLE
        // ===================================================================
        #region Lifecycle

        private Timer _recheckTimer;

        // Stores the last ban lookup per admin so they can reference bans by number
        // Key: admin ID (IPlayer.Id), Value: list of ban dictionaries from last lookup
        private Dictionary<string, List<Dictionary<string, object>>> _banLookupCache
            = new Dictionary<string, List<Dictionary<string, object>>>();
        private Dictionary<string, DateTime> _banLookupTimestamps
            = new Dictionary<string, DateTime>();
        private const int BAN_CACHE_STALE_MINUTES = 5;

        private void Init()
        {
            // Ensure the configured Oxide group exists
            if (!string.IsNullOrEmpty(_config.OxideGroupVerified))
            {
                permission.CreateGroup(_config.OxideGroupVerified, "PlaySafe ID Verified", 0);
            }

            AddCovalenceCommand("psidban", nameof(CommandBan));
            AddCovalenceCommand("psidunban", nameof(CommandUnban));
        }

        private void OnServerInitialized()
        {
            _recheckTimer = timer.Every(_config.RecheckIntervalSeconds, PeriodicRecheck);
            Puts($"PlaySafe ID v2.0.0 loaded. Re-check every {_config.RecheckIntervalSeconds / 60f} min.");
        }

        private void Unload()
        {
            _recheckTimer?.Destroy();
            _banLookupCache?.Clear();
            _banLookupTimestamps?.Clear();
        }

        #endregion

        // ===================================================================
        // PLAYER CONNECTION
        // ===================================================================
        #region Player Connection

        /// <summary>
        /// Called after a player has connected. We verify their PlaySafe ID
        /// status asynchronously and kick if not ACTIVE.
        ///
        /// We intentionally use OnPlayerConnected rather than CanClientLogin
        /// to avoid injecting any message into the loading/connecting screen
        /// (community guideline compliance).
        /// </summary>
        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;

            string steamId = player.UserIDString;
            string name = player.displayName;

            // --- Bypass: whitelisted SteamIDs ---
            if (IsWhitelisted(steamId))
            {
                Log($"{name} ({steamId}) is whitelisted — bypassing.");
                return;
            }

            // --- Bypass: server admins ---
            if (_config.AllowAdminsAndModeratorsBypass && IsAdminOrModerator(player))
            {
                Log($"{name} ({steamId}) is an admin/moderator — bypassing.");
                return;
            }

            // --- API check ---
            GetUserStatus(steamId, (status) =>
            {
                try
                {
                    if (player == null || !player.IsConnected) return;

                    if (IsActiveStatus(status))
                    {
                        AssignOxideGroup(steamId);
                        Log($"{name} ({steamId}) verified — status: {status}.");
                    }
                    else
                    {
                        RemoveOxideGroup(steamId);
                        SilentKick(player, _config.KickMessageNotVerified);
                        Log($"Kicked {name} ({steamId}) — status: {status ?? "NOT_FOUND"}.");
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"[PlaySafe ID] Exception in status handler for {name} ({steamId}): {ex}");
                }
            },
            () =>
            {
                try
                {
                    if (player == null || !player.IsConnected) return;
                    HandleApiFallback(player, steamId, name);
                }
                catch (Exception ex)
                {
                    PrintWarning($"[PlaySafe ID] Exception in error handler for {name} ({steamId}): {ex}");
                }
            });
        }

        #endregion

        // ===================================================================
        // PERIODIC RE-CHECK
        // ===================================================================
        #region Periodic Recheck

        private void PeriodicRecheck()
        {
            var players = BasePlayer.activePlayerList;
            if (players == null || players.Count == 0) return;

            // Collect eligible players (not whitelisted, not admin)
            var eligiblePlayers = new Dictionary<string, BasePlayer>();
            foreach (BasePlayer player in players.ToArray())
            {
                if (player == null || !player.IsConnected) continue;
                string steamId = player.UserIDString;
                if (IsWhitelisted(steamId)) continue;
                if (_config.AllowAdminsAndModeratorsBypass && IsAdminOrModerator(player)) continue;
                eligiblePlayers[steamId] = player;
            }

            if (eligiblePlayers.Count == 0) return;

            Log($"Periodic re-check starting for {eligiblePlayers.Count} eligible player(s)...");

            // Chunk into groups of BATCH_MAX_USERS and batch-check each
            var steamIds = eligiblePlayers.Keys.ToList();
            for (int i = 0; i < steamIds.Count; i += BATCH_MAX_USERS)
            {
                var chunk = steamIds.GetRange(i, Math.Min(BATCH_MAX_USERS, steamIds.Count - i));

                BatchGetUserStatus(chunk, (results) =>
                {
                    foreach (var kvp in results)
                    {
                        string steamId = kvp.Key;
                        string status = kvp.Value;

                        if (!eligiblePlayers.ContainsKey(steamId)) continue;
                        BasePlayer player = eligiblePlayers[steamId];
                        if (player == null || !player.IsConnected) continue;

                        if (IsActiveStatus(status))
                        {
                            AssignOxideGroup(steamId);
                        }
                        else
                        {
                            RemoveOxideGroup(steamId);
                            SilentKick(player, _config.KickMessageNotVerified);
                            Log($"Periodic: Kicked {player.displayName} ({steamId}) — status: {status ?? "NOT_FOUND"}.");
                        }
                    }
                },
                () =>
                {
                    // During periodic checks, API failures skip the batch rather than
                    // mass-kicking everyone during a brief outage.
                    PrintWarning("[PlaySafe ID] Periodic: Batch status check failed. Skipping batch.");
                });
            }
        }

        #endregion

        // ===================================================================
        // BAN HOOKS — AUTOMATIC ANTI-CHEAT BAN REPORTING
        // ===================================================================
        #region Ban Hooks

        /// <summary>
        /// Fires when a player is banned on the server (EAC or admin /ban).
        /// Only reports bans where the reason clearly indicates an anti-cheat
        /// detection (EAC, VAC, DMA). Other bans (toxicity, griefing, manual
        /// admin actions) are outside PlaySafe ID's scope and are skipped.
        /// Admins can still report any ban type explicitly via /psidban.
        /// </summary>
        private void OnPlayerBanned(string name, ulong steamId, string address, string reason)
        {
            if (string.IsNullOrEmpty(reason)) return;

            string banType = InferBanType(reason);
            if (banType == null)
            {
                Log($"Skipping ban for {name} ({steamId}) — reason does not match a known anti-cheat detection: {reason}");
                return;
            }

            string steamIdStr = steamId.ToString();
            string reporter = InferReporter(reason);

            SubmitCommunityBan(steamIdStr, banType, reporter, reason, null, (success, resp) =>
            {
                if (success)
                    Puts($"[PlaySafe ID] Anti-cheat ban for {name} ({steamIdStr}) reported to PlaySafe ID.");
                else
                    PrintWarning($"[PlaySafe ID] Failed to report ban for {name} ({steamIdStr}): {resp}");
            });
        }

        #endregion

        // ===================================================================
        // ADMIN COMMAND: /psidban
        // ===================================================================
        #region Admin Command

        /// <summary>
        /// Usage: /psidban &lt;steamID&gt; &lt;type&gt; &lt;reason&gt;
        ///
        /// Types: CHEATING_SOFTWARE, CHEATING_BOTTING, CHEATING_HARDWARE,
        ///        CHEATING_DMA, CHEATING_OTHER, CHILD_CSEA, CHILD_CSAM,
        ///        CHILD_GROOMING, CHILD_OTHER
        ///
        /// Submits a community ban to the PlaySafe ID API and kicks the
        /// player from this server if they are online.
        /// </summary>
        private void CommandBan(IPlayer caller, string command, string[] args)
        {
            if (!caller.IsAdmin)
            {
                caller.Reply("[PlaySafe ID] You do not have permission to use this command.");
                return;
            }

            if (args.Length < 3)
            {
                caller.Reply("[PlaySafe ID] Usage: /psidban <steamID> <type> <reason>");
                caller.Reply("  Types: CHEATING_SOFTWARE | CHEATING_BOTTING | CHEATING_HARDWARE");
                caller.Reply("         CHEATING_DMA | CHEATING_OTHER | CHILD_CSEA | CHILD_CSAM");
                caller.Reply("         CHILD_GROOMING | CHILD_OTHER");
                return;
            }

            string targetSteamId = args[0];
            string banType = args[1].ToUpper();
            string reason = string.Join(" ", args.Skip(2));

            // Validate SteamID
            if (targetSteamId.Length != 17 || !ulong.TryParse(targetSteamId, out ulong targetId))
            {
                caller.Reply("[PlaySafe ID] Invalid SteamID. Must be a 17-digit Steam64 ID.");
                return;
            }

            // Validate ban type
            string[] validTypes = {
                "CHEATING_SOFTWARE", "CHEATING_BOTTING", "CHEATING_HARDWARE",
                "CHEATING_DMA", "CHEATING_OTHER", "CHILD_CSEA", "CHILD_CSAM",
                "CHILD_GROOMING", "CHILD_OTHER"
            };

            if (!validTypes.Contains(banType))
            {
                caller.Reply($"[PlaySafe ID] Invalid ban type: {banType}. See /psidban for valid types.");
                return;
            }

            SubmitCommunityBan(targetSteamId, banType, "COMMUNITY_ADMIN", reason, null, (success, resp) =>
            {
                if (success)
                {
                    caller.Reply($"[PlaySafe ID] Ban for {targetSteamId} submitted successfully.");
                    Puts($"[PlaySafe ID] Admin {caller.Name} banned {targetSteamId}. Type: {banType}. Reason: {reason}");

                    // Kick from this server if online
                    BasePlayer target = BasePlayer.FindByID(targetId);
                    if (target != null && target.IsConnected)
                    {
                        RemoveOxideGroup(targetSteamId);
                        SilentKick(target, _config.KickMessageNotVerified);
                    }
                }
                else
                {
                    caller.Reply("[PlaySafe ID] Ban submission failed — check server logs.");
                    PrintWarning($"[PlaySafe ID] Ban submission failed for {targetSteamId}: {resp}");
                }
            });
        }

        #endregion

        // ===================================================================
        // ADMIN COMMAND: /psidunban
        // ===================================================================
        #region Admin Unban Command

        /// <summary>
        /// Two-step flow:
        ///   Step 1: /psidunban &lt;steamID&gt;              → Lists all active bans numbered (1), (2), etc.
        ///   Step 2: /psidunban &lt;number&gt; &lt;reason&gt;      → Overturns the ban at that number from the last lookup
        ///
        /// Also supports direct ban ID:
        ///   /psidunban &lt;banID&gt; &lt;reason&gt;               → Overturns by ban ID directly
        /// </summary>
        private void CommandUnban(IPlayer caller, string command, string[] args)
        {
            if (!caller.IsAdmin)
            {
                caller.Reply("[PlaySafe ID] You do not have permission to use this command.");
                return;
            }

            if (args.Length < 1)
            {
                caller.Reply("[PlaySafe ID] Usage:");
                caller.Reply("  /psidunban <steamID>           — List active bans for a player");
                caller.Reply("  /psidunban <#> <reason>        — Overturn ban # from the last lookup");
                caller.Reply("  /psidunban <banID> <reason>    — Overturn by ban ID directly");
                return;
            }

            string firstArg = args[0];

            // Detect mode: 17-digit number = SteamID lookup
            bool isSteamIdLookup = firstArg.Length == 17 && ulong.TryParse(firstArg, out _);

            // Detect mode: small number = index from last lookup
            bool isNumberedIndex = int.TryParse(firstArg, out int banIndex) && banIndex > 0 && firstArg.Length < 5;

            // ── STEP 1: Lookup bans by SteamID ──
            if (isSteamIdLookup && args.Length == 1)
            {
                string steamId = firstArg;
                caller.Reply($"[PlaySafe ID] Looking up bans for {steamId}...");

                GetUserBans(steamId, (bans) =>
                {
                    if (bans == null || bans.Count == 0)
                    {
                        caller.Reply($"[PlaySafe ID] No bans found for {steamId}.");
                        _banLookupCache.Remove(caller.Id);
                        _banLookupTimestamps.Remove(caller.Id);
                        return;
                    }

                    // Filter to only ACTIVE bans
                    var activeBans = new List<Dictionary<string, object>>();
                    foreach (var ban in bans)
                    {
                        if (ban.ContainsKey("status") && ban["status"]?.ToString()?.ToUpper() == "ACTIVE")
                        {
                            activeBans.Add(ban);
                        }
                    }

                    if (activeBans.Count == 0)
                    {
                        caller.Reply($"[PlaySafe ID] No active bans found for {steamId}. ({bans.Count} overturned/expired ban(s) exist.)");
                        _banLookupCache.Remove(caller.Id);
                        _banLookupTimestamps.Remove(caller.Id);
                        return;
                    }

                    // Cache the results and timestamp for this admin
                    _banLookupCache[caller.Id] = activeBans;
                    _banLookupTimestamps[caller.Id] = DateTime.UtcNow;

                    caller.Reply($"[PlaySafe ID] Found {activeBans.Count} active ban(s) for {steamId} (looked up just now):");
                    caller.Reply("  ───────────────────────────────────────────");

                    for (int i = 0; i < activeBans.Count; i++)
                    {
                        var ban = activeBans[i];
                        int num = i + 1;

                        string banId = ban.ContainsKey("id") ? ban["id"]?.ToString() : "unknown";
                        string banType = ban.ContainsKey("type") ? ban["type"]?.ToString() : "unknown";
                        string starts = ban.ContainsKey("startsAt") ? ban["startsAt"]?.ToString() : "unknown";
                        string game = ban.ContainsKey("gameCode") ? ban["gameCode"]?.ToString() : "unknown";

                        string reason = ExtractBanReason(ban);

                        caller.Reply($"  ({num}) {banType} | Game: {game} | Since: {starts}");
                        caller.Reply($"      Reason: {reason}");
                        caller.Reply($"      ID: {banId}");
                        caller.Reply("  ───────────────────────────────────────────");
                    }

                    caller.Reply("[PlaySafe ID] To overturn a ban, run:");
                    caller.Reply("  /psidunban <#> <reason for overturning>");
                    caller.Reply("  Example: /psidunban 1 False positive confirmed");
                },
                (error) =>
                {
                    caller.Reply($"[PlaySafe ID] Failed to look up bans: {error}");
                });
            }

            // ── STEP 2a: Overturn by numbered index from last lookup ──
            else if (isNumberedIndex && args.Length >= 2)
            {
                string reason = string.Join(" ", args.Skip(1));

                if (!_banLookupCache.ContainsKey(caller.Id) || _banLookupCache[caller.Id] == null)
                {
                    caller.Reply("[PlaySafe ID] No previous ban lookup found. Run /psidunban <steamID> first.");
                    return;
                }

                var cachedBans = _banLookupCache[caller.Id];

                // Warn if using stale cached data
                if (_banLookupTimestamps.ContainsKey(caller.Id))
                {
                    int minutesAgo = (int)(DateTime.UtcNow - _banLookupTimestamps[caller.Id]).TotalMinutes;
                    if (minutesAgo >= BAN_CACHE_STALE_MINUTES)
                        caller.Reply($"[PlaySafe ID] Warning: Ban data is from {minutesAgo} minutes ago. Run /psidunban <steamID> again for fresh results.");
                }

                if (banIndex < 1 || banIndex > cachedBans.Count)
                {
                    caller.Reply($"[PlaySafe ID] Invalid selection. Choose a number between 1 and {cachedBans.Count}.");
                    return;
                }

                var selectedBan = cachedBans[banIndex - 1];
                string banId = selectedBan.ContainsKey("id") ? selectedBan["id"]?.ToString() : null;

                if (string.IsNullOrEmpty(banId))
                {
                    caller.Reply("[PlaySafe ID] Could not determine ban ID. Try using the ban ID directly.");
                    return;
                }

                string banType = selectedBan.ContainsKey("type") ? selectedBan["type"]?.ToString() : "unknown";
                caller.Reply($"[PlaySafe ID] Overturning ban ({banIndex}): {banType} — ID: {banId}...");

                OverturnCommunityBan(banId, reason, caller.Name, (success, response) =>
                {
                    if (success)
                    {
                        caller.Reply($"[PlaySafe ID] Ban ({banIndex}) has been overturned.");
                        Puts($"[PlaySafe ID] Admin {caller.Name} overturned ban {banId}. Reason: {reason}");

                        // Remove from cache
                        if (_banLookupCache.ContainsKey(caller.Id))
                            _banLookupCache[caller.Id].RemoveAt(banIndex - 1);
                    }
                    else
                    {
                        caller.Reply($"[PlaySafe ID] Failed to overturn ban ({banIndex}) — check server logs.");
                        PrintWarning($"[PlaySafe ID] Overturn failed for {banId}: {response}");
                    }
                });
            }

            // ── STEP 2b: Overturn by direct ban ID ──
            else if (!isSteamIdLookup && !isNumberedIndex && args.Length >= 2)
            {
                string banId = firstArg;
                string reason = string.Join(" ", args.Skip(1));

                caller.Reply($"[PlaySafe ID] Overturning ban {banId}...");

                OverturnCommunityBan(banId, reason, caller.Name, (success, response) =>
                {
                    if (success)
                    {
                        caller.Reply($"[PlaySafe ID] Ban {banId} has been overturned.");
                        Puts($"[PlaySafe ID] Admin {caller.Name} overturned ban {banId}. Reason: {reason}");
                    }
                    else
                    {
                        caller.Reply($"[PlaySafe ID] Failed to overturn ban {banId} — check server logs.");
                        PrintWarning($"[PlaySafe ID] Overturn failed for {banId}: {response}");
                    }
                });
            }

            // ── Error: SteamID with extra args ──
            else if (isSteamIdLookup && args.Length >= 2)
            {
                caller.Reply("[PlaySafe ID] To overturn, use a ban number or ban ID (not SteamID).");
                caller.Reply($"  Run /psidunban {firstArg} first to list bans.");
            }
            else
            {
                caller.Reply("[PlaySafe ID] Usage:");
                caller.Reply("  /psidunban <steamID>           — List active bans for a player");
                caller.Reply("  /psidunban <#> <reason>        — Overturn ban # from the last lookup");
                caller.Reply("  /psidunban <banID> <reason>    — Overturn by ban ID directly");
            }
        }

        #endregion

        // ===================================================================
        // API METHODS
        // ===================================================================
        #region API — Get User Status

        /// <summary>
        /// GET /v1/community/user/{platform}/{platformUserId}
        /// Header: X-Api-Key
        ///
        /// 200 → { psid, platform, platformUserId, status }
        ///        status enum: ACTIVE | PERM | TEMP | LOCKED | UNVERIFIED
        /// 400 → Invalid request parameters
        /// 401 → Invalid or missing community API key
        /// 404 → User not found
        /// </summary>
        private void GetUserStatus(string steamId, Action<string> onResult, Action onError)
        {
            string url = $"{API_BASE}/user/{PLATFORM}/{steamId}";

            if (_config.LogEvents)
                Puts($"[PlaySafe ID] Requesting status: GET {url}");

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", _config.ApiKey },
                { "Accept", "application/json" }
            };

            webrequest.Enqueue(url, null, (code, body) =>
            {
                // Log every API response for debugging
                if (_config.LogEvents)
                    Puts($"[PlaySafe ID] Status API response for {steamId}: HTTP {code} — Body: {body ?? "(empty)"}");

                if (code == 404)
                {
                    // User does not exist in PlaySafe ID
                    onResult?.Invoke(null);
                    return;
                }

                if (code == 500)
                {
                    PrintWarning($"[PlaySafe ID] Server error (500) from API for {steamId}. This is a PlaySafe ID server-side issue.");
                    onError?.Invoke();
                    return;
                }

                if (code == 401)
                {
                    PrintWarning($"[PlaySafe ID] Invalid or missing API key (401). Check your config.");
                    onError?.Invoke();
                    return;
                }

                if (code == 403)
                {
                    PrintWarning($"[PlaySafe ID] Community provider account is inactive (403). Contact PlaySafe ID.");
                    onError?.Invoke();
                    return;
                }

                if (code == 429)
                {
                    PrintWarning("[PlaySafe ID] Rate limited (429). Too many requests — try again shortly.");
                    onError?.Invoke();
                    return;
                }

                if (code < 200 || code >= 300 || string.IsNullOrEmpty(body))
                {
                    PrintWarning($"[PlaySafe ID] Status check for {steamId} — HTTP {code}");
                    onError?.Invoke();
                    return;
                }

                try
                {
                    var data = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                    if (data != null && data.ContainsKey("status"))
                    {
                        string status = data["status"]?.ToString();
                        if (_config.LogEvents)
                            Puts($"[PlaySafe ID] Parsed status for {steamId}: {status}");
                        onResult?.Invoke(status);
                    }
                    else
                    {
                        PrintWarning($"[PlaySafe ID] Unexpected response for {steamId}: {body}");
                        onError?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    PrintWarning($"[PlaySafe ID] JSON parse error for {steamId}: {ex.Message}");
                    onError?.Invoke();
                }
            }, this, RequestMethod.GET, headers, _config.TimeoutSeconds);
        }

        #endregion

        #region API — Batch User Status

        private const int BATCH_MAX_USERS = 50;

        /// <summary>
        /// POST /v1/community/user/batch
        /// Header: X-Api-Key, Content-Type: application/json
        ///
        /// Body: { "users": ["STEAM:id1", "STEAM:id2", ...] }
        /// Response: { "users": [{ platform, platformUserId, status }], "notFound": ["STEAM:id99"] }
        ///
        /// Returns a dictionary mapping steamId -> status (null for not-found users).
        /// </summary>
        private void BatchGetUserStatus(List<string> steamIds,
            Action<Dictionary<string, string>> onResult, Action onError)
        {
            if (steamIds == null || steamIds.Count == 0)
            {
                onResult?.Invoke(new Dictionary<string, string>());
                return;
            }

            string url = $"{API_BASE}/user/batch";

            var users = new List<string>();
            foreach (string id in steamIds)
                users.Add($"{PLATFORM}:{id}");

            var payload = new Dictionary<string, object> { { "users", users } };
            string body = JsonConvert.SerializeObject(payload);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", _config.ApiKey },
                { "Content-Type", "application/json" },
                { "Accept", "application/json" }
            };

            if (_config.LogEvents)
                Puts($"[PlaySafe ID] Batch status request for {steamIds.Count} user(s): POST {url}");

            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (_config.LogEvents)
                    Puts($"[PlaySafe ID] Batch status response: HTTP {code}");

                if (code == 429)
                {
                    PrintWarning("[PlaySafe ID] Rate limited (429). Too many requests — try again shortly.");
                    onError?.Invoke();
                    return;
                }

                if (code < 200 || code >= 300 || string.IsNullOrEmpty(response))
                {
                    PrintWarning($"[PlaySafe ID] Batch status check — HTTP {code}: {response}");
                    onError?.Invoke();
                    return;
                }

                try
                {
                    var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(response);
                    var result = new Dictionary<string, string>();

                    if (wrapper != null && wrapper.ContainsKey("users") && wrapper["users"] != null)
                    {
                        var usersResult = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(
                            wrapper["users"].ToString());
                        if (usersResult != null)
                        {
                            foreach (var user in usersResult)
                            {
                                string userId = user.ContainsKey("platformUserId")
                                    ? user["platformUserId"]?.ToString() : null;
                                string status = user.ContainsKey("status")
                                    ? user["status"]?.ToString() : null;
                                if (userId != null)
                                    result[userId] = status;
                            }
                        }
                    }

                    // Mark not-found users with null status
                    if (wrapper != null && wrapper.ContainsKey("notFound") && wrapper["notFound"] != null)
                    {
                        var notFound = JsonConvert.DeserializeObject<List<string>>(
                            wrapper["notFound"].ToString());
                        if (notFound != null)
                        {
                            foreach (string entry in notFound)
                            {
                                // Entry format is "STEAM:12345" — extract the ID after the colon
                                int colonIdx = entry.IndexOf(':');
                                string id = colonIdx >= 0 ? entry.Substring(colonIdx + 1) : entry;
                                if (!result.ContainsKey(id))
                                    result[id] = null;
                            }
                        }
                    }

                    onResult?.Invoke(result);
                }
                catch (Exception ex)
                {
                    PrintWarning($"[PlaySafe ID] Batch status parse error: {ex.Message}");
                    onError?.Invoke();
                }
            }, this, RequestMethod.POST, headers, _config.TimeoutSeconds);
        }

        #endregion

        #region API — Submit Community Ban

        /// <summary>
        /// POST /v1/community/bans
        /// Header: X-Api-Key, Content-Type: application/json
        ///
        /// Body: { platform, platformUserId, type, reporter, evidence: { reason },
        ///         gameCode, startsAt, expiresAt }
        ///
        /// 201 → Ban created successfully
        /// </summary>
        private void SubmitCommunityBan(string steamId, string banType, string reporter,
            string reason, string expiresAt, Action<bool, string> callback)
        {
            string url = $"{API_BASE}/bans";

            var evidence = new Dictionary<string, string>
            {
                { "reason", reason }
            };

            var payload = new Dictionary<string, object>
            {
                { "platform", PLATFORM },
                { "platformUserId", steamId },
                { "type", banType },
                { "reporter", reporter },
                { "reason", reason },
                { "evidence", evidence },
                { "gameCode", _config.GameCode },
                { "startsAt", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") },
                { "expiresAt", expiresAt }  // null = permanent
            };

            string body = JsonConvert.SerializeObject(payload);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", _config.ApiKey },
                { "Content-Type", "application/json" },
                { "Accept", "application/json" }
            };

            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (code == 403)
                    PrintWarning($"[PlaySafe ID] Game code '{_config.GameCode}' not found under your developer account (403). Check your config GameCode matches the code registered with PlaySafe ID.");
                else if (code == 401)
                    PrintWarning("[PlaySafe ID] Invalid or missing API key (401). Check your config.");
                else if (code == 429)
                    PrintWarning("[PlaySafe ID] Rate limited (429). Too many requests — try again shortly.");

                callback?.Invoke(code >= 200 && code < 300, response ?? $"HTTP {code}");
            }, this, RequestMethod.POST, headers, _config.TimeoutSeconds);
        }

        #endregion

        #region API — List User Bans

        /// <summary>
        /// GET /v1/community/bans/user/{platform}/{platformUserId}?limit=100&amp;page=N
        /// Header: X-Api-Key
        ///
        /// Returns all community bans for the specified user, paginating
        /// automatically when the total exceeds one page.
        /// </summary>
        private void GetUserBans(string steamId, Action<List<Dictionary<string, object>>> onResult,
            Action<string> onError, int page = 1, List<Dictionary<string, object>> accumulated = null)
        {
            const int PAGE_SIZE = 100;
            string url = $"{API_BASE}/bans/user/{PLATFORM}/{steamId}?limit={PAGE_SIZE}&page={page}";

            if (_config.LogEvents)
                Puts($"[PlaySafe ID] Requesting bans: GET {url}");

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", _config.ApiKey },
                { "Accept", "application/json" }
            };

            webrequest.Enqueue(url, null, (code, body) =>
            {
                if (code == 404)
                {
                    onResult?.Invoke(accumulated ?? new List<Dictionary<string, object>>());
                    return;
                }

                if (code == 429)
                {
                    PrintWarning("[PlaySafe ID] Rate limited (429). Too many requests — try again shortly.");
                    onError?.Invoke("HTTP 429 — rate limited");
                    return;
                }

                if (code < 200 || code >= 300 || string.IsNullOrEmpty(body))
                {
                    PrintWarning($"[PlaySafe ID] Bans lookup for {steamId} — HTTP {code}: {body}");
                    onError?.Invoke($"HTTP {code}");
                    return;
                }

                try
                {
                    var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                    if (wrapper != null && wrapper.ContainsKey("bans") && wrapper["bans"] != null)
                    {
                        var bans = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(
                            wrapper["bans"].ToString());
                        if (bans != null)
                        {
                            if (accumulated == null)
                                accumulated = new List<Dictionary<string, object>>();
                            accumulated.AddRange(bans);

                            // Check if more pages exist
                            int total = 0;
                            if (wrapper.ContainsKey("total"))
                                int.TryParse(wrapper["total"]?.ToString(), out total);

                            if (total > page * PAGE_SIZE)
                            {
                                GetUserBans(steamId, onResult, onError, page + 1, accumulated);
                                return;
                            }

                            onResult?.Invoke(accumulated);
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (_config.LogEvents)
                        Puts($"[PlaySafe ID] Parse error for bans response: {ex.Message}");
                }

                PrintWarning($"[PlaySafe ID] Could not parse bans response for {steamId}: {body}");
                onError?.Invoke("Could not parse API response");
            }, this, RequestMethod.GET, headers, _config.TimeoutSeconds);
        }

        #endregion

        #region API — Overturn Community Ban

        /// <summary>
        /// PATCH /v1/community/bans/{banId}
        /// Header: X-Api-Key, Content-Type: application/json
        ///
        /// Body: { reason, overturnedBy }
        ///
        /// 200 → Ban overturned successfully
        /// </summary>
        private void OverturnCommunityBan(string banId, string reason, string overturnedBy, Action<bool, string> callback)
        {
            string url = $"{API_BASE}/bans/{banId}";

            if (_config.LogEvents)
                Puts($"[PlaySafe ID] Overturning ban: PATCH {url}");

            var payload = new Dictionary<string, string>
            {
                { "reason", reason },
                { "overturnedBy", overturnedBy }
            };

            string body = JsonConvert.SerializeObject(payload);

            var headers = new Dictionary<string, string>
            {
                { "X-Api-Key", _config.ApiKey },
                { "Content-Type", "application/json" },
                { "Accept", "application/json" }
            };

            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (_config.LogEvents)
                    Puts($"[PlaySafe ID] Overturn response: HTTP {code} — {response ?? "(empty)"}");

                if (code == 429)
                    PrintWarning("[PlaySafe ID] Rate limited (429). Too many requests — try again shortly.");

                callback?.Invoke(code >= 200 && code < 300, response ?? $"HTTP {code}");
            }, this, RequestMethod.PATCH, headers, _config.TimeoutSeconds);
        }

        #endregion

        // ===================================================================
        // HELPER METHODS
        // ===================================================================
        #region Helpers

        /// <summary>
        /// Extracts a human-readable reason from a ban object,
        /// checking both evidence.reason and reason fields.
        /// </summary>
        private string ExtractBanReason(Dictionary<string, object> ban)
        {
            string reason = "No reason provided";

            // Try evidence.reason first
            if (ban.ContainsKey("evidence") && ban["evidence"] != null)
            {
                try
                {
                    var evidence = JsonConvert.DeserializeObject<Dictionary<string, object>>(ban["evidence"].ToString());
                    if (evidence != null && evidence.ContainsKey("reason"))
                        reason = evidence["reason"]?.ToString() ?? reason;
                }
                catch (Exception ex)
                {
                    if (_config.LogEvents)
                        Puts($"[PlaySafe ID] Failed to parse evidence.reason: {ex.Message}");
                }
            }

            // Fall back to reason field
            if (reason == "No reason provided" && ban.ContainsKey("reason") && ban["reason"] != null)
            {
                try
                {
                    var reasonObj = JsonConvert.DeserializeObject<Dictionary<string, object>>(ban["reason"].ToString());
                    if (reasonObj != null && reasonObj.ContainsKey("reason"))
                        reason = reasonObj["reason"]?.ToString() ?? reason;
                }
                catch (Exception ex)
                {
                    if (_config.LogEvents)
                        Puts($"[PlaySafe ID] Failed to parse reason as object, using raw value: {ex.Message}");
                    reason = ban["reason"]?.ToString() ?? reason;
                }
            }

            return reason;
        }

        private bool IsActiveStatus(string status)
        {
            return !string.IsNullOrEmpty(status) &&
                   status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsWhitelisted(string steamId)
        {
            return _config.WhitelistedSteamIds != null &&
                   _config.WhitelistedSteamIds.Contains(steamId);
        }

        /// <summary>
        /// Checks authLevel >= 1, which covers both admins (level 2)
        /// and moderators (level 1) in Oxide/Rust.
        /// </summary>
        private bool IsAdminOrModerator(BasePlayer player)
        {
            if (player == null) return false;
            return player.IsAdmin || player.net?.connection?.authLevel >= 1;
        }

        /// <summary>
        /// Kicks the player. Rust's BasePlayer.Kick() sends the reason
        /// only to the kicked player's disconnect screen — it does NOT
        /// broadcast to server chat.
        /// </summary>
        private void SilentKick(BasePlayer player, string reason)
        {
            if (player == null || !player.IsConnected) return;
            player.Kick(reason);
        }

        private void AssignOxideGroup(string steamId)
        {
            if (string.IsNullOrEmpty(_config.OxideGroupVerified)) return;
            if (!permission.UserHasGroup(steamId, _config.OxideGroupVerified))
            {
                permission.AddUserGroup(steamId, _config.OxideGroupVerified);
            }
        }

        private void RemoveOxideGroup(string steamId)
        {
            if (string.IsNullOrEmpty(_config.OxideGroupVerified)) return;
            if (!_config.RemoveGroupOnFail) return;
            if (permission.UserHasGroup(steamId, _config.OxideGroupVerified))
            {
                permission.RemoveUserGroup(steamId, _config.OxideGroupVerified);
            }
        }

        /// <summary>
        /// When the API is unreachable:
        ///   - If fallback is enabled AND the player has the verified Oxide group
        ///     from a previous successful check, they are allowed to stay.
        ///   - Otherwise they are kicked with the API-down message.
        /// </summary>
        private void HandleApiFallback(BasePlayer player, string steamId, string name)
        {
            if (_config.FallbackAllowByGroup &&
                !string.IsNullOrEmpty(_config.OxideGroupVerified) &&
                permission.UserHasGroup(steamId, _config.OxideGroupVerified))
            {
                Log($"API unreachable — {name} ({steamId}) allowed via fallback (has verified group).");
            }
            else
            {
                SilentKick(player, _config.KickMessageApiDown);
                Log($"API unreachable — kicked {name} ({steamId}) (no fallback group).");
            }
        }

        /// <summary>
        /// Strict detection of ban type from a ban reason string.
        /// Returns null if the reason does not clearly indicate a known
        /// anti-cheat detection. Only matches that can be confidently
        /// attributed to an anti-cheat system are reported automatically.
        /// </summary>
        private string InferBanType(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return null;
            string r = reason.ToUpper();

            if (r.Contains("EAC") || r.Contains("EASY ANTI"))
                return "CHEATING_SOFTWARE";
            if (r.Contains("VAC"))
                return "CHEATING_SOFTWARE";
            if (r.Contains("DMA"))
                return "CHEATING_DMA";

            return null;
        }

        /// <summary>
        /// Determines the reporting source based on the ban reason.
        /// Only called when InferBanType returned a non-null match.
        /// </summary>
        private string InferReporter(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "ANTI_CHEAT";
            string r = reason.ToUpper();

            if (r.Contains("EAC") || r.Contains("EASY ANTI"))
                return "EAC";
            if (r.Contains("VAC"))
                return "VAC";

            return "ANTI_CHEAT";
        }

        private void Log(string message)
        {
            if (_config.LogEvents)
                Puts($"[PlaySafe ID] {message}");
        }

        #endregion
    }
}
