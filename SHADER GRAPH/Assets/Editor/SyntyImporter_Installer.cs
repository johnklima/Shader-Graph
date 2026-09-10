using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Synty.Passport.Bootstrap
{
    /// <summary>
    /// Synty Passport installer.
    ///
    /// This is the ONLY file a customer needs to add to their Unity project. On load it
    /// checks whether the full Passport tool is already present. If not, it downloads the
    /// latest .unitypackage from the Synty worker, imports it, and (on the very first
    /// install) opens the Passport window automatically.
    ///
    /// How it decides what to do:
    ///   - If the Passport tool type already exists in the project, it does nothing
    ///     (apart from a quiet background version check that can prompt an update).
    ///   - If the tool is missing, it fetches /passport-package and imports it.
    ///
    /// It is safe to leave this file in the project permanently. After the tool is
    /// installed this script just no-ops on each load. Customers can also trigger a
    /// manual install/update from the Synty menu.
    /// </summary>
    [InitializeOnLoad]
    public static class SyntyImporter_Installer
    {
        private const string WorkerBaseUrl = "https://synty-downloads.syntystore.workers.dev";
        private const string PackageEndpoint = WorkerBaseUrl + "/passport-package";
        private const string VersionEndpoint = WorkerBaseUrl + "/passport-version";

        // Fully-qualified type name of the installed tool's main window. Used to detect
        // whether Passport is already present without a hard assembly reference (the
        // bootstrapper compiles fine on its own, before the tool exists).
        private const string PassportTypeName = "Synty.Tools.V2.SyntyAssetDownloaderV2, Assembly-CSharp-Editor";

        // EditorPrefs keys (project-scoped via the data path hash so multiple projects
        // do not stomp each other's install state).
        private static readonly string PrefInstallAttempted = "SyntyPassport_BootstrapInstallAttempted_" + ProjectKey();
        private static readonly string PrefInstalledVersion = "SyntyPassport_InstalledVersion_" + ProjectKey();
        private static readonly string PrefOpenAfterImport = "SyntyPassport_OpenAfterImport_" + ProjectKey();
        // Per-project auto-update toggle. Default ON. Turn OFF on dev machines so a working copy
        // is never overwritten by whatever version is live on the server.
        private static readonly string PrefAutoUpdate = "SyntyPassport_AutoUpdate_" + ProjectKey();
        private const string AutoUpdateMenuPath = "Synty/Internal/Auto-Update Synty Importer";

        private static bool AutoUpdateEnabled => EditorPrefs.GetBool(PrefAutoUpdate, true);
        
        // True if this project contains the dev debug-unlock marker. Detected via reflection so the
        // installer needs no compile-time reference (it may be the only Synty file in a project).
        private static bool IsDevProject()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType("Synty.Passport.Dev.SyntyDebugUnlock") != null) return true; }
                catch { }
            }
            return false;
        }
        
        // Auto-update is allowed only when the toggle is ON and this is not a dev project.
        private static bool AutoUpdateAllowed => AutoUpdateEnabled && !IsDevProject();

        // Dev-only: the "Auto-Update" menu item is REGISTERED (see the static constructor below) only
        // when the developer marker (SyntyDebugUnlock) is present, so the whole Internal submenu is
        // HIDDEN in customer projects — a [MenuItem] validate can only grey it out. Auto-update still
        // runs for customers (defaults ON) and is automatically OFF on dev projects via the marker.
        private static void ToggleAutoUpdate()
        {
            EditorPrefs.SetBool(PrefAutoUpdate, !AutoUpdateEnabled);
            Menu.SetChecked(AutoUpdateMenuPath, AutoUpdateEnabled);
        }

        private static bool _busy;

        static SyntyImporter_Installer()
        {
            // Register the dev-only Auto-Update menu item ONLY when the marker is present, so the
            // Internal submenu doesn't exist at all in customer projects. Menu.AddMenuItem is internal,
            // so it's invoked via reflection.
            if (IsDevProject())
                EditorApplication.delayCall += () =>
                    AddMenuItemReflective(AutoUpdateMenuPath, AutoUpdateEnabled, 1000, ToggleAutoUpdate);
            // Defer one tick so this runs after the editor finishes its own load and the
            // AssetDatabase is ready to import.
            EditorApplication.delayCall += AutoRunOnLoad;
        }

        // UnityEditor.Menu.AddMenuItem is internal, so call it via reflection to register a menu item
        // at runtime (used to show the dev-only Auto-Update item only when the marker is present).
        private static void AddMenuItemReflective(string path, bool isChecked, int priority, System.Action execute)
        {
            try
            {
                var m = typeof(Menu).GetMethod("AddMenuItem",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(string), typeof(string), typeof(bool), typeof(int), typeof(System.Action), typeof(System.Func<bool>) },
                    null);
                if (m != null) m.Invoke(null, new object[] { path, "", isChecked, priority, execute, null });
            }
            catch { }
        }

        private static void AutoRunOnLoad()
        {
            // Clear any install progress bar left over from before a domain reload. The import that
            // triggers the reload wipes our importPackageCompleted handler, so the bar can otherwise
            // stay stuck on screen after the tool has already opened.
            try { EditorUtility.ClearProgressBar(); } catch { }

            // If a previous import just completed, we may owe the user an auto-open.
            if (SessionState.GetBool("SyntyPassport_JustImported", false))
            {
                SessionState.SetBool("SyntyPassport_JustImported", false);
                OpenPassportIfRequested();
                return;
            }

            if (IsPassportInstalled())
            {
                // Installed already. Do a version check on load: if the worker reports a
                // newer version than what we have, download and import it automatically
                // before the user starts working. We only auto-update ONCE per editor
                // session (tracked via SessionState) so a transient version mismatch can't
                // loop. The update does not auto-open Passport (the user didn't ask to open
                // it; they just launched the editor) - it silently swaps in the new tool.
                if (!SessionState.GetBool("SyntyPassport_UpdateCheckedThisSession", false))
                {
                    SessionState.SetBool("SyntyPassport_UpdateCheckedThisSession", true);
                    if (AutoUpdateAllowed) CheckForUpdateThenMaybeInstall(openAfter: false);
                }
                return;
            }

            // Not installed. Only auto-install once per project so a customer who
            // deliberately removed the tool is not fighting the bootstrapper on every
            // load. They can always reinstall from the Synty menu.
            // NOTE: the "attempted" flag is set only after a SUCCESSFUL install (see
            // InstallLatest), so a failed first attempt (network/worker down) will retry on
            // the next load rather than locking the user out.
            if (EditorPrefs.GetBool(PrefInstallAttempted, false)) return;

            EditorPrefs.SetBool(PrefOpenAfterImport, true); // first install -> open after
            InstallLatest(isManual: false);
        }

        // Called when the user explicitly opens Passport. Checks for a newer version and,
        // if one exists, updates first; the freshly imported tool then opens via the
        // post-import flow. If already up to date (or the check fails), opens immediately.
        // This is the entry point wired to the Synty/Synty Passport menu so "open" always
        // means "open the latest".
        public static void OpenPassportLatest()
        {
            if (_busy) return;

            if (!IsPassportInstalled())
            {
                // Tool missing entirely - install it and open afterwards.
                EditorPrefs.SetBool(PrefOpenAfterImport, true);
                InstallLatest(isManual: true);
                return;
            }

            // Tool present - check version. If newer, update then open; else open now.
            // When auto-update is disabled (or this is a dev project), always open the installed
            // tool as-is so a working copy is never replaced by the server version.
            if (!AutoUpdateAllowed)
            {
                OpenInstalledPassport();
                return;
            }
            FetchVersion(remoteVersion =>
            {
                string local = EditorPrefs.GetString(PrefInstalledVersion, "");
                if (!string.IsNullOrEmpty(remoteVersion) && remoteVersion != local)
                {
                    // Newer version available - update first, open after import.
                    EditorPrefs.SetBool(PrefOpenAfterImport, true);
                    InstallLatest(isManual: true);
                }
                else
                {
                    // Up to date (or version unknown) - open the installed tool directly.
                    OpenInstalledPassport();
                }
            });
        }

        // Fetches the remote version, then installs only if it differs from what we have.
        // openAfter controls whether Passport opens once the update is imported.
        private static void CheckForUpdateThenMaybeInstall(bool openAfter)
        {
            FetchVersion(remoteVersion =>
            {
                if (string.IsNullOrEmpty(remoteVersion)) return; // check failed; leave as-is
                string local = EditorPrefs.GetString(PrefInstalledVersion, "");
                if (remoteVersion != local)
                {
                    // Debug.Log($"[Synty Importer] Updating tool: {(string.IsNullOrEmpty(local) ? "unknown" : local)} -> {remoteVersion}");
                    EditorPrefs.SetBool(PrefOpenAfterImport, openAfter);
                    InstallLatest(isManual: false);
                }
            });
        }

        // ── Menu items ───────────────────────────────────────────────────

        // Primary entry point customers use. Always opens the LATEST: checks for an update
        // first, installs it if newer, then opens. This is a separate menu path from the
        // tool's own "Synty/Synty Passport" item (which opens whatever is installed without
        // a version check), so both can coexist. Lower priority puts it just under the
        // tool's own entry.
        [MenuItem("Synty/Synty Importer", priority = 0)]
        public static void MenuSyntyPassport()
        {
            // One entry that does the right thing: installs if missing, updates if a newer
            // version exists, then opens Passport.
            OpenPassportLatest();
        }

        [MenuItem("Synty/Synty Importer", validate = true)]
        private static bool MenuSyntyPassportValidate()
        {
            return !_busy;
        }

        // True when the SyntyDebugUnlock marker type exists (internal/QA project). Customers
        // never have this file, so they never see the Live/Dev choice.
        private static bool HasDebugUnlockMarker()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType("Synty.Passport.Dev.SyntyDebugUnlock") != null) return true; }
                catch { }
            }
            return false;
        }

        // ── Install / update ─────────────────────────────────────────────

        private static void InstallLatest(bool isManual)
        {
            if (_busy) return;

            // Internal/QA projects (SyntyDebugUnlock marker present) may choose Live or the newer
            // Dev build. Customers (no marker) silently get Live.
            string channel = "live";
            if (isManual && HasDebugUnlockMarker())
            {
                int choice = EditorUtility.DisplayDialogComplex(
                    "Synty Importer",
                    "A developer marker (SyntyDebugUnlock) is present in this project.\n\n" +
                    "Install the LIVE version (what customers get) or the DEV version (newest upload, for QA)?",
                    "Install Live",   // 0
                    "Cancel",         // 1
                    "Install Dev");   // 2
                if (choice == 1) return;
                channel = (choice == 2) ? "dev" : "live";
            }

            _busy = true;

            // Show a progress bar for BOTH auto and manual installs so a fresh-project drop makes
            // it obvious the tool is downloading (previously the auto-install was silent).
            EditorUtility.DisplayProgressBar("Synty Importer", "Downloading the Synty Importer tool...", 0.05f);

            var req = UnityWebRequest.Get(PackageEndpoint + "?channel=" + channel);
            req.downloadHandler = new DownloadHandlerBuffer();
            var op = req.SendWebRequest();
            // Live download-progress ticker so the bar reflects real download progress.
            EditorApplication.CallbackFunction progressTick = null;
            progressTick = () =>
            {
                if (op.isDone) { EditorApplication.update -= progressTick; return; }
                try
                {
                    float p = Mathf.Clamp01(req.downloadProgress);
                    EditorUtility.DisplayProgressBar("Synty Importer",
                        $"Downloading the Synty Importer tool...  {(int)(p * 100)}%",
                        0.05f + p * 0.6f);
                }
                catch { }
            };
            EditorApplication.update += progressTick;
            op.completed += _ =>
            {
                EditorApplication.update -= progressTick;
                try
                {
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        ClearProgress();
                        _busy = false;
                        string msg = $"Could not download Synty Importer.\n\n{req.error}\n\nCheck your internet connection and try again from the Synty menu.";
                        if (isManual) EditorUtility.DisplayDialog("Synty Importer", msg, "OK");
                        // else Debug.LogWarning("[Synty Importer] " + msg);  (log suppressed)
                        return;
                    }

                    byte[] bytes = req.downloadHandler.data;
                    if (bytes == null || bytes.Length == 0)
                    {
                        ClearProgress();
                        _busy = false;
                        if (isManual) EditorUtility.DisplayDialog("Synty Importer", "The downloaded package was empty. Please try again shortly.", "OK");
                        return;
                    }

                    EditorUtility.DisplayProgressBar("Synty Importer", "Installing the Synty Importer tool...", 0.75f);

                    // Write to a temp file and import. interactive:false so it imports
                    // silently without the import dialog for a smooth first-run experience.
                    string tempPath = Path.Combine(Path.GetTempPath(), "SyntyPassport_" + Guid.NewGuid().ToString("N") + ".unitypackage");
                    File.WriteAllBytes(tempPath, bytes);

                    UnityEngine.Debug.Log("[Synty Importer] A new version is available, updating...");

                    // Flag that an import is in flight so the post-import reload can open
                    // Passport. Import triggers a domain reload, so we cannot rely on
                    // in-memory state surviving; SessionState persists across the reload.
                    SessionState.SetBool("SyntyPassport_JustImported", true);
                    // Download succeeded and import is starting — NOW mark the auto-install as
                    // attempted so we don't reinstall on every load. (Set here, not before the
                    // download, so a failed download retries next load instead of locking out.)
                    EditorPrefs.SetBool(PrefInstallAttempted, true);

                    // Keep the "Installing..." bar up while Unity imports; clear it when the import
                    // finishes (importPackageCompleted). Import is async and may trigger a domain
                    // reload — the progress bar is cleared either way.
                    AssetDatabase.importPackageCompleted += OnInstallImportDone;
                    AssetDatabase.importPackageFailed += OnInstallImportFailed;
                    AssetDatabase.ImportPackage(tempPath, false);

                    // Record the version we just installed (best-effort background fetch).
                    FetchAndStoreVersion();

                    _busy = false;

                    // The package's own asset import callbacks will finish after this.
                    // Auto-open is handled on the next load tick via the JustImported flag,
                    // but also try now in case no domain reload happens.
                    EditorApplication.delayCall += OpenPassportIfRequested;
                }
                catch (Exception e)
                {
                    ClearProgress();
                    _busy = false;
                    // Debug.LogError("[Synty Importer] Install failed: " + e.Message);
                    if (isManual) EditorUtility.DisplayDialog("Synty Importer", "Install failed:\n" + e.Message, "OK");
                }
            };
        }

        // ── Helpers ──────────────────────────────────────────────────────

        // True if the installed Passport tool type is present in the compiled project.
        private static bool IsPassportInstalled()
        {
            return Type.GetType(PassportTypeName) != null;
        }

        // Opens the Passport window if an install just requested it. Clears the flag so
        // it only happens once per install.
        private static void OpenPassportIfRequested()
        {
            if (!EditorPrefs.GetBool(PrefOpenAfterImport, false)) return;
            if (!IsPassportInstalled()) return; // tool not compiled yet; will retry next reload via flag

            EditorPrefs.SetBool(PrefOpenAfterImport, false);
            OpenPassportWindow();
        }

        // Opens the installed tool window via reflection on its public static ShowWindow() — no hard
        // assembly reference (the bootstrapper compiles standalone), and no dependency on an
        // "Open Window" menu item (which was removed from the tool).
        private static void OpenPassportWindow()
        {
            try
            {
                var t = Type.GetType(PassportTypeName);
                var m = t?.GetMethod("ShowWindow", BindingFlags.Public | BindingFlags.Static);
                m?.Invoke(null, null);
            }
            catch (Exception)
            {
                // Debug.LogWarning("[Synty Importer] Could not auto-open the tool window.");
            }
        }

        // Background fetch of the current version string so we can store what we have.
        private static void FetchAndStoreVersion()
        {
            try
            {
                var req = UnityWebRequest.Get(VersionEndpoint);
                req.downloadHandler = new DownloadHandlerBuffer();
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    try
                    {
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var info = JsonUtility.FromJson<VersionInfo>(req.downloadHandler.text);
                            if (info != null && !string.IsNullOrEmpty(info.version))
                            {
                                EditorPrefs.SetString(PrefInstalledVersion, info.version);
                                // Shared, non-project-scoped key the tool reads to display
                                // its version. The package filename (parsed by the worker)
                                // is the single source of truth; this carries it to the tool.
                                EditorPrefs.SetString("SyntyPassport_DisplayVersion", info.version);
                            }
                        }
                    }
                    catch { }
                    finally { req.Dispose(); }
                };
            }
            catch { }
        }

        // Fetches the remote version string and hands it to the callback (empty string on
        // failure). Non-blocking; the callback runs on the main thread via the request's
        // completed event.
        private static void FetchVersion(Action<string> onResult)
        {
            try
            {
                var req = UnityWebRequest.Get(VersionEndpoint);
                req.downloadHandler = new DownloadHandlerBuffer();
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    string version = "";
                    try
                    {
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var info = JsonUtility.FromJson<VersionInfo>(req.downloadHandler.text);
                            if (info != null && info.available && !string.IsNullOrEmpty(info.version))
                            {
                                version = info.version;
                                // Keep the tool-facing display version current on every check.
                                EditorPrefs.SetString("SyntyPassport_DisplayVersion", version);
                            }
                        }
                    }
                    catch { }
                    finally { req.Dispose(); }
                    try { onResult?.Invoke(version); } catch (Exception) { /* version callback error (log suppressed) */ }
                };
            }
            catch
            {
                try { onResult?.Invoke(""); } catch { }
            }
        }

        // Opens the installed tool window when we are already up to date and just need to show it.
        private static void OpenInstalledPassport()
        {
            OpenPassportWindow();
        }

        private static void OnInstallImportDone(string packageName)
        {
            AssetDatabase.importPackageCompleted -= OnInstallImportDone;
            AssetDatabase.importPackageFailed -= OnInstallImportFailed;
            ClearProgress();
        }

        private static void OnInstallImportFailed(string packageName, string errorMessage)
        {
            AssetDatabase.importPackageCompleted -= OnInstallImportDone;
            AssetDatabase.importPackageFailed -= OnInstallImportFailed;
            ClearProgress();
        }

        private static void ClearProgress()
        {
            try { EditorUtility.ClearProgressBar(); } catch { }
        }

        // Stable per-project key so install state does not leak between projects that
        // share an EditorPrefs store (same machine + Unity version).
        private static string ProjectKey()
        {
            string p = Application.dataPath;
            int h = 17;
            foreach (char c in p) h = h * 31 + c;
            return (h & 0x7fffffff).ToString();
        }

        [Serializable]
        private class VersionInfo
        {
            public string version;
            public bool available;
            public string updated;
        }
    }
}
