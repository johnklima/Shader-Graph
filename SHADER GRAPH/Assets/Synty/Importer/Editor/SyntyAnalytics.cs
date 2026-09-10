using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;
using System;
using System.Collections.Generic;
using System.Text;

namespace Synty.Tools
{
    /// <summary>
    /// Lightweight analytics tracker for Synty Passport.
    /// Queues events in memory and flushes them in batches to the analytics endpoint.
    /// Fire-and-forget — never blocks UI, fails silently.
    /// </summary>
    [InitializeOnLoad]
    public static class SyntyAnalytics
    {
        // ─── Config ──────────────────────────────────────────────────────
        private const string API_URL = "https://synty-downloads.syntystore.workers.dev";
        private const string PREF_INSTALL_ID = "SyntyAnalytics_InstallID";
        private const double FLUSH_INTERVAL = 60.0; // seconds between batch sends
        private const int MAX_QUEUE_SIZE = 200; // flush if queue exceeds this
        private const int MAX_BATCH_SIZE = 50; // max events per request
        
        // ─── State ───────────────────────────────────────────────────────
        private static string _installId;
        
        /// <summary>
        /// The anonymous install ID for this Unity installation.
        /// </summary>
        public static string InstallId => _installId;

        /// <summary>
        /// Generates a brand-new anonymous install ID and persists it. Call after a GDPR data
        /// deletion so future events are not re-linked to the erased identity.
        /// </summary>
        public static void RotateInstallId()
        {
            _installId = Guid.NewGuid().ToString("N").Substring(0, 16);
            EditorPrefs.SetString(PREF_INSTALL_ID, _installId);
        }
        private static string _sessionId;
        private static string _emailDomain = "";
        private static string _fullEmail = "";
        private static double _sessionStart;
        private static double _lastFlush;
        private static bool _sessionTracked;
        private static List<AnalyticsEvent> _queue = new List<AnalyticsEvent>();
        private static bool _isFlushing;
        private static UnityWebRequest _activeRequest; // in-flight batch (aborted on domain reload)
        
        // ─── Init ────────────────────────────────────────────────────────
        static SyntyAnalytics()
        {
            // Abort any in-flight batch before a domain reload so its native GC handle
            // isn't released from the new domain ("Release of invalid GC handle" warning)
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (_activeRequest != null) { try { _activeRequest.Abort(); _activeRequest.Dispose(); } catch { } _activeRequest = null; }
                _isFlushing = false;
            };
            
            // Generate or restore anonymous install ID
            _installId = EditorPrefs.GetString(PREF_INSTALL_ID, "");
            if (string.IsNullOrEmpty(_installId))
            {
                _installId = Guid.NewGuid().ToString("N").Substring(0, 16);
                EditorPrefs.SetString(PREF_INSTALL_ID, _installId);
            }
            
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            _sessionStart = EditorApplication.timeSinceStartup;
            _lastFlush = _sessionStart;
            _sessionTracked = false;
            
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            
            // Flush on quit
            EditorApplication.quitting -= OnQuit;
            EditorApplication.quitting += OnQuit;
            
            // Auto-track that the analytics system loaded
        }
        
        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            
            // Periodic flush
            if (now - _lastFlush >= FLUSH_INTERVAL && _queue.Count > 0)
            {
                Flush();
            }
            
