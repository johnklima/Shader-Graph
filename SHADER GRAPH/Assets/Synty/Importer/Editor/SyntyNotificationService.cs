using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using Synty.Tools;

namespace Synty.Passport.Editor
{
    /// <summary>
    /// Background notification service that runs independently of the Synty Passport tool.
    /// Checks for: active store sales, product updates, new releases.
    /// Controls the toolbar button badge/color and shows sale ad windows.
    /// </summary>
    [InitializeOnLoad]
    public static class SyntyNotificationService
    {
        private const string API_URL = "https://synty-downloads.syntystore.workers.dev";
        
        // EditorPrefs keys
        private const string PREFS_PREFIX = "SyntyNotif_";
        private const string PREFS_LAST_CHECK = PREFS_PREFIX + "LastCheck";
        private const string PREFS_SEEN_RELEASES = PREFS_PREFIX + "SeenReleases"; // comma-separated handles
        private const string PREFS_SALE_DISMISSED = PREFS_PREFIX + "SaleDismissed"; // ISO timestamp of last dismissed sale
        private const string PREFS_SALE_SHOWN_SESSION = PREFS_PREFIX + "SaleShownSession"; // session key
        private const string PREFS_NEW_RELEASE_SHOWN = PREFS_PREFIX + "NewReleaseShown"; // handle of last shown featured release
        
        // New Release featured data
        public static bool NewReleaseActive { get; private set; }
        public static string NewReleaseHandle { get; private set; } = "";
        public static string NewReleaseTitle { get; private set; } = "";
        public static string NewReleaseUrl { get; private set; } = "";
        
        // Announcements
        public static List<(string id, string message, string url, string date)> Announcements { get; private set; } = new List<(string, string, string, string)>();
        public static int UnseenAnnouncementCount { get; private set; }
        public static event System.Action OnAnnouncementsUpdated;
        
        private const string PREFS_SEEN_ANNOUNCEMENTS = PREFS_PREFIX + "SeenAnnouncements";
        
        // Check interval in seconds (15 minutes)
        private const double CHECK_INTERVAL = 900.0;
        
        // Startup delay before first check (seconds)
        private const double STARTUP_DELAY = 5.0;
        
        // User prefs — Profile panel toggles. Default true so existing users aren't affected.
        public const string PREFS_NOTIF_SALES    = PREFS_PREFIX + "NotifSales";
        public const string PREFS_NOTIF_RELEASES = PREFS_PREFIX + "NotifReleases";
        public const string PREFS_NOTIF_UPDATES  = PREFS_PREFIX + "NotifUpdates";
        
        // Notification state — backing fields, gated by user prefs at read time so the toggles
        // in the Profile panel suppress the corresponding notification without needing a re-evaluate.
        private static bool _hasSaleNotification;
        private static bool _hasUpdateNotification;
        private static bool _hasNewReleaseNotification;
        public static bool HasSaleNotification       => _hasSaleNotification       && UnityEditor.EditorPrefs.GetBool(PREFS_NOTIF_SALES, true);
        public static bool HasUpdateNotification     => _hasUpdateNotification     && UnityEditor.EditorPrefs.GetBool(PREFS_NOTIF_UPDATES, true);
        public static bool HasNewReleaseNotification => _hasNewReleaseNotification && UnityEditor.EditorPrefs.GetBool(PREFS_NOTIF_RELEASES, true);
        public static bool HasAnyNotification => HasSaleNotification || HasUpdateNotification || HasNewReleaseNotification;
        
        // Sale data
        public static bool SaleStartActive { get; private set; }
        public static bool SaleMiddleActive { get; private set; }
        public static bool SaleEndActive { get; private set; }
        public static bool SaleExtendedActive { get; private set; }
        public static string SaleStartDate { get; private set; } = "";
        public static string SaleEndDate { get; private set; } = "";
        public static string SaleExtendedDate { get; private set; } = "";
        public static bool SaleActive => SaleStartActive || SaleMiddleActive || SaleEndActive || SaleExtendedActive;
        public static string SaleTitle { get; private set; } = "";
        public static string SaleUrl { get; private set; } = "";
        public static string SaleButtonColor { get; private set; } = "";
        public static string SaleButtonTextColor { get; private set; } = "";
        
        /// <summary>
        /// Returns true if the sale's start date hasn't been reached yet. Empty/invalid dates count as "started".
        /// </summary>
        public static bool HasSaleStarted()
        {
            if (string.IsNullOrEmpty(SaleStartDate)) return true;
            DateTime startDate;
            if (DateTime.TryParse(SaleStartDate, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out startDate))
            {
                return DateTime.UtcNow >= startDate;
            }
            return true;
        }
        
