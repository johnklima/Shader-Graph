using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

// =============================================================================
//  CANONICAL LOCATION:  Assets/Synty/Importer/Editor/SyntyDownloadManager.cs
//  -------------------------------------------------------------------------
//  This file must exist as exactly ONE copy, and ONLY inside an `Editor`
//  folder. It uses [InitializeOnLoad], EditorApplication and AssetDatabase,
//  so it can only compile in the Editor assembly.
//
//  If a second copy of this file (or any other .cs declaring
//  `class SyntyDownloadManager`) exists OUTSIDE an Editor folder, it gets
//  compiled into Assembly-CSharp as well and Unity reports:
//      warning CS0436: The type 'SyntyDownloadManager' ... conflicts with
//      the imported type 'SyntyDownloadManager' in 'Assembly-CSharp'
//  Fix: delete the stray copy (and its .meta). Do NOT duplicate this file.
// =============================================================================

namespace Synty.Tools
{
    /// <summary>
    /// Manages download queue and progress for Synty assets
    /// Persists queue state across domain reloads (script compilation)
    /// </summary>
    [InitializeOnLoad]
    public class SyntyDownloadManager
    {
        #region Singleton
        
        private static SyntyDownloadManager _instance;
        public static SyntyDownloadManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new SyntyDownloadManager();
                }
                return _instance;
            }
        }
        
        // Static constructor for InitializeOnLoad - runs after every domain reload
        static SyntyDownloadManager()
        {
            // Delay initialization to ensure everything is ready
            EditorApplication.delayCall += OnDomainReloadComplete;
        }
        
        private static void OnDomainReloadComplete()
        {
            // Always create the Instance after a domain reload so its self-healing watchdog is
            // registered (it advances any stalled queue even if the SessionState resume below
            // doesn't fire). Touching Instance also re-subscribes the manager's event handlers.
            var instance = Instance;
            
            // Check if we have a pending queue to resume
            string pendingQueue = SessionState.GetString(SESSION_QUEUE_KEY, "");
            if (!string.IsNullOrEmpty(pendingQueue))
            {
                instance.RestoreAndResumeQueue();
            }
        }
        
        #endregion
        
        #region Constants
        
        // Shared download cache — persists across projects and Unity sessions
        // Located in user's AppData (Windows: %LOCALAPPDATA%/Synty/Downloads, Mac: ~/Library/Application Support/Synty/Downloads)
        private static readonly string SHARED_DOWNLOAD_CACHE = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
            "Synty", "Downloads");
        
        /// <summary>
        /// The shared download cache folder where downloaded package files are kept
        /// (cross-platform: Windows %LOCALAPPDATA%/Synty/Downloads, Mac ~/Library/Application Support/Synty/Downloads).
        /// </summary>
        public static string DownloadCacheFolder => SHARED_DOWNLOAD_CACHE;
        // Disk-backed copy of the pending queue, so a batch can resume even after a full Unity
        // restart or crash (SessionState only survives domain reloads, not a quit). Lives in the
        // cache folder alongside the downloaded packages.
        private static string BatchResumeFile => Path.Combine(SHARED_DOWNLOAD_CACHE, "_batch_resume.json");
        
        /// <summary>
        /// Check if an asset's package file exists in the shared download cache.
        /// </summary>
        public static bool IsInDownloadCache(string bucketFilename)
        {
            if (string.IsNullOrEmpty(bucketFilename)) return false;
            string path = Path.Combine(SHARED_DOWNLOAD_CACHE, bucketFilename);
            if (!File.Exists(path)) return false;
            // Unity packages must be a valid gzip. A partial/corrupt cache file (e.g. from an
            // interrupted download) would otherwise be treated as a hit and fail import with
            // "Couldn't decompress package". Non-package files fall back to a size check.
            if (bucketFilename.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                return IsValidPackageFile(path);
            return new FileInfo(path).Length > 1024;
        }

        // A .unitypackage is a gzip tarball. Treat a file as usable only if it exists, is a sane
        // size, AND begins with the gzip magic bytes (0x1F 0x8B). This rejects truncated/partial
        // downloads and error responses (HTML/JSON) saved with a .unitypackage name — both of which
        // fail import with "Couldn't decompress package".
        public static bool IsValidPackageFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                if (new FileInfo(path).Length < 1024) return false;
                using (var fs = File.OpenRead(path))
                    return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
            }
            catch { return false; }
        }
        private const string SESSION_QUEUE_KEY = "SyntyDownloadManager_SessionQueue";
        private const string SESSION_PROCESSING_KEY = "SyntyDownloadManager_WasProcessing";
        
        #endregion
        
        #region Data Classes
        
        public enum DownloadStatus
        {
            Queued,
            Downloading,
            Importing,
            Completed,
            Failed,
            Cancelled
        }
        
        [Serializable]
        public class DownloadItem
        {
            public string productHandle;
            public string productTitle;
            public string productVersion;
            public string bucketFilename;
            public string storeUrl;
            public bool isCacheHit;
            public DownloadStatus status;
            public float progress;
            public string errorMessage;
            public string downloadedFilePath;
            public DateTime? queuedAt;
            public DateTime? startedAt;
            public DateTime? completedAt;
            // Timing for the post-batch summary (seconds). -1 = not measured.
            public double downloadSeconds = -1;
            public double installSeconds = -1;
            public DateTime? downloadStartedAt;
            public DateTime? installStartedAt;
            // Fixed 1-based position of this pack within its install batch. Assigned once when the
            // install is started and never changed, so the pack's "X of Y" number is stable across
            // window focus changes and domain reloads.
            public int batchIndex = 0;
        }
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Current download queue
        /// </summary>
        public List<DownloadItem> Queue { get; private set; } = new List<DownloadItem>();
        
        /// <summary>
        /// Number of active downloads
        /// </summary>
        public int ActiveDownloads => _activeDownloads.Count;
        
        /// <summary>
        /// Number of queued downloads
        /// </summary>
        public int QueuedCount => Queue.FindAll(d => d.status == DownloadStatus.Queued).Count;
        
        /// <summary>
        /// Whether there are active or queued downloads
        /// </summary>
        // Set true by the tool while it has locked assembly reloading for a multi-install batch.
        // While locked, the between-item compile wait must be skipped — no compile can run until
        // the tool unlocks, so waiting for one would deadlock the queue.
        public static bool AssembliesLockedForBatch = false;
        
        public bool HasActiveWork => ActiveDownloads > 0 || QueuedCount > 0 || _isWaitingForImport;
        
        /// <summary>
        /// Whether we're waiting for an import to complete
        /// </summary>
        public bool IsWaitingForImport => _isWaitingForImport;
        
        /// <summary>
        /// The item currently being imported (if any)
        /// </summary>
        public DownloadItem CurrentImportingItem => _currentImportingItem;
        /// <summary>The product handle currently downloading or importing (null if idle).</summary>
        public string CurrentProductHandle => _currentProductHandle;
        
        #endregion
        
        #region Events
        
        public event Action<DownloadItem> OnItemAdded;
        public event Action<DownloadItem> OnItemStatusChanged;
        public event Action<DownloadItem> OnItemProgressChanged;
        public event Action<DownloadItem> OnItemCompleted;
        public event Action<DownloadItem> OnItemFailed;
        public event Action OnQueueChanged;
        // Download-only batch: when true, StartDownload fetches to cache but does NOT import; the
        // batch just caches every pack. OnDownloadOnlyBatchComplete fires when the queue drains.
        public bool DownloadOnlyMode = false;
        public event Action OnDownloadOnlyBatchComplete;
        
        #endregion
        
        #region Private Fields
        
        private Dictionary<string, DownloadItem> _activeDownloads = new Dictionary<string, DownloadItem>();
        private bool _isWaitingForImport = false;
        private DownloadItem _currentImportingItem = null;
        private float _importStartTime = 0f;
        
        #endregion
        
        #region Constructor
        
        private SyntyDownloadManager()
        {
            // Subscribe to service events
            SyntyStoreService.Instance.OnDownloadProgress += HandleDownloadProgress;
            SyntyStoreService.Instance.OnDownloadComplete += HandleDownloadComplete;
            SyntyStoreService.Instance.OnDownloadFailed += HandleDownloadFailed;
            
            // Subscribe to Unity import events
            AssetDatabase.importPackageCompleted += OnImportPackageCompleted;
            AssetDatabase.importPackageCancelled += OnImportPackageCancelled;
            AssetDatabase.importPackageFailed += OnImportPackageFailed;
            
            // CheckImportStatus is subscribed on-demand when _isWaitingForImport is set
            
            // Self-healing watchdog: runs independently of the tool window, so the queue keeps
            // advancing even if a domain reload (from an asset's script recompile) interrupted the
            // batch and the normal resume was missed. This is the authoritative recovery — the
            // tool's UI watchdog can't help here because its own state resets on the reload.
            EditorApplication.update += SelfHealWatchdog;

            // Background keep-alive: while there is active download/import work, force the editor to
            // keep pumping its loop even when the window is unfocused. Lives on the manager (a
            // ScriptableSingleton) so it survives the domain reloads a multi-asset install causes —
            // unlike a tool-window subscription, which resets on every reload.
            EditorApplication.update += InstallKeepAlivePump;

            // Persist the queue right before any domain reload, so whatever is pending survives a
            // recompile triggered by importing an asset with scripts.
            AssemblyReloadEvents.beforeAssemblyReload += SaveQueueToSession;
        }

        // Keeps the editor ticking at full speed while a batch is running, so clicking out of Unity
        // doesn't stall the install. No-op (near-zero cost) whenever there's no active work.
        private double _lastKeepAliveRepaint = 0;
        private void InstallKeepAlivePump()
        {
            if (!HasActiveWork) return;
            EditorApplication.QueuePlayerLoopUpdate(); // request the next tick immediately, defeating the unfocused throttle
            // Repaint at most ~4x/sec rather than every tick. Repainting ALL editor views on every
            // tick during a long batch churns a huge number of allocations and can exhaust Unity's
            // Domain allocator ("reached its limit of 262144 tracked allocations"). 4fps is plenty
            // to keep the progress UI moving.
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastKeepAliveRepaint >= 0.25)
            {
                _lastKeepAliveRepaint = now;
                UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            }
        }
        
        private double _selfHealLastCheck = 0;
        private double _lastHeartbeat = 0;
        private void SelfHealWatchdog()
        {
            // Throttle to ~2x/second.
            double now = EditorApplication.timeSinceStartup;
            if (now - _selfHealLastCheck < 0.5) return;
            _selfHealLastCheck = now;
            
            // Diagnostic heartbeat (debug logging only): logs state every ~5s during active work so
            // if the batch stops, the LAST heartbeat shows exactly which item/phase it froze on.
            if (HasActiveWork && (now - _lastHeartbeat) > 5.0 &&
                EditorPrefs.GetBool("SyntyImporter_DebugLogging", false))
            {
                _lastHeartbeat = now;
                Synty.Tools.SyntyLog.Info($"[HEARTBEAT] dl={_currentlyDownloading} imp={_currentlyImporting} wait={_isWaitingForImport} updating={EditorApplication.isUpdating} compiling={EditorApplication.isCompiling} item='{(_currentImportingItem != null ? _currentImportingItem.productTitle : _currentProductHandle)}' active={_activeDownloads.Count} queued={QueuedCount}");
            }

            // Import-timeout recovery FIRST — before the isCompiling/isUpdating bail below. A hung
            // import keeps EditorApplication.isUpdating true indefinitely, so gating this behind that
            // check made it unreachable and the batch could freeze forever on one import. The generous
            // timeout means a genuinely slow large import isn't force-completed early.
            if (_isWaitingForImport && _currentImportingItem != null)
            {
                float impWaited = (float)EditorApplication.timeSinceStartup - _importStartTime;
                float impTimeout = _currentImportingItem.isCacheHit ? 30f : 180f;
                if (impWaited > impTimeout)
                {
                    Synty.Tools.SyntyLog.Warn($"Import of {_currentImportingItem.productTitle} exceeded {impTimeout}s (stuck / callback lost) — force-completing and continuing.");
                    CompleteImport(_currentImportingItem); // advances the queue
                    return;
                }
            }

            // Never act on the remaining recoveries while Unity is genuinely busy — the reload/compile
            // path resumes on its own.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            
            // Nothing to do unless there are still queued items waiting.
            if (!Queue.Exists(q => q.status == DownloadStatus.Queued)) return;
            
            // If something is genuinely in progress, leave it alone — with two exceptions handled
            // below: a download stuck well past any legitimate duration, and an import whose callback
            // was lost across a domain reload.
            if (_currentlyDownloading || _activeDownloads.Count > 0)
            {
                // Backup recovery: if a download has been running far longer than any real download
                // should (the in-download stall watchdog was somehow lost), mark it failed and move on
                // so the whole batch doesn't freeze on one bad connection.
                DownloadItem stuck = null;
                foreach (var kv in _activeDownloads)
                {
                    var it = kv.Value;
                    if (it != null && it.downloadStartedAt.HasValue &&
                        (DateTime.Now - it.downloadStartedAt.Value).TotalMinutes > 6)
                    { stuck = it; break; }
                }
                if (stuck == null) return; // still within a plausible download window
                Synty.Tools.SyntyLog.Warn($"Download for {stuck.productTitle} appears stuck — recovering and continuing.");
                stuck.status = DownloadStatus.Failed;
                stuck.errorMessage = "Download timed out.";
                _activeDownloads.Remove(stuck.productHandle);
                ResetAllFlags();
                OnItemFailed?.Invoke(stuck);
                OnQueueChanged?.Invoke();
                ProcessQueue();
                return;
            }
            // Import in progress: timeout recovery already handled at the top of the watchdog. Here we
            // only clean up the degenerate "waiting but no item" case, otherwise leave it running.
            if (_isWaitingForImport)
            {
                if (_currentImportingItem == null) { ResetAllFlags(); }
                else return;
            }
            if (_currentlyImporting)
                return;
            
            // Queued items exist and nothing is running: the batch stalled. Restart it.
            ResetAllFlags();
            ProcessQueue();
        }
        
        #endregion
        
        #region Persistence
        
        [Serializable]
        private class SerializedQueue
        {
            public List<SerializedItem> items = new List<SerializedItem>();
        }
        
        [Serializable]
        private class SerializedItem
        {
            public string productHandle;
            public string productTitle;
            public string productVersion;
            public string bucketFilename;
            public string storeUrl;
        }
        
        /// <summary>
        /// Save pending queue items to SessionState (survives domain reload)
        /// </summary>
        // Returns the number of packs in an interrupted (disk-persisted) batch, or 0 if none. Lets
        // the tool prompt before resuming rather than auto-starting a long install on launch.
        public int PendingDiskResumeCount()
        {
            try
            {
                if (!string.IsNullOrEmpty(SessionState.GetString(SESSION_QUEUE_KEY, ""))) return 0;
                if (!File.Exists(BatchResumeFile)) return 0;
                var s = JsonUtility.FromJson<SerializedQueue>(File.ReadAllText(BatchResumeFile));
                if (s == null || s.items == null) return 0;
                // Only count packs that aren't already installed. A single-pack install records its
                // manifest BEFORE importing, so by the time we reach here the just-installed pack
                // already has a manifest; without this filter its stale resume entry fires a phantom
                // "Resume Install" prompt (and a second import if the user clicks Resume).
                int pending = 0;
                foreach (var it in s.items)
                    if (it != null && !SyntyInstallManifest.HasManifest(it.productHandle)) pending++;
                // Every pack in the file is already installed → the file is stale. Clear it so it
                // can never prompt again.
                if (pending == 0) ClearBatchResumeDisk();
                return pending;
            }
            catch { return 0; }
        }

        public void DiscardDiskResume() { ClearBatchResumeDisk(); }

        // Writes the pending-queue json to disk (survives a full Unity restart/crash).
        private void SaveBatchResumeToDisk(string json)
        {
            try
            {
                Directory.CreateDirectory(SHARED_DOWNLOAD_CACHE);
                File.WriteAllText(BatchResumeFile, json);
            }
            catch { }
        }

        private void ClearBatchResumeDisk()
        {
            try { if (File.Exists(BatchResumeFile)) File.Delete(BatchResumeFile); }
            catch { }
        }

        // On startup, if SessionState has no pending queue (e.g. after a full restart) but a disk
        // resume file exists, restore the batch from disk and resume. Returns true if it resumed.
        public bool TryResumeBatchFromDisk()
        {
            try
            {
                // If SessionState already has a live queue, the normal reload-resume handles it.
                if (!string.IsNullOrEmpty(SessionState.GetString(SESSION_QUEUE_KEY, ""))) return false;
                if (!File.Exists(BatchResumeFile)) return false;
                string json = File.ReadAllText(BatchResumeFile);
                if (string.IsNullOrEmpty(json)) { ClearBatchResumeDisk(); return false; }
                var serialized = JsonUtility.FromJson<SerializedQueue>(json);
                if (serialized == null || serialized.items == null || serialized.items.Count == 0)
                { ClearBatchResumeDisk(); return false; }

                // Rebuild queue items. Skip any whose file isn't cached AND that are already
                // installed — but simplest and safe: re-queue all; cached ones import fast, missing
                // ones re-download.
                Queue.Clear();
                foreach (var s in serialized.items)
                {
                    // Skip packs that are already installed (e.g. the single pack whose own import
                    // triggered the domain reload that stranded this file). Re-importing them would
                    // pop the "Script Updating Consent" dialog a second time and reinstall.
                    if (s == null || SyntyInstallManifest.HasManifest(s.productHandle)) continue;
                    Queue.Add(new DownloadItem
                    {
                        productHandle = s.productHandle,
                        productTitle = s.productTitle,
                        productVersion = s.productVersion,
                        bucketFilename = s.bucketFilename,
                        storeUrl = s.storeUrl,
                        status = DownloadStatus.Queued
                    });
                }
                if (Queue.Count == 0)
                {
                    // Nothing genuinely left to resume — the file was stale. Clear it and report
                    // "not resumed" so the caller doesn't set batch counters for a no-op.
                    ClearBatchResumeDisk();
                    return false;
                }
                Synty.Tools.SyntyLog.Info($"Resuming interrupted install: {Queue.Count} packs remaining.");
                OnQueueChanged?.Invoke();
                StartProcessing();
                return true;
            }
            catch { return false; }
        }

        // Returns true only if the currently logged-in account genuinely owns the product — either
        // individually or via the Synty Pass. Used to gate cache imports so a shared cached file
        // can't be installed by a non-owner. This reads the store service's verified ownership
        // (populated by the login/verify flow), NOT any UI filter toggle.
        private bool CurrentAccountOwns(string productHandle)
        {
            try
            {
                var svc = SyntyStoreService.Instance;
                if (svc == null) return false;
                if (svc.HasSyntyPass) return true;
                if (string.IsNullOrEmpty(productHandle)) return false;
                if (svc.OwnedProductHandles == null) return false;
                // Normalize for a robust match (handles may differ in case).
                string want = productHandle.Trim().ToLowerInvariant();
                foreach (var h in svc.OwnedProductHandles)
                {
                    if (!string.IsNullOrEmpty(h) && h.Trim().ToLowerInvariant() == want) return true;
                }
                return false;
            }
            catch { return false; }
        }

        private void SaveQueueToSession()
        {
            try
            {
                var serialized = new SerializedQueue();
                foreach (var item in Queue)
                {
                    // Only save items that are still pending (not completed/failed/cancelled)
                    if (item.status == DownloadStatus.Queued || 
                        item.status == DownloadStatus.Downloading || 
                        item.status == DownloadStatus.Importing)
                    {
                        serialized.items.Add(new SerializedItem
                        {
                            productHandle = item.productHandle,
                            productTitle = item.productTitle,
                            productVersion = item.productVersion,
                            bucketFilename = item.bucketFilename,
                            storeUrl = item.storeUrl
                        });
                    }
                }
                
                if (serialized.items.Count > 0)
                {
                    string json = JsonUtility.ToJson(serialized);
                    SessionState.SetString(SESSION_QUEUE_KEY, json);
                    SessionState.SetBool(SESSION_PROCESSING_KEY, true);
                    // Also mirror to disk so a full Unity restart/crash can resume the batch.
                    SaveBatchResumeToDisk(json);
                }
                else
                {
                    ClearSessionState();
                    ClearBatchResumeDisk();
                }
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyDownloadManager] Failed to save queue: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Restore queue from SessionState and resume processing
        /// Called after domain reload (script compilation)
        /// </summary>
        private void RestoreAndResumeQueue()
        {
            try
            {
                string json = SessionState.GetString(SESSION_QUEUE_KEY, "");
                bool wasProcessing = SessionState.GetBool(SESSION_PROCESSING_KEY, false);
                
                // Clear session state immediately to prevent double-restore
                ClearSessionState();
                
                if (string.IsNullOrEmpty(json))
                {
                    return;
                }
                
                var serialized = JsonUtility.FromJson<SerializedQueue>(json);
                if (serialized == null || serialized.items == null || serialized.items.Count == 0)
                {
                    return;
                }
                
                
                // Restore queue items
                foreach (var saved in serialized.items)
                {
                    // Check if already in queue
                    if (Queue.Exists(q => q.productHandle == saved.productHandle))
                        continue;
                    
                    // Skip items that were already installed before the reload
                    if (SyntyInstallManifest.HasManifest(saved.productHandle))
                    {
                        continue;
                    }
                    
                    var item = new DownloadItem
                    {
                        productHandle = saved.productHandle,
                        productTitle = saved.productTitle,
                        productVersion = saved.productVersion,
                        bucketFilename = saved.bucketFilename,
                        storeUrl = saved.storeUrl,
                        status = DownloadStatus.Queued,
                        progress = 0f,
                        queuedAt = DateTime.Now
                    };
                    
                    Queue.Add(item);
                }
                
                OnQueueChanged?.Invoke();

                // If every saved item was already installed (the common single-pack case: the
                // pack's own import caused this reload), nothing is left to resume. Clear the disk
                // mirror now so it can't later trigger a phantom "Resume Install" prompt.
                if (!Queue.Exists(q => q.status == DownloadStatus.Queued ||
                                       q.status == DownloadStatus.Downloading ||
                                       q.status == DownloadStatus.Importing))
                {
                    ClearBatchResumeDisk();
                }

                // If we were processing before the reload, resume after a delay
                if (wasProcessing && Queue.Count > 0)
                {
                    
                    // Wait for compilation to fully complete, then resume
                    EditorApplication.delayCall += WaitForCompilationThenResume;
                }
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyDownloadManager] Failed to restore queue: {ex.Message}");
            }
        }
        
        private void WaitForCompilationThenResume()
        {
            // If still compiling, wait more
            if (EditorApplication.isCompiling)
            {
                EditorApplication.delayCall += WaitForCompilationThenResume;
                return;
            }
            
            // Additional delay to ensure Unity is fully stable
            EditorApplication.delayCall += () =>
            {
                
                // Make sure flags are reset
                ResetAllFlags();
                
                // Start processing if there are queued items
                if (Queue.Exists(q => q.status == DownloadStatus.Queued))
                {
                    StartProcessing();
                }
            };
        }
        
        private void ClearSessionState()
        {
            SessionState.EraseString(SESSION_QUEUE_KEY);
            SessionState.EraseBool(SESSION_PROCESSING_KEY);
        }
        
        #endregion
        
        #region Queue Management
        
        /// <summary>
        /// Add an asset pack to the download queue (does not start download automatically)
        /// </summary>
        public DownloadItem AddToQueue(AssetPackData pack)
        {
            if (pack == null) return null;
            
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (string.IsNullOrEmpty(handle))
            {
                return null;
            }
            
            // Check if already in queue
            var existing = Queue.Find(d => d.productHandle == handle);
            if (existing != null)
            {
                return existing;
            }
            
            var item = new DownloadItem
            {
                productHandle = handle,
                productTitle = pack.GetDisplayName(),
                productVersion = pack.version,
                bucketFilename = pack.bucketFilename,
                storeUrl = pack.storeUrl,
                status = DownloadStatus.Queued,
                progress = 0f,
                queuedAt = DateTime.Now
            };
            
            Queue.Add(item);
            
            // Save queue state for persistence across domain reloads
            SaveQueueToSession();
            
            OnItemAdded?.Invoke(item);
            OnQueueChanged?.Invoke();
            
            return item;
        }
        
        /// <summary>
        /// Add multiple packs to the queue and start processing
        /// </summary>
        public void AddToQueueAndStart(List<AssetPackData> packs)
        {
            if (packs == null) return;
            
            // First add all items to queue
            foreach (var pack in packs)
            {
                AddToQueue(pack);
            }
            
            // Then start processing (only starts first item)
            StartProcessing();
        }
        
        /// <summary>
        /// Start processing the queue (call after adding items)
        /// </summary>
        public void StartProcessing()
        {
            if (_currentlyDownloading || _currentlyImporting || _isDownloadOrImportInProgress || _isWaitingForImport || _activeDownloads.Count > 0)
                return;

            EnableParallelImportForBatch();
            ProcessQueue();
        }

        // Speeds up each package import by using multiple import worker processes (the "Parallel
        // Import" editor setting). Enabled for the duration of a batch, restored afterwards. Guarded
        // so it silently no-ops if the API differs across Unity versions.
        private int _savedWorkerCount = -1;
        private bool _parallelImportChanged = false;
        private void EnableParallelImportForBatch()
        {
            if (_parallelImportChanged) return; // already enabled for this batch
            try
            {
                _savedWorkerCount = UnityEditor.AssetDatabase.DesiredWorkerCount;
                int target = Mathf.Max(2, Environment.ProcessorCount - 1);
                if (_savedWorkerCount < target)
                {
                    UnityEditor.AssetDatabase.DesiredWorkerCount = target;
                    UnityEditor.AssetDatabase.ForceToDesiredWorkerCount();
                    _parallelImportChanged = true;
                    Synty.Tools.SyntyLog.Info($"Enabled parallel import ({target} workers) for this batch.");
                }
            }
            catch { _parallelImportChanged = false; }
        }

        private void RestoreParallelImportAfterBatch()
        {
            if (!_parallelImportChanged) return;
            try
            {
                if (_savedWorkerCount >= 0)
                {
                    UnityEditor.AssetDatabase.DesiredWorkerCount = _savedWorkerCount;
                    UnityEditor.AssetDatabase.ForceToDesiredWorkerCount();
                }
            }
            catch { }
            _parallelImportChanged = false;
        }
        
        /// <summary>
        /// Recovery for a stalled queue: if there are still queued items but nothing is actively
        /// downloading or importing (e.g. a domain reload from an asset's script recompile
        /// interrupted the batch and the resume was missed), restart processing. Safe to call
        /// repeatedly. Returns true if it kicked the queue.
        /// </summary>
        public bool KickQueueIfStalled()
        {
            // Don't interfere while Unity is busy compiling/importing — the reload path resumes.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return false;
            
            // If we've been "waiting for import" well past the timeout, the import-status callback
            // was almost certainly lost across a domain reload. Force-complete it so the queue can
            // advance instead of waiting forever.
            if (_isWaitingForImport && _currentImportingItem != null)
            {
                float waited = (float)EditorApplication.timeSinceStartup - _importStartTime;
                float timeout = _currentImportingItem.isCacheHit ? 30f : 180f;
                if (waited > timeout)
                {
                    var stuck = _currentImportingItem;
                    CompleteImport(stuck); // advances the queue
                    return true;
                }
                return false; // still within a plausible import window
            }
            
            // Something is genuinely in progress — not stalled.
            if (_currentlyDownloading || _currentlyImporting || _activeDownloads.Count > 0)
                return false;
            // Are there queued items waiting?
            bool hasQueued = Queue.Exists(q => q.status == DownloadStatus.Queued);
            if (!hasQueued) return false;
            // Clear any stale flags and restart.
            ResetAllFlags();
            ProcessQueue();
            return true;
        }
        
        /// <summary>
        /// Add multiple packs to the queue
        /// </summary>
        public void AddToQueue(List<AssetPackData> packs)
        {
            if (packs == null) return;
            
            foreach (var pack in packs)
            {
                AddToQueue(pack);
            }
        }
        
        /// <summary>
        /// Remove an item from the queue
        /// </summary>
        public void RemoveFromQueue(string productHandle)
        {
            var item = Queue.Find(d => d.productHandle == productHandle);
            if (item == null) return;
            
            if (item.status == DownloadStatus.Downloading)
            {
                CancelDownload(item);
            }
            else
            {
                Queue.Remove(item);
                OnQueueChanged?.Invoke();
            }
        }
        
        /// <summary>
        /// Cancel a download in progress or remove from queue
        /// </summary>
        public void CancelDownload(DownloadItem item)
        {
            if (item.status == DownloadStatus.Downloading)
            {
                SyntyStoreService.Instance.CancelDownload(item.productHandle);
                item.status = DownloadStatus.Cancelled;
                _activeDownloads.Remove(item.productHandle);
                
                // Reset ALL flags
                ResetAllFlags();
                
                OnItemStatusChanged?.Invoke(item);
                OnQueueChanged?.Invoke();
                
                
                // Process next item since this one was cancelled
                ProcessQueue();
            }
            else if (item.status == DownloadStatus.Queued)
            {
                // Just remove queued items that haven't started
                Queue.Remove(item);
                OnQueueChanged?.Invoke();
            }
            else if (item.status == DownloadStatus.Importing)
            {
                // Can't cancel during import, mark as cancelled so it doesn't continue
                item.status = DownloadStatus.Cancelled;
                
                // Reset ALL flags
                ResetAllFlags();
                
                OnItemStatusChanged?.Invoke(item);
                OnQueueChanged?.Invoke();
                
                
                ProcessQueue();
            }
        }
        
        /// <summary>
        /// Clear completed/failed/cancelled items from queue
        /// </summary>
        public void ClearCompleted()
        {
            Queue.RemoveAll(d => 
                d.status == DownloadStatus.Completed || 
                d.status == DownloadStatus.Failed || 
                d.status == DownloadStatus.Cancelled);
            OnQueueChanged?.Invoke();
        }
        
        /// <summary>
        /// Retry a failed download
        /// </summary>
        public void RetryDownload(DownloadItem item)
        {
            if (item.status == DownloadStatus.Failed || item.status == DownloadStatus.Cancelled)
            {
                item.status = DownloadStatus.Queued;
                item.progress = 0f;
                item.errorMessage = null;
                item.queuedAt = DateTime.Now;
                OnItemStatusChanged?.Invoke(item);
                ProcessQueue();
            }
        }
        
        /// <summary>
        /// Get a download item by handle
        /// </summary>
        public DownloadItem GetItem(string productHandle)
        {
            return Queue.Find(d => d.productHandle == productHandle);
        }
        
        /// <summary>
        /// Cancel a download by handle
        /// </summary>
        public void CancelDownload(string productHandle)
        {
            var item = GetItem(productHandle);
            if (item != null)
            {
                CancelDownload(item);
            }
        }
        
        #endregion
        
        #region Download Processing
        
        // State flags for strict sequential processing
        private bool _isDownloadOrImportInProgress = false;
        private bool _currentlyDownloading = false;
        private bool _currentlyImporting = false;
        private string _currentProductHandle = null;
        
        private void ProcessQueue()
        {
            // STRICT CHECK: If anything is in progress, do NOT start another
            if (_currentlyDownloading || _currentlyImporting || _isDownloadOrImportInProgress)
                return;
            
            if (_activeDownloads.Count > 0)
                return;
            
            if (_isWaitingForImport)
                return;
            
            // Find next queued item
            var nextItem = Queue.Find(d => d.status == DownloadStatus.Queued);
            if (nextItem == null)
            {
                _currentProductHandle = null;
                return;
            }
            
            // Set ALL flags before starting
            _isDownloadOrImportInProgress = true;
            _currentlyDownloading = true;
            _currentlyImporting = false;
            _currentProductHandle = nextItem.productHandle;
            
            StartDownload(nextItem);
        }
        
        private async void StartDownload(DownloadItem item)
        {
            // Final safety check
            if (item.productHandle != _currentProductHandle)
            {
                return;
            }
            
            item.status = DownloadStatus.Downloading;
            item.startedAt = DateTime.Now;
            item.downloadStartedAt = DateTime.Now;
            _activeDownloads[item.productHandle] = item;
            Synty.Tools.SyntyLog.Info($"Downloading {item.productTitle}...");
            
            OnItemStatusChanged?.Invoke(item);
            
            
            try
            {
                string cachePath = SHARED_DOWNLOAD_CACHE;
                Directory.CreateDirectory(cachePath);
                
                // Warn if disk space is critically low before we pull a package down.
                try
                {
                    var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(cachePath)));
                    if (drive.IsReady && drive.AvailableFreeSpace < 500L * 1024 * 1024) // < 500 MB
                        Synty.Tools.SyntyLog.Warn($"Not enough disk space to install {item.productTitle}");
                }
                catch { }
                
                // Check shared cache before making any network call
                if (!string.IsNullOrEmpty(item.bucketFilename))
                {
                    string cachedFile = Path.Combine(cachePath, item.bucketFilename);
                    // Purge a corrupt/incomplete cache file (e.g. left by an interrupted download) so
                    // we re-download a fresh copy instead of failing import with "Couldn't decompress".
                    if (File.Exists(cachedFile) && !IsValidPackageFile(cachedFile))
                    {
                        Synty.Tools.SyntyLog.Warn($"Cached file for {item.productTitle} is corrupt/incomplete — re-downloading.");
                        try { File.Delete(cachedFile); } catch { }
                    }
                    // Version check: if the cached file's recorded version doesn't match the latest
                    // server version (i.e. the asset was updated on the server), delete it so we
                    // re-download the latest instead of importing the stale local copy. A cache file
                    // with no recorded version is treated as unknown and re-downloaded, so we never
                    // install an outdated pack.
                    if (File.Exists(cachedFile) && !string.IsNullOrEmpty(item.productVersion))
                    {
                        string verFile = cachedFile + ".ver";
                        string cachedVersion = "";
                        try { if (File.Exists(verFile)) cachedVersion = File.ReadAllText(verFile).Trim(); } catch { }
                        if (cachedVersion != item.productVersion)
                        {
                            Synty.Tools.SyntyLog.Info($"Cached '{item.productTitle}' is version '{cachedVersion}' but latest is '{item.productVersion}' — re-downloading latest.");
                            try { File.Delete(cachedFile); } catch { }
                            try { if (File.Exists(verFile)) File.Delete(verFile); } catch { }
                        }
                    }
                    if (IsValidPackageFile(cachedFile))
                    {
                        // SECURITY: the download cache is shared across accounts/projects on this
                        // machine, so a cached file does NOT prove the current account owns the asset.
                        // Verify entitlement before importing from cache; otherwise a non-owner could
                        // install a pack another account had downloaded.
                        if (!CurrentAccountOwns(item.productHandle))
                        {
                            item.status = DownloadStatus.Failed;
                            item.errorMessage = "You don't own this product.";
                            _currentlyDownloading = false;
                            _activeDownloads.Remove(item.productHandle);
                            Synty.Tools.SyntyLog.Warn($"Skipped {item.productTitle}: not owned by the current account (cache blocked).");
                            OnItemStatusChanged?.Invoke(item);
                            OnItemFailed?.Invoke(item);
                            ResetAllFlags();
                            SaveQueueToSession();
                            ProcessQueueAfterDelay(0.25);
                            return;
                        }
                        
                        _currentlyDownloading = false;
                        _activeDownloads.Remove(item.productHandle);
                        item.isCacheHit = true;

                        item.downloadedFilePath = cachedFile;

                        // Download-only mode: already cached, so nothing to fetch. Mark complete
                        // (as downloaded, not imported) and advance without importing.
                        if (DownloadOnlyMode)
                        {
                            item.status = DownloadStatus.Completed;
                            item.progress = 1f;
                            OnItemStatusChanged?.Invoke(item);
                            AdvanceDownloadOnly(item);
                            return;
                        }

                        item.status = DownloadStatus.Importing;
                        item.progress = 1f;
                        if (item.downloadStartedAt.HasValue)
                            item.downloadSeconds = (DateTime.Now - item.downloadStartedAt.Value).TotalSeconds;
                        else if (item.isCacheHit) item.downloadSeconds = 0;
                        item.installStartedAt = DateTime.Now;
                        OnItemStatusChanged?.Invoke(item);
                        
                        _currentlyImporting = true;
                        _isWaitingForImport = true;
                        EditorApplication.update += CheckImportStatus;
                        _currentImportingItem = item;
                        _importStartTime = (float)EditorApplication.timeSinceStartup;
                        
                        if (cachedFile.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                        {
                            SyntyInstallManifest.RecordInstall(item.productHandle, item.productTitle, cachedFile, item.productVersion);
                            SyntyInstallManifest.SavePendingImport(item.productHandle);
                            // Defer the actual import a few frames so the tool can scroll the card
                            // into view and repaint BEFORE ImportPackage blocks the main thread.
                            DeferImport(cachedFile);
                        }
                        else
                        {
                            // Not a .unitypackage — just complete immediately
                            CompleteImport(item);
                        }
                        return;
                    }
                }
                
                // Cache miss — download via backend-verified endpoint
                var result = await SyntyStoreService.Instance.DownloadProductAsync(
                    item.productHandle,
                    cachePath
                );
                
                // Download finished - now transition to import phase
                _currentlyDownloading = false;
                _activeDownloads.Remove(item.productHandle);
                
                if (result.Success)
                {
                    item.downloadedFilePath = result.FilePath;

                    // Guard against a "successful" download that returned a bad body (partial file or
                    // an error page). A valid .unitypackage is gzip; if it isn't, treat it as failed so
                    // we don't hit "Couldn't decompress package" on import.
                    if (!string.IsNullOrEmpty(result.FilePath) &&
                        result.FilePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase) &&
                        !IsValidPackageFile(result.FilePath))
                    {
                        try { if (File.Exists(result.FilePath)) File.Delete(result.FilePath); } catch { }
                        item.status = DownloadStatus.Failed;
                        item.errorMessage = "Downloaded file was invalid (corrupt or incomplete).";
                        Synty.Tools.SyntyLog.Error($"Download for {item.productTitle} was not a valid package — discarded.");
                        OnItemStatusChanged?.Invoke(item);
                        OnItemFailed?.Invoke(item);
                        ResetAllFlags();
                        SaveQueueToSession();
                        ProcessQueueAfterDelay(0.25);
                        return;
                    }

                    // Record the cached file's version so a later install can detect if it's stale
                    // (the server got a newer version) and re-download rather than reuse the old copy.
                    try {
                        if (!string.IsNullOrEmpty(result.FilePath) && !string.IsNullOrEmpty(item.productVersion))
                            File.WriteAllText(result.FilePath + ".ver", item.productVersion);
                    } catch { }

                    // Download-only mode: file is now cached. Mark complete and advance without
                    // importing — the install pass runs later after the user confirms.
                    if (DownloadOnlyMode)
                    {
                        item.status = DownloadStatus.Completed;
                        item.progress = 1f;
                        OnItemStatusChanged?.Invoke(item);
                        AdvanceDownloadOnly(item);
                        return;
                    }

                    item.status = DownloadStatus.Importing;
                    item.progress = 1f;
                    if (item.downloadStartedAt.HasValue)
                        item.downloadSeconds = (DateTime.Now - item.downloadStartedAt.Value).TotalSeconds;
                    item.installStartedAt = DateTime.Now;
                    
                    // Transition to import phase
                    _currentlyImporting = true;
                    _isWaitingForImport = true;
                    EditorApplication.update += CheckImportStatus;
                    _currentImportingItem = item;
                    _importStartTime = (float)EditorApplication.timeSinceStartup;
                    
                    OnItemStatusChanged?.Invoke(item);
                    
                    
                    // Import Unity package silently (no dialog)
                    if (result.FilePath.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                    {
                        // Record installed files in manifest before importing
                        SyntyInstallManifest.RecordInstall(item.productHandle, item.productTitle, result.FilePath, item.productVersion);
                        
                        // Save folder snapshot to disk (survives domain reload during import)
                        SyntyInstallManifest.SavePendingImport(item.productHandle);
                        
                        // Defer the actual import a few frames so the tool can scroll the card into
                        // view and repaint BEFORE ImportPackage blocks the main thread.
                        DeferImport(result.FilePath);
                    }
                    else
                    {
                        // Not a unity package, mark as complete immediately
                        CompleteImport(item);
                    }
                }
                else
                {
                    item.status = DownloadStatus.Failed;
                    item.errorMessage = result.Error;
                    Synty.Tools.SyntyLog.Error($"Download failed for {item.productTitle}: {result.Error}");
                    
                    // Reset ALL flags
                    ResetAllFlags();
                    
                    OnItemFailed?.Invoke(item);
                    OnItemStatusChanged?.Invoke(item);
                    OnQueueChanged?.Invoke();
                    
                    
                    // Process next item
                    ProcessQueue();
                }
            }
            catch (Exception ex)
            {
                item.status = DownloadStatus.Failed;
                item.errorMessage = ex.Message;
                Synty.Tools.SyntyLog.Error($"Download failed for {item.productTitle}: {ex.Message}");
                _activeDownloads.Remove(item.productHandle);
                
                // Reset ALL flags
                ResetAllFlags();
                
                OnItemFailed?.Invoke(item);
                OnItemStatusChanged?.Invoke(item);
                OnQueueChanged?.Invoke();
                
                // Debug.LogError($"[SyntyDownloadManager] Download exception for {item.productTitle}: {ex.Message}");
                
                // Process next item
                ProcessQueue();
            }
        }
        
        private void ResetAllFlags()
        {
            _isDownloadOrImportInProgress = false;
            _currentlyDownloading = false;
            _currentlyImporting = false;
            if (_isWaitingForImport)
                EditorApplication.update -= CheckImportStatus;
            _isWaitingForImport = false;
            _currentImportingItem = null;
            _currentProductHandle = null;
        }
        
        // Advances the download-only batch: no import, just move to the next queued item. When none
        // remain, fires OnDownloadOnlyBatchComplete so the tool can prompt then run the install pass.
        // After a download-only batch, flips completed (downloaded-but-not-installed) items back to
        // Queued so a normal StartProcessing pass imports them from cache.
        public void RequeueForInstall()
        {
            foreach (var d in Queue)
            {
                if (d.status == DownloadStatus.Completed)
                    d.status = DownloadStatus.Queued;
            }
            OnQueueChanged?.Invoke();
        }

        private void AdvanceDownloadOnly(DownloadItem item)
        {
            Synty.Tools.SyntyLog.Info($"Downloaded {item.productTitle} (cached, not yet installed)");
            ResetAllFlags();
            SaveQueueToSession();
            OnItemStatusChanged?.Invoke(item);
            OnQueueChanged?.Invoke();

            bool anyLeft = false;
            foreach (var d in Queue)
            {
                if (d.status == DownloadStatus.Queued) { anyLeft = true; break; }
            }
            if (anyLeft)
            {
                ProcessQueueAfterDelay(1.0);
            }
            else
            {
                Synty.Tools.SyntyLog.Info("All downloads finished (nothing installed yet).");
                OnDownloadOnlyBatchComplete?.Invoke();
            }
        }

        // Delays the actual AssetDatabase.ImportPackage call by a few editor frames. The Importing
        // status event has already fired, so the tool scrolls the card into view; those frames let
        // the scroll repaint while the main thread is still idle, BEFORE ImportPackage blocks it.
        private void DeferImport(string packageFile)
        {
            int framesLeft = 3;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (framesLeft-- > 0) return;
                EditorApplication.update -= tick;
                // Silence Unity's own asset-import warnings (rig import notes, self-intersecting
                // polygon discards, etc.) that flood the console during ImportPackage. Restored in
                // the import-completed/cancelled handlers. Best-effort: some worker-process warnings
                // may still slip through, since they're emitted by Unity's native importers.
                SuppressImportLogging(true);
                try { AssetDatabase.ImportPackage(packageFile, false); }
                catch (Exception e) { SuppressImportLogging(false); Synty.Tools.SyntyLog.Error($"Import failed: {e.Message}"); }
            };
            EditorApplication.update += tick;
        }

        private bool _importLoggingSuppressed = false;
        private bool _savedLogEnabled = true;
        private void SuppressImportLogging(bool suppress)
        {
            try
            {
                if (suppress)
                {
                    if (_importLoggingSuppressed) return;
                    _savedLogEnabled = Debug.unityLogger.logEnabled;
                    Debug.unityLogger.logEnabled = false;
                    _importLoggingSuppressed = true;
                }
                else
                {
                    if (!_importLoggingSuppressed) return;
                    Debug.unityLogger.logEnabled = _savedLogEnabled;
                    _importLoggingSuppressed = false;
                }
            }
            catch { _importLoggingSuppressed = false; }
        }

        // Logs a per-pack download/install timing breakdown after a multi-install batch.
        private void LogBatchTimingSummary()
        {
            try
            {
                var done = new List<DownloadItem>();
                foreach (var d in Queue)
                    if (d.status == DownloadStatus.Completed) done.Add(d);
                if (done.Count == 0) return;

                int nameWidth = 4;
                foreach (var d in done)
                    nameWidth = Math.Max(nameWidth, (d.productTitle ?? "").Length);
                nameWidth = Math.Min(nameWidth, 40);

                string Fmt(double s) => s < 0 ? "  -  " : $"{s,6:0.0}s";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Install timing ({done.Count} packs):");
                sb.AppendLine($"  {"Pack".PadRight(nameWidth)}   Download    Install");
                sb.AppendLine($"  {new string('-', nameWidth)}   --------   --------");
                double totalDl = 0, totalIn = 0;
                foreach (var d in done)
                {
                    string name = (d.productTitle ?? "").Length > nameWidth
                        ? d.productTitle.Substring(0, nameWidth) : (d.productTitle ?? "");
                    sb.AppendLine($"  {name.PadRight(nameWidth)}   {Fmt(d.downloadSeconds)}   {Fmt(d.installSeconds)}");
                    if (d.downloadSeconds > 0) totalDl += d.downloadSeconds;
                    if (d.installSeconds > 0) totalIn += d.installSeconds;
                }
                sb.AppendLine($"  {new string('-', nameWidth)}   --------   --------");
                sb.AppendLine($"  {"TOTAL".PadRight(nameWidth)}   {totalDl,6:0.0}s   {totalIn,6:0.0}s");
                sb.Append($"  Combined: {(totalDl + totalIn),0:0.0}s ({(totalDl + totalIn) / 60.0:0.0} min)");
                Synty.Tools.SyntyLog.Info(sb.ToString());
            }
            catch { }
        }

        private void CompleteImport(DownloadItem item)
        {
            if (item == null) return;
            
            
            item.status = DownloadStatus.Completed;
            item.completedAt = DateTime.Now;
            if (item.installStartedAt.HasValue)
                item.installSeconds = (DateTime.Now - item.installStartedAt.Value).TotalSeconds;
            Synty.Tools.SyntyLog.Info(string.IsNullOrEmpty(item.productVersion)
                ? $"Installed {item.productTitle}"
                : $"Installed {item.productTitle} v{item.productVersion}");
            // If nothing else is queued or active, this was the last item in the batch.
            int remaining = 0, completedCount = 0;
            foreach (var d in Queue)
            {
                if (d.status == DownloadStatus.Queued || d.status == DownloadStatus.Downloading || d.status == DownloadStatus.Importing) remaining++;
                if (d.status == DownloadStatus.Completed) completedCount++;
            }
            if (remaining == 0)
            {
                // Whole batch done — remove the disk resume file so nothing re-triggers on restart.
                ClearBatchResumeDisk();
                RestoreParallelImportAfterBatch();
                SuppressImportLogging(false);
                if (completedCount > 1)
                {
                    Synty.Tools.SyntyLog.Info($"All {completedCount} assets installed successfully");
                    LogBatchTimingSummary();
                }
            }
            
            // Install status is recorded in the per-project manifest file (SyntyInstallManifest),
            // not in the global SyntyImportTracker (EditorPrefs), which leaked across projects.
            // The manifest write happens in the import-completion flow; nothing to record here.
            
            // Detect new folders created by the import
            // (For packages that don't trigger domain reload - if domain reloads, InitializeOnLoad handles this)
            SyntyInstallManifest.CompletePendingFolderDetectionPublic();
            
            // Reset ALL flags
            ResetAllFlags();
            
            // Save state before processing next (in case next import causes domain reload)
            SaveQueueToSession();
            
            OnItemCompleted?.Invoke(item);
            OnItemStatusChanged?.Invoke(item);
            OnQueueChanged?.Invoke();
            
            // Process the next item only once Unity is genuinely idle. A package that contains
            // scripts triggers a recompile, but Unity DEFERS it — isCompiling is usually still
            // false the instant the import finishes. If we started the next item immediately, the
            // deferred recompile + domain reload would interrupt it mid-download. So we wait a few
            // frames: if a compile starts in that window we hand off to the domain-reload resume
            // path; otherwise (no scripts) we advance to the next item.
            WaitForCompileToSettleThenProcessNext();
        }
        
        private double _settleStartTime = 0;
        private bool _settleSawCompile = false;
        // Advances to the next queued item after a short pause, so installs don't run back-to-back
        // with no breathing room (gives Unity a moment to settle and the UI a chance to update).
        private void ProcessQueueAfterDelay(double seconds)
        {
            double startAt = EditorApplication.timeSinceStartup;
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (EditorApplication.timeSinceStartup - startAt < seconds) return;
                EditorApplication.update -= tick;
                ProcessQueue();
            };
            EditorApplication.update += tick;
        }

        private void WaitForCompileToSettleThenProcessNext()
        {
            // If the tool has locked assembly reloading for the batch, no compile/reload can run
            // until it unlocks at the end. Waiting for one here would hang the queue forever, so
            // advance after a short pause between items.
            if (AssembliesLockedForBatch)
            {
                ProcessQueueAfterDelay(1.0);
                return;
            }
            
            _settleStartTime = EditorApplication.timeSinceStartup;
            _settleSawCompile = false;
            
            // Force any pending script compilation to start now (importing a package with scripts
            // queues a deferred recompile; this makes Unity begin it immediately) so we can wait
            // for it to finish instead of racing the next item into a domain reload.
            try { UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(); } catch { }
            
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                bool busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                
                if (busy)
                {
                    // A compile/reload is running. Persist the queue so it resumes after the
                    // domain reload, and keep waiting (if no reload happens, we advance once idle).
                    _settleSawCompile = true;
                    SaveQueueToSession();
                    return;
                }
                
                double elapsed = EditorApplication.timeSinceStartup - _settleStartTime;
                
                // If we saw a compile and it's now finished (no reload happened), advance.
                if (_settleSawCompile)
                {
                    EditorApplication.update -= tick;
                    ProcessQueueAfterDelay(1.0);
                    return;
                }
                
                // No compile observed yet. Give Unity up to ~1.5s to actually begin the deferred
                // recompile before concluding the package had no scripts and moving on.
                if (elapsed >= 1.5)
                {
                    EditorApplication.update -= tick;
                    ProcessQueueAfterDelay(1.0);
                }
            };
            EditorApplication.update += tick;
        }
        
        private void CancelImport(DownloadItem item)
        {
            if (item == null) return;
            
            
            item.status = DownloadStatus.Cancelled;
            item.completedAt = DateTime.Now;
            
            // Reset ALL flags
            ResetAllFlags();
            
            // Save state before processing next
            SaveQueueToSession();
            
            OnItemStatusChanged?.Invoke(item);
            OnQueueChanged?.Invoke();
            
            
            // Process next item in queue after a delay
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling)
                {
                    SaveQueueToSession();
                    // Domain reload will happen, static constructor will resume
                }
                else
                {
                    ProcessQueue();
                }
            };
        }
        
        #endregion
        
        #region Import Event Handlers
        
        /// <summary>
        /// Fallback check for import completion.
        /// With silent (non-interactive) imports, the importPackageCompleted event
        /// should fire, but this is a safety net in case it doesn't.
        /// </summary>
        private void CheckImportStatus()
        {
            if (!_isWaitingForImport || _currentImportingItem == null)
                return;
            
            float timeSinceImportStart = (float)EditorApplication.timeSinceStartup - _importStartTime;
            
            // The authoritative signal is the importPackageCompleted event (OnImportPackageCompleted
            // below) — this is only a safety net for the rare case it doesn't fire. Use generous
            // timeouts so the real event almost always wins and we don't mark an import "done"
            // before it actually is. Don't time out at all while Unity is still importing/compiling.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _importStartTime = (float)EditorApplication.timeSinceStartup; // keep pushing the deadline while busy
                return;
            }
            float timeout = _currentImportingItem.isCacheHit ? 8f : 60f;
            if (timeSinceImportStart > timeout)
            {
                SuppressImportLogging(false);
                CompleteImport(_currentImportingItem);
            }
        }
        
        private void OnImportPackageCompleted(string packageName)
        {
            SuppressImportLogging(false); // restore before we log our own completion messages
            if (_currentImportingItem != null)
            {
                CompleteImport(_currentImportingItem);
            }
        }
        
        private void OnImportPackageCancelled(string packageName)
        {
            SuppressImportLogging(false);
            if (_currentImportingItem != null)
            {
                CancelImport(_currentImportingItem);
            }
        }

        // Fires when Unity can't import a package — most commonly "Couldn't decompress package" from a
        // corrupt or truncated file. Clean up so the run recovers: delete the bad cache file (a re-run
        // re-downloads a fresh copy), drop the pre-recorded manifest entry (nothing actually imported,
        // so it must not read as installed), mark the item Failed, and advance to the next pack so the
        // batch doesn't stall.
        private void OnImportPackageFailed(string packageName, string errorMessage)
        {
            SuppressImportLogging(false);
            Synty.Tools.SyntyLog.Error($"Package import failed: {errorMessage}");
            var item = _currentImportingItem; // capture before ResetAllFlags clears it
            if (item == null) return;
            try
            {
                if (!string.IsNullOrEmpty(item.downloadedFilePath) && File.Exists(item.downloadedFilePath))
                    File.Delete(item.downloadedFilePath);
            }
            catch { }
            try { SyntyInstallManifest.RemoveEntry(item.productHandle); } catch { }
            item.status = DownloadStatus.Failed;
            item.errorMessage = string.IsNullOrEmpty(errorMessage) ? "Import failed" : errorMessage;
            ResetAllFlags();
            SaveQueueToSession();
            OnItemStatusChanged?.Invoke(item);
            OnItemFailed?.Invoke(item);
            OnQueueChanged?.Invoke();
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isCompiling) SaveQueueToSession();
                else ProcessQueue();
            };
        }
        
        #endregion
        
        #region Event Handlers
        
        private void HandleDownloadProgress(string productHandle, float progress)
        {
            if (_activeDownloads.TryGetValue(productHandle, out var item))
            {
                item.progress = progress;
                OnItemProgressChanged?.Invoke(item);
            }
        }
        
        private void HandleDownloadComplete(string productHandle)
        {
            // Handled in StartDownload
        }
        
        private void HandleDownloadFailed(string productHandle, string error)
        {
            // Handled in StartDownload
        }
        
        #endregion
        
        #region Queries
        
        /// <summary>
        /// Check if an item is downloading
        /// </summary>
        public bool IsDownloading(string productHandle)
        {
            return _activeDownloads.ContainsKey(productHandle);
        }
        
        /// <summary>
        /// Check if an item is in queue
        /// </summary>
        public bool IsInQueue(string productHandle)
        {
            return Queue.Exists(d => d.productHandle == productHandle);
        }
        
        /// <summary>
        /// Get download progress for a handle
        /// </summary>
        public float GetProgress(string productHandle)
        {
            var item = Queue.Find(d => d.productHandle == productHandle);
            return item?.progress ?? 0f;
        }
        
        /// <summary>
        /// Get total progress across all downloads (0-1)
        /// </summary>
        public float GetTotalProgress()
        {
            if (Queue.Count == 0) return 0f;
            
            float total = 0f;
            int count = 0;
            
            foreach (var item in Queue)
            {
                if (item.status == DownloadStatus.Completed)
                {
                    total += 1f;
                }
                else if (item.status == DownloadStatus.Downloading || item.status == DownloadStatus.Importing)
                {
                    total += item.progress;
                }
                // Queued items contribute 0
                count++;
            }
            
            return count > 0 ? total / count : 0f;
        }
        
        /// <summary>
        /// Cancel all active and queued downloads
        /// </summary>
        // Removes all items from the queue (does not touch cached files on disk). Used when the
        // user cancels after the download-only phase — downloads stay cached for a fast re-run.
        public void ClearQueue()
        {
            Queue.Clear();
            _activeDownloads.Clear();
            ClearBatchResumeDisk();
            RestoreParallelImportAfterBatch();
            SuppressImportLogging(false);
            OnQueueChanged?.Invoke();
        }

        public void CancelAll()
        {
            // Cancel active downloads
            foreach (var kvp in new Dictionary<string, DownloadItem>(_activeDownloads))
            {
                CancelDownload(kvp.Value);
            }
            
            // Clear queued items
            Queue.RemoveAll(d => d.status == DownloadStatus.Queued);
            
            // Note: Cannot programmatically cancel the Unity import dialog
            // User must manually close it
            
            OnQueueChanged?.Invoke();
        }
        
        #endregion
    }
}