            // Overflow flush
            if (_queue.Count >= MAX_QUEUE_SIZE)
            {
                Flush();
            }
        }
        
        private static void OnQuit()
        {
            // Track session end
            double duration = EditorApplication.timeSinceStartup - _sessionStart;
            Track("session_end", new Dictionary<string, object>
            {
                { "duration_seconds", Math.Round(duration, 1) }
            });
            
            // Synchronous flush on quit
            FlushSync();
        }
        
        // ─── Public API ──────────────────────────────────────────────────
        
        /// <summary>
        /// Set the logged-in user's email. Only the domain is stored (e.g. "syntystudios.com").
        /// Call this after login succeeds.
        /// </summary>
        public static void SetUserEmail(string email)
        {
            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
            {
                _emailDomain = "";
                _fullEmail = "";
                return;
            }
            _fullEmail = email.ToLowerInvariant().Trim();
            _emailDomain = email.Substring(email.IndexOf('@') + 1).ToLowerInvariant().Trim();
            _emailHash = ComputeEmailHash(_fullEmail);
        }

        private static string _emailHash = "";
        /// <summary>One-way SHA-256 hash of the account email (lowercased). Never reversible to the
        /// address; used for account-level opt-out and per-account data deletion.</summary>
        public static string EmailHash => _emailHash;

        public static string ComputeEmailHash(string email)
        {
            if (string.IsNullOrEmpty(email)) return "";
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(email.ToLowerInvariant().Trim()));
                var sb = new System.Text.StringBuilder();
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        
        /// <summary>
        // User pref: analytics on/off. Set from the Profile panel; saved to EditorPrefs.
        // Default true so existing users aren't affected unless they explicitly opt out.
        public const string PREFS_ANALYTICS_ENABLED = "Synty.Passport.AnalyticsEnabled";
        // Set true when the logged-in account is found on the server opt-out list. Gates tracking
        // regardless of the local pref, so an account opt-out follows the user to any install.
        private static bool _accountOptedOut = false;
        public static bool AccountOptedOut { get => _accountOptedOut; set => _accountOptedOut = value; }
        public static bool IsAnalyticsEnabled => !_accountOptedOut && UnityEditor.EditorPrefs.GetBool(PREFS_ANALYTICS_ENABLED, true);
        
        /// <summary>
        /// Track an event with optional properties.
        /// </summary>
        public static void Track(string eventName, Dictionary<string, object> properties = null)
        {
            // Opted out — silently drop everything (no buffering, no later send).
            if (!IsAnalyticsEnabled) return;
            
            var evt = new AnalyticsEvent
            {
                installId = _installId,
                sessionId = _sessionId,
                eventName = eventName,
                timestamp = DateTime.UtcNow.ToString("o"),
                toolVersion = SyntyToolVersion.VERSION,
                unityVersion = Application.unityVersion,
                os = SystemInfo.operatingSystem,
                emailDomain = _emailDomain,
                emailHash = _emailHash,
                properties = properties != null ? DictToJson(properties) : "{}"
            };
            
            _queue.Add(evt);
        }
        
        /// <summary>
        /// Track tool_open event with context. Call once when the tool window opens.
        /// </summary>
        public static void TrackToolOpen(bool isLoggedIn, bool hasSyntyPass, int ownedCount, int installedCount)
        {
            if (_sessionTracked) return;
            _sessionTracked = true;
            
            Track("tool_open", new Dictionary<string, object>
            {
                { "is_logged_in", isLoggedIn },
                { "has_syntypass", hasSyntyPass },
                { "owned_count", ownedCount },
                { "installed_count", installedCount },
                { "email", _fullEmail }
            });
        }
        
        /// <summary>
        /// Track a download event.
        /// </summary>
        public static void TrackDownload(string assetHandle, string assetName, string assetType, bool isCacheHit)
        {
            Track("download_start", new Dictionary<string, object>
            {
                { "asset_handle", assetHandle },
                { "asset_name", assetName },
                { "asset_type", assetType },
                { "cache_hit", isCacheHit }
            });
        }
        
        /// <summary>
        /// Track download completion.
        /// </summary>
        public static void TrackDownloadComplete(string assetHandle, float durationSeconds, bool success, string error = null)
        {
            var props = new Dictionary<string, object>
            {
                { "asset_handle", assetHandle },
                { "duration_seconds", Math.Round(durationSeconds, 1) },
                { "success", success }
            };
            if (error != null) props["error"] = error;
            Track("download_complete", props);
        }
        
        /// <summary>
        /// Track an install — the asset being imported into the project.
        /// This fires whether the asset came from a fresh network download or the local cache.
        /// </summary>
        public static void TrackInstall(string assetHandle, string assetName, string assetType, bool fromCache)
        {
            Track("install", new Dictionary<string, object>
            {
                { "asset_handle", assetHandle },
                { "asset_name", assetName },
                { "asset_type", assetType },
                { "from_cache", fromCache }
            });
        }
        
        /// <summary>
        /// Track add to cart.
        /// </summary>
        public static void TrackAddToCart(string assetHandle, string assetName, float price, bool isMultiSelect, int cartSize)
        {
            Track("add_to_cart", new Dictionary<string, object>
            {
                { "asset_handle", assetHandle },
                { "asset_name", assetName },
                { "price", price },
                { "multi_select", isMultiSelect },
                { "cart_size", cartSize }
            });
        }
        
        /// <summary>
        /// Track uninstall.
        /// </summary>
        public static void TrackUninstall(string assetHandle, string assetName)
        {
            Track("uninstall", new Dictionary<string, object>
            {
                { "asset_handle", assetHandle },
                { "asset_name", assetName }
            });
        }
        
        /// <summary>
        /// Track SyntyPass toggle.
        /// </summary>
        public static void TrackSyntyPassToggle(bool enabled)
        {
            Track("syntypass_toggle", new Dictionary<string, object>
            {
                { "enabled", enabled }
            });
        }
        
        /// <summary>
        /// Track subscribe button click.
        /// </summary>
        public static void TrackSubscribeClick(string plan)
        {
            Track("subscribe_click", new Dictionary<string, object>
            {
                { "plan", plan }
            });
        }
        
        /// <summary>
        /// Track sale/notification interactions.
        /// </summary>
        public static void TrackSaleImpression(string stage, string title)
        {
            Track("sale_impression", new Dictionary<string, object>
            {
                { "stage", stage },
                { "title", title }
            });
        }
        
        public static void TrackSaleClick(string stage, string url)
        {
            Track("sale_click", new Dictionary<string, object>
            {
                { "stage", stage },
                { "url", url }
            });
        }

        // Fired when the user clicks "Open Synty Importer" in the sale pop-up (opened the tool from the sale).
        public static void TrackSaleOpenTool(string stage, string title)
        {
            Track("sale_open_tool", new Dictionary<string, object>
            {
                { "stage", stage },
                { "title", title }
            });
        }
        
        public static void TrackNewReleaseImpression(string handle, string title)
        {
            Track("new_release_impression", new Dictionary<string, object>
            {
                { "handle", handle },
                { "title", title }
            });
        }
        
        public static void TrackNewReleaseClick(string handle, string url)
        {
            Track("new_release_click", new Dictionary<string, object>
            {
                { "handle", handle },
                { "url", url }
            });
        }
        
        public static void TrackNotificationOpen(int unseenCount)
        {
            Track("notification_open", new Dictionary<string, object>
            {
                { "unseen_count", unseenCount }
            });
        }
        
        public static void TrackNotificationClick(string announcementId, string url)
        {
            Track("notification_click", new Dictionary<string, object>
            {
                { "announcement_id", announcementId },
                { "url", url }
            });
        }
        
        public static void TrackExternalLink(string platform, string url)
        {
            Track("external_link_click", new Dictionary<string, object>
            {
                { "platform", platform },
                { "url", url }
            });
        }
        
        public static void TrackLogin(bool success, string method = "email")
        {
            Track(success ? "login_success" : "login_failure", new Dictionary<string, object>
            {
                { "method", method },
                { "email", _fullEmail }
            });
        }
        
        public static void TrackFilterChange(string filterType, string value, bool enabled)
        {
            Track("filter_change", new Dictionary<string, object>
            {
                { "filter_type", filterType },
                { "value", value },
                { "enabled", enabled }
            });
        }
        
        public static void TrackSearch(string query)
        {
            Track("search", new Dictionary<string, object>
            {
                { "query_length", query?.Length ?? 0 }
            });
        }
        
        public static void TrackGridSize(int columns)
        {
            Track("grid_size_change", new Dictionary<string, object>
            {
                { "columns", columns }
            });
        }
        
        // ─── Flush ───────────────────────────────────────────────────────
        
        /// <summary>
        /// Force an immediate async flush.
        /// </summary>
        public static void Flush()
        {
            if (_isFlushing || _queue.Count == 0) return;
            _isFlushing = true;
            _lastFlush = EditorApplication.timeSinceStartup;
            
            int count = Mathf.Min(_queue.Count, MAX_BATCH_SIZE);
            var batch = _queue.GetRange(0, count);
            _queue.RemoveRange(0, count);
            
            SendBatch(batch);
        }
        
        private static void FlushSync()
        {
            if (_queue.Count == 0) return;
            
            int count = Mathf.Min(_queue.Count, MAX_BATCH_SIZE);
            var batch = _queue.GetRange(0, count);
            _queue.RemoveRange(0, count);
            
            string json = BatchToJson(batch);
            var request = new UnityWebRequest($"{API_URL}/analytics", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.SetRequestHeader("Content-Type", "application/json");
            request.SendWebRequest();
            // Don't wait — best effort on quit
        }
        
        private static void SendBatch(List<AnalyticsEvent> batch)
        {
            string json = BatchToJson(batch);
            
            var request = new UnityWebRequest($"{API_URL}/analytics", "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            _activeRequest = request;
            
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                _isFlushing = false;
                try { request.Dispose(); } catch { }
                if (_activeRequest == request) _activeRequest = null;
                
                // If more queued, flush again
                if (_queue.Count > 0)
                    Flush();
            };
        }
        
        // ─── Serialization ───────────────────────────────────────────────
        
        [Serializable]
        private class AnalyticsEvent
        {
            public string installId;
            public string sessionId;
            public string eventName;
            public string timestamp;
            public string toolVersion;
            public string unityVersion;
            public string os;
            public string emailDomain;
            public string emailHash;
            public string properties; // JSON string
        }
        
        private static string BatchToJson(List<AnalyticsEvent> batch)
        {
            var sb = new StringBuilder();
            sb.Append("{\"events\":[");
            for (int i = 0; i < batch.Count; i++)
            {
                if (i > 0) sb.Append(",");
                var e = batch[i];
                sb.Append("{");
                sb.Append($"\"installId\":\"{Escape(e.installId)}\",");
                sb.Append($"\"sessionId\":\"{Escape(e.sessionId)}\",");
                sb.Append($"\"eventName\":\"{Escape(e.eventName)}\",");
                sb.Append($"\"timestamp\":\"{Escape(e.timestamp)}\",");
                sb.Append($"\"toolVersion\":\"{Escape(e.toolVersion ?? "")}\",");
                sb.Append($"\"unityVersion\":\"{Escape(e.unityVersion ?? "")}\",");
                sb.Append($"\"os\":\"{Escape(e.os ?? "")}\",");
                sb.Append($"\"emailDomain\":\"{Escape(e.emailDomain ?? "")}\",");
                sb.Append($"\"emailHash\":\"{Escape(e.emailHash ?? "")}\",");
                sb.Append($"\"properties\":{e.properties}");
                sb.Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }
        
        private static string DictToJson(Dictionary<string, object> dict)
        {
            var sb = new StringBuilder();
            sb.Append("{");
            bool first = true;
            foreach (var kv in dict)
            {
                if (!first) sb.Append(",");
                first = false;
                sb.Append($"\"{Escape(kv.Key)}\":");
                if (kv.Value is string s)
                    sb.Append($"\"{Escape(s)}\"");
                else if (kv.Value is bool b)
                    sb.Append(b ? "true" : "false");
                else if (kv.Value is float f)
                    sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (kv.Value is double d)
                    sb.Append(d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                else if (kv.Value is int n)
                    sb.Append(n);
                else
                    sb.Append($"\"{Escape(kv.Value?.ToString() ?? "")}\"");
            }
            sb.Append("}");
            return sb.ToString();
        }
        
        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }
    }
}