        /// <summary>
        /// Returns the current active sale stage: "extended" > "end" > "middle" > "start" (priority order).
        /// Returns null if no sale is active or the sale has ended (past end date).
        /// </summary>
        public static string CurrentSaleStage
        {
            get
            {
                // Check if sale has expired
                if (HasSaleExpired()) return null;
                
                if (SaleExtendedActive) return "extended";
                if (SaleEndActive) return "end";
                if (SaleMiddleActive) return "middle";
                if (SaleStartActive) return "start";
                return null;
            }
        }
        
        /// <summary>
        /// Check if the sale end date has passed. Dates are stored as UTC ISO strings.
        /// Uses extendedDate if extended is active, otherwise endDate.
        /// </summary>
        public static bool HasSaleExpired()
        {
            string dateStr = SaleExtendedActive ? SaleExtendedDate : SaleEndDate;
            if (string.IsNullOrEmpty(dateStr)) return false; // No date set = never expires
            
            DateTime endDate;
            if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, 
                out endDate))
            {
                return DateTime.UtcNow > endDate;
            }
            return false;
        }
        
        // Product data for update/new release detection
        public static List<NotifProduct> Products { get; private set; } = new List<NotifProduct>();
        
        private static double _startupTime;
        private static double _lastCheckTime;
        private static bool _initialCheckDone;
        private static UnityWebRequest _activeRequest;
        private static string _sessionKey;
        
        [Serializable]
        public class NotifProduct
        {
            public string handle;
            public string title;
            public string version;
            public string releaseDate;
            public bool hasDownload;
        }
        
        [Serializable]
        private class NotifResponse
        {
            public bool success;
            public SaleData sale;
            public NewReleaseData newRelease;
            public AnnouncementData[] announcements;
            public NotifProduct[] products;
        }
        
        [Serializable]
        private class AnnouncementData
        {
            public string id;
            public string message;
            public string url;
            public string date;
        }
        
        [Serializable]
        private class NewReleaseData
        {
            public bool active;
            public string handle;
            public string title;
            public string url;
        }
        
        [Serializable]
        private class SaleData
        {
            public bool startActive;
            public bool middleActive;
            public bool endActive;
            public string startDate;
            public string endDate;
            public bool extendedActive;
            public string extendedDate;
            public string title;
            public string url;
            public string buttonColor;
            public string buttonTextColor;
        }
        
        static SyntyNotificationService()
        {
            _startupTime = EditorApplication.timeSinceStartup;
            _sessionKey = DateTime.Now.ToString("yyyyMMddHHmmss");
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
            // Abort any in-flight request before a domain reload so its native GC handle
            // isn't released from the new domain ("Release of invalid GC handle" warning)
            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                if (_activeRequest != null) { try { _activeRequest.Abort(); _activeRequest.Dispose(); } catch { } _activeRequest = null; }
            };
        }
        
        private static void OnUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            
            // Wait for startup delay
            if (now - _startupTime < STARTUP_DELAY) return;
            
            // Initial check
            if (!_initialCheckDone)
            {
                _initialCheckDone = true;
                _lastCheckTime = now;
                CheckNotifications();
                return;
            }
            
            // Periodic check
            if (now - _lastCheckTime >= CHECK_INTERVAL)
            {
                _lastCheckTime = now;
                CheckNotifications();
            }
        }
        
        /// <summary>
        /// Force an immediate notification check.
        /// </summary>
        public static void ForceCheck()
        {
            _lastCheckTime = EditorApplication.timeSinceStartup;
            CheckNotifications();
        }
        
        /// <summary>
        /// Mark all new releases as seen (called when Synty Passport opens).
        /// </summary>
        public static void MarkNewReleasesSeen()
        {
            if (Products == null || Products.Count == 0) return;
            
            var handles = Products
                .Where(p => p.hasDownload && !string.IsNullOrEmpty(p.handle))
                .Select(p => p.handle);
            
            EditorPrefs.SetString(PREFS_SEEN_RELEASES, string.Join(",", handles));
            _hasNewReleaseNotification = false;
            UpdateToolbarBadge();
        }
        
        /// <summary>
        /// Dismiss the current sale ad (won't show again until sale config changes).
        /// </summary>
        public static void DismissSale()
        {
            EditorPrefs.SetString(PREFS_SALE_DISMISSED, DateTime.Now.ToString("o"));
            _hasSaleNotification = false;
            UpdateToolbarBadge();
        }
        
        /// <summary>
        /// Stable key identifying the current sale (its date range), so "shown" state persists across
        /// domain reloads (installing an asset reloads the domain) instead of resetting each session.
        /// </summary>
        private static string SaleKey()
        {
            return (SaleStartDate ?? "") + "|" + (SaleEndDate ?? "");
        }

        /// <summary>
        /// Mark the current sale stage as shown for THIS sale — persists across reloads, so each stage
        /// (start / middle / end / extended) is only shown once per sale, not once per editor session.
        /// </summary>
        public static void MarkSaleShownThisSession()
        {
            string stage = CurrentSaleStage ?? "none";
            string prefix = SaleKey() + "::";
            string stored = EditorPrefs.GetString(PREFS_SALE_SHOWN_SESSION, "");
            // stages stored comma-wrapped (",start,middle,") for simple contains checks; reset when the sale changes.
            string stages = stored.StartsWith(prefix) ? stored.Substring(prefix.Length) : ",";
            if (!stages.Contains("," + stage + ",")) stages += stage + ",";
            EditorPrefs.SetString(PREFS_SALE_SHOWN_SESSION, prefix + stages);
        }

        private static bool WasSaleShownThisSession()
        {
            string stage = CurrentSaleStage ?? "none";
            string prefix = SaleKey() + "::";
            string stored = EditorPrefs.GetString(PREFS_SALE_SHOWN_SESSION, "");
            if (!stored.StartsWith(prefix)) return false; // different sale (or none recorded) → not shown yet
            return stored.Substring(prefix.Length).Contains("," + stage + ",");
        }
        
        private static void MarkNewReleaseShown(string handle)
        {
            EditorPrefs.SetString(PREFS_NEW_RELEASE_SHOWN, handle);
        }
        
        private static bool WasNewReleaseShown(string handle)
        {
            return EditorPrefs.GetString(PREFS_NEW_RELEASE_SHOWN, "") == handle;
        }
        
        /// <summary>
        /// Reset new release shown state (for debug).
        /// </summary>
        public static void ResetNewReleaseShown()
        {
            EditorPrefs.SetString(PREFS_NEW_RELEASE_SHOWN, "");
        }
        
        private static HashSet<string> GetSeenAnnouncementIds()
        {
            var set = new HashSet<string>();
            string csv = EditorPrefs.GetString(PREFS_SEEN_ANNOUNCEMENTS, "");
            if (!string.IsNullOrEmpty(csv))
            {
                foreach (var id in csv.Split(','))
                {
                    string trimmed = id.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        set.Add(trimmed);
                }
            }
            return set;
        }
        
        /// <summary>
        /// Mark all current announcements as seen. Called when user opens notification popup.
        /// </summary>
        public static void MarkAnnouncementsSeen()
        {
            var ids = new List<string>();
            foreach (var a in Announcements)
            {
                if (!string.IsNullOrEmpty(a.id))
                    ids.Add(a.id);
            }
            EditorPrefs.SetString(PREFS_SEEN_ANNOUNCEMENTS, string.Join(",", ids));
            UnseenAnnouncementCount = 0;
        }
        
        private static void CheckNotifications()
        {
            if (_activeRequest != null && !_activeRequest.isDone) return;
            
            string url = $"{API_URL}/notifications?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            _activeRequest = UnityWebRequest.Get(url);
            
            var op = _activeRequest.SendWebRequest();
            op.completed += _ => OnNotificationsReceived(_activeRequest);
        }
        
        private static void OnNotificationsReceived(UnityWebRequest request)
        {
            if (request == null) return; // aborted before a domain reload
            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    return;
                }
                
                var response = JsonUtility.FromJson<NotifResponse>(request.downloadHandler.text);
                if (response == null || !response.success) return;
                
                // Update sale state
                if (response.sale != null)
                {
                    SaleStartActive = response.sale.startActive;
                    SaleMiddleActive = response.sale.middleActive;
                    SaleEndActive = response.sale.endActive;
                    SaleStartDate = response.sale.startDate ?? "";
                    SaleEndDate = response.sale.endDate ?? "";
                    SaleExtendedActive = response.sale.extendedActive;
                    SaleExtendedDate = response.sale.extendedDate ?? "";
                    SaleTitle = response.sale.title ?? "";
                    SaleUrl = response.sale.url ?? "";
                    SaleButtonColor = response.sale.buttonColor ?? "";
                    SaleButtonTextColor = response.sale.buttonTextColor ?? "";
                }
                else
                {
                    SaleStartActive = false;
                    SaleMiddleActive = false;
                    SaleEndActive = false;
                    SaleExtendedActive = false;
                }
                
                // Update new release state
                if (response.newRelease != null)
                {
                    NewReleaseActive = response.newRelease.active;
                    NewReleaseHandle = response.newRelease.handle ?? "";
                    NewReleaseTitle = response.newRelease.title ?? "";
                    NewReleaseUrl = response.newRelease.url ?? "";
                }
                else
                {
                    NewReleaseActive = false;
                }
                
                // Update announcements
                Announcements.Clear();
                if (response.announcements != null)
                {
                    foreach (var a in response.announcements)
                    {
                        if (!string.IsNullOrEmpty(a.message))
                            Announcements.Add((a.id ?? "", a.message, a.url ?? "", a.date ?? ""));
                    }
                }
                
                // Count unseen
                var seenIds = GetSeenAnnouncementIds();
                UnseenAnnouncementCount = 0;
                foreach (var a in Announcements)
                {
                    if (!seenIds.Contains(a.id))
                        UnseenAnnouncementCount++;
                }
                
                OnAnnouncementsUpdated?.Invoke();
                
                // Update products
                Products = response.products != null ? new List<NotifProduct>(response.products) : new List<NotifProduct>();
                
                // Evaluate notifications
                EvaluateSaleNotification();
                EvaluateUpdateNotification();
                EvaluateNewReleaseNotification();
                
                UpdateToolbarBadge();
                
                // Show sale window if conditions met (priority over new release).
                // Respect the profile Sales toggle — when off, suppress all sale pop-ups.
                if (SaleActive && !HasSaleExpired() && !WasSaleShownThisSession()
                    && EditorPrefs.GetBool(PREFS_NOTIF_SALES, true))
                {
                    string stage = CurrentSaleStage;
                    MarkSaleShownThisSession();
                    EditorApplication.delayCall += () =>
                    {
                        SyntySaleWindow.ShowSaleAd(SaleTitle, stage, SaleUrl);
                    };
                }
                // Show new release window if no sale popup and user hasn't seen this release
                else if (NewReleaseActive && !string.IsNullOrEmpty(NewReleaseHandle) && !WasNewReleaseShown(NewReleaseHandle))
                {
                    MarkNewReleaseShown(NewReleaseHandle);
                    EditorApplication.delayCall += () =>
                    {
                        SyntySaleWindow.ShowNewRelease(NewReleaseTitle, NewReleaseUrl);
                    };
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try { request.Dispose(); } catch { }
                _activeRequest = null;
            }
        }
        
        private static void EvaluateSaleNotification()
        {
            _hasSaleNotification = SaleActive && !HasSaleExpired() && HasSaleStarted();
        }
        
        private static void EvaluateUpdateNotification()
        {
            // Check if any installed asset has a newer version in the catalog
            _hasUpdateNotification = false;
            
            foreach (var product in Products)
            {
                if (string.IsNullOrEmpty(product.handle) || string.IsNullOrEmpty(product.version))
                    continue;
                
                if (SyntyInstallManifest.HasUpdate(product.handle, null, product.version))
                {
                    _hasUpdateNotification = true;
                    break;
                }
            }
        }
        
        private static void EvaluateNewReleaseNotification()
        {
            _hasNewReleaseNotification = false;
            
            string seenStr = EditorPrefs.GetString(PREFS_SEEN_RELEASES, "");
            var seenSet = new HashSet<string>(
                seenStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            );
            
            foreach (var product in Products)
            {
                if (!product.hasDownload || string.IsNullOrEmpty(product.handle)) continue;
                
                // Check if this product is new (within 60 days)
                if (!string.IsNullOrEmpty(product.releaseDate))
                {
                    DateTime releaseDate;
                    if (DateTime.TryParse(product.releaseDate, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out releaseDate))
                    {
                        if ((DateTime.Now - releaseDate).TotalDays <= 60 && !seenSet.Contains(product.handle))
                        {
                            _hasNewReleaseNotification = true;
                            break;
                        }
                    }
                }
            }
        }
        
        private static void UpdateToolbarBadge()
        {
            bool shouldNotify = HasUpdateNotification || HasNewReleaseNotification;
            SyntyToolbarButton.SetNotifications(shouldNotify);
            EditorPrefs.SetBool("SyntyDownloaderV2_Notifications", shouldNotify);
        }
    }
}
