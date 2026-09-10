using UnityEditor;
using UnityEngine;

namespace Synty.Passport.Editor
{
    public class SyntyNotificationDebug : EditorWindow
    {
        private string _saleTitle = "Summer Sale 2026";
        private string _saleUrl = "https://syntystore.com";
        private int _saleStageIdx = 0;
        private string[] _saleStages = { "start", "middle", "end", "extended" };
        
        // Registered dev-only in RegisterInternalMenu() below — only when the SyntyDebugUnlock marker
        // is present — via Menu.AddMenuItem, so it (and the whole Internal submenu) is HIDDEN, not just
        // greyed, in customer projects. A [MenuItem] validate can only grey out, not hide.
        public static void ShowWindow()
        {
            GetWindow<SyntyNotificationDebug>("Notif Debug").minSize = new Vector2(300, 400);
        }

        private static bool HasDebugMarker()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType("Synty.Passport.Dev.SyntyDebugUnlock") != null) return true; }
                catch { }
            }
            return false;
        }

        [InitializeOnLoadMethod]
        private static void RegisterInternalMenu()
        {
            if (!HasDebugMarker()) return;
            EditorApplication.delayCall += () =>
                AddMenuItemReflective("Synty/Internal/Notification Debug", false, 1001, ShowWindow);
        }

        // UnityEditor.Menu.AddMenuItem is internal, so call it via reflection to register the dev
        // menu item only when the marker is present (so it's hidden, not greyed, otherwise).
        private static void AddMenuItemReflective(string path, bool isChecked, int priority, System.Action execute)
        {
            try
            {
                var m = typeof(UnityEditor.Menu).GetMethod("AddMenuItem",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string), typeof(string), typeof(bool), typeof(int), typeof(System.Action), typeof(System.Func<bool>) },
                    null);
                if (m != null) m.Invoke(null, new object[] { path, "", isChecked, priority, execute, null });
            }
            catch { }
        }
        
        private void OnInspectorUpdate()
        {
            Repaint();
        }
        
        private void OnGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Notification Debug", EditorStyles.boldLabel);
            EditorGUILayout.Space(4);
            
            // Current state
            EditorGUILayout.LabelField("Current State", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"  Sale Active: {SyntyNotificationService.SaleActive}");
            EditorGUILayout.LabelField($"  Sale Stage: {SyntyNotificationService.CurrentSaleStage ?? "none"}");
            EditorGUILayout.LabelField($"    Start: {SyntyNotificationService.SaleStartActive}  Middle: {SyntyNotificationService.SaleMiddleActive}  End: {SyntyNotificationService.SaleEndActive}  Extended: {SyntyNotificationService.SaleExtendedActive}");
            EditorGUILayout.LabelField($"  Expired: {SyntyNotificationService.HasSaleExpired()}");
            if (!string.IsNullOrEmpty(SyntyNotificationService.SaleEndDate))
                EditorGUILayout.LabelField($"  End Date: {SyntyNotificationService.SaleEndDate}");
            if (!string.IsNullOrEmpty(SyntyNotificationService.SaleExtendedDate))
                EditorGUILayout.LabelField($"  Extended Date: {SyntyNotificationService.SaleExtendedDate}");
            EditorGUILayout.LabelField($"  Updates: {SyntyNotificationService.HasUpdateNotification}");
            EditorGUILayout.LabelField($"  New Releases: {SyntyNotificationService.HasNewReleaseNotification}");
            EditorGUILayout.LabelField($"  Any: {SyntyNotificationService.HasAnyNotification}");
            EditorGUILayout.Space(8);
            
            // Toolbar badge
            EditorGUILayout.LabelField("Toolbar Badge", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Badge ON"))
                SyntyToolbarButton.SetNotifications(true);
            if (GUILayout.Button("Badge OFF"))
                SyntyToolbarButton.SetNotifications(false);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(8);
            
            // Force check
            EditorGUILayout.LabelField("Server Check", EditorStyles.boldLabel);
            if (GUILayout.Button("Force Check Now"))
                SyntyNotificationService.ForceCheck();
            EditorGUILayout.Space(8);
            
            // Sale window
            EditorGUILayout.LabelField("Sale Ad Window", EditorStyles.boldLabel);
            _saleTitle = EditorGUILayout.TextField("Title", _saleTitle);
            _saleStageIdx = EditorGUILayout.Popup("Stage", _saleStageIdx, _saleStages);
            _saleUrl = EditorGUILayout.TextField("Sale URL", _saleUrl);
            if (GUILayout.Button("Show Sale Window"))
                SyntySaleWindow.ShowSaleAd(_saleTitle, _saleStages[_saleStageIdx], _saleUrl);
            EditorGUILayout.Space(8);
            
            // New release window
            EditorGUILayout.LabelField("New Release Window", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"  Active: {SyntyNotificationService.NewReleaseActive}");
            EditorGUILayout.LabelField($"  Handle: {(string.IsNullOrEmpty(SyntyNotificationService.NewReleaseHandle) ? "(none — Force Check first)" : SyntyNotificationService.NewReleaseHandle)}");
            EditorGUILayout.LabelField($"  Title: {(string.IsNullOrEmpty(SyntyNotificationService.NewReleaseTitle) ? "(empty)" : SyntyNotificationService.NewReleaseTitle)}");
            EditorGUILayout.LabelField($"  URL: {(string.IsNullOrEmpty(SyntyNotificationService.NewReleaseUrl) ? "(empty)" : SyntyNotificationService.NewReleaseUrl)}");
            
            if (GUILayout.Button("Show New Release Window"))
            {
                string title = SyntyNotificationService.NewReleaseTitle;
                string url = SyntyNotificationService.NewReleaseUrl;
                if (string.IsNullOrEmpty(title)) title = "NEW RELEASE - Test";
                SyntySaleWindow.ShowNewRelease(title, url);
            }
            EditorGUILayout.Space(8);
            
            // Mark seen / dismiss
            EditorGUILayout.LabelField("Clear Notifications", EditorStyles.boldLabel);
            if (GUILayout.Button("Reset Sale (will re-trigger on next check)"))
            {
                EditorPrefs.DeleteKey("SyntyNotif_SaleShownSession");
                EditorPrefs.DeleteKey("SyntyNotif_SaleDismissed");
                // Debug.Log("[SyntyNotifDebug] Sale session reset — Force Check will re-trigger sale window");
            }
            if (GUILayout.Button("Reset New Release"))
            {
                SyntyNotificationService.ResetNewReleaseShown();
                // Debug.Log("[SyntyNotifDebug] New release reset — Force Check will re-trigger popup");
            }
            if (GUILayout.Button("Mark Releases Seen"))
                SyntyNotificationService.MarkNewReleasesSeen();
            if (GUILayout.Button("Dismiss Sale"))
                SyntyNotificationService.DismissSale();
            if (GUILayout.Button("Clear All Notif Prefs"))
            {
                EditorPrefs.DeleteKey("SyntyNotif_LastCheck");
                EditorPrefs.DeleteKey("SyntyNotif_SeenReleases");
                EditorPrefs.DeleteKey("SyntyNotif_SaleDismissed");
                EditorPrefs.DeleteKey("SyntyNotif_SaleShownSession");
                // Debug.Log("[SyntyNotifDebug] Cleared all notification prefs");
            }
        }
    }
}
