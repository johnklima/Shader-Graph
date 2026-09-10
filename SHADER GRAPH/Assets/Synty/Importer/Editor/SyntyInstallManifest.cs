using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Synty.Tools
{
    /// <summary>
    /// Tracks which files each asset pack installed, enabling safe uninstall.
    /// Shared files (used by multiple packs) are only deleted when no pack needs them.
    /// Only files installed by this tool are tracked/deleted.
    /// 
    /// Manifest is stored at Assets/Synty/Importer/install_manifest.json
    /// </summary>
    [InitializeOnLoad]
    public static class SyntyInstallManifest
    {
        private const string MANIFEST_PATH = "Assets/Synty/Importer/install_manifest.json";
        
        [Serializable]
        public class PackEntry
        {
            public string handle;
            public string displayName;
            public string filename;
            public string installedVersion;
            public string installedDate;
            public List<string> files = new List<string>();
            public List<string> installedFolders = new List<string>();
        }
        
        [Serializable]
        private class ManifestData
        {
            public List<PackEntry> packs = new List<PackEntry>();
        }
        
        private static ManifestData _manifest;
        
        // Transient file used to survive domain reloads mid-import. Kept OUTSIDE the Assets folder
        // (in the project's Library folder) so Unity never creates a .meta for it — a .meta left
        // behind after this file is deleted was causing "meta file exists but asset can't be found"
        // warnings. Library persists across domain reloads but isn't scanned by the asset pipeline.
        private static string PENDING_IMPORT_PATH
        {
            get
            {
                // Application.dataPath = <project>/Assets → go up one to the project root, then Library.
                string projectRoot = System.IO.Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
                return Path.Combine(projectRoot, "Library", "SyntyPassport_pending_import.json");
            }
        }
        private const string PENDING_IMPORT_PATH_LEGACY = "Assets/Synty/Passport/_pending_import.json";
        
        [Serializable]
        private class PendingImport
        {
            public string handle;
            public List<string> preFolders = new List<string>();
        }
        
        static SyntyInstallManifest()
        {
            Load();
            CompletePendingFolderDetection();
        }
        
        // ===== PUBLIC API =====
        
        /// <summary>
        /// Extract the file list from a .unitypackage and record it in the manifest.
        /// Call this BEFORE or AFTER importing the package.
        /// </summary>
        public static void RecordInstall(string handle, string displayName, string unitypackagePath, string version = null)
        {
            if (string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(unitypackagePath))
                return;
            
            if (!File.Exists(unitypackagePath))
            {
                return;
            }
            
            // Extract file paths from the .unitypackage
            var files = ExtractFileListFromPackage(unitypackagePath);
            
            if (files.Count == 0)
            {
            }
            
            // Remove existing entry for this handle (re-install case)
            _manifest.packs.RemoveAll(p => p.handle == handle);
            
            // Add new entry
            var entry = new PackEntry
            {
                handle = handle,
                displayName = displayName ?? handle,
                filename = Path.GetFileName(unitypackagePath),
                installedVersion = version ?? "",
                installedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                files = files
            };
            
            _manifest.packs.Add(entry);
            Save();
            
        }
        
        /// <summary>
        /// Get the list of files that are safe to delete for a pack.
        /// Files shared with other installed packs are excluded.
        /// </summary>
        public static List<string> GetSafeToDeleteFiles(string handle)
        {
            var entry = _manifest.packs.Find(p => p.handle == handle);
            if (entry == null || entry.files == null) return new List<string>();
            
            // Build reference count: how many OTHER packs use each file
            var otherPackFiles = new HashSet<string>();
            foreach (var pack in _manifest.packs)
            {
                if (pack.handle == handle) continue;
                if (pack.files == null) continue;
                foreach (var f in pack.files)
                    otherPackFiles.Add(f);
            }
            
            // Only include files NOT used by other packs
            return entry.files.Where(f => !otherPackFiles.Contains(f)).ToList();
        }
        
        /// <summary>
        /// Get all files for a pack (including shared ones).
        /// </summary>
        public static List<string> GetPackFiles(string handle)
        {
            var entry = _manifest.packs.Find(p => p.handle == handle);
            return entry?.files ?? new List<string>();
        }
        
        /// <summary>
        /// Get files that would be kept (shared with other packs).
        /// </summary>
        public static List<string> GetSharedFiles(string handle)
        {
            var safeToDelete = new HashSet<string>(GetSafeToDeleteFiles(handle));
            var allFiles = GetPackFiles(handle);
            return allFiles.Where(f => !safeToDelete.Contains(f)).ToList();
        }
        
        /// <summary>
        /// Perform the actual uninstall: delete safe files, remove empty folders, clear manifest entry.
        /// Returns (deletedCount, skippedSharedCount, totalFiles).
        /// </summary>
        // Deletes the given folder and its .meta if it's empty, then walks up doing the same for
        // parents, stopping at Assets or the Synty root. Used to tidy up after file-based uninstall.
        private static void RemoveEmptyFoldersUpward(string dir)
        {
            try
            {
                string cur = dir?.Replace("\\", "/");
                while (!string.IsNullOrEmpty(cur)
                       && cur.StartsWith("Assets/")
                       && cur != "Assets"
                       && !cur.Equals("Assets/Synty", StringComparison.OrdinalIgnoreCase)
                       && Directory.Exists(cur))
                {
                    // Empty = no files and no subdirectories (ignore .meta of the folder itself).
                    bool empty = Directory.GetFileSystemEntries(cur).Length == 0;
                    if (!empty) break;
                    Directory.Delete(cur);
                    string meta = cur + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    int slash = cur.LastIndexOf('/');
                    cur = slash > 0 ? cur.Substring(0, slash) : null;
                }
            }
            catch { }
        }

        public static (int deleted, int shared, int total) UninstallPack(string handle)
        {
            var entry = _manifest.packs.Find(p => p.handle == handle);
            if (entry == null)
            {
                Debug.LogError($"[InstallManifest] UninstallPack: no entry found for '{handle}'");
                return (0, 0, 0);
            }
            
            int total = entry.files?.Count ?? 0;
            int deleted = 0;

            // Primary: delete the individual files recorded from the .unitypackage (excluding files
            // shared with other installed packs). This works even when folder detection failed at
            // install time (which left installedFolders empty and previously caused packs to not be
            // removed on uninstall — seen on Unity 6).
            var safeFiles = GetSafeToDeleteFiles(handle);
            var touchedDirs = new HashSet<string>();
            foreach (var rel in safeFiles)
            {
                try
                {
                    string path = rel.Replace("\\", "/");
                    if (File.Exists(path))
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        string meta = path + ".meta";
                        if (File.Exists(meta)) File.Delete(meta);
                        deleted++;
                        string d = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(d)) touchedDirs.Add(d.Replace("\\", "/"));
                    }
                }
                catch (Exception e) { Debug.LogWarning($"[InstallManifest] Could not delete '{rel}': {e.Message}"); }
            }
            // Remove any now-empty directories left behind (deepest first).
            foreach (var dir in touchedDirs.OrderByDescending(d => d.Length))
                RemoveEmptyFoldersUpward(dir);

            if (entry.installedFolders != null && entry.installedFolders.Count > 0)
            {
                foreach (var folder in entry.installedFolders)
                {
                    
                    // Try relative path first
                    if (Directory.Exists(folder))
                    {
                        try
                        {
                            Directory.Delete(folder, true);
                            bool stillExists = Directory.Exists(folder);
                            
                            if (stillExists)
                            {
                                // Force: delete all files first, then directory
                                foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
                                {
                                    try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch { }
                                }
                                Directory.Delete(folder, true);
                            }
                            
                            if (!Directory.Exists(folder))
                            {
                                string metaPath = folder + ".meta";
                                if (File.Exists(metaPath)) File.Delete(metaPath);
                                deleted++;
                            }
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[InstallManifest] Delete failed for '{folder}': {e.GetType().Name}: {e.Message}");
                        }
                    }
                    
                    // Try absolute path
                    string absPath = Path.GetFullPath(folder);
                    if (absPath != folder && Directory.Exists(absPath))
                    {
                        try
                        {
                            Directory.Delete(absPath, true);
                            string absMeta = absPath + ".meta";
                            if (File.Exists(absMeta)) File.Delete(absMeta);
                            if (!Directory.Exists(absPath)) deleted++;
                        }
                        catch (Exception e)
                        {
                            Debug.LogError($"[InstallManifest] Absolute delete failed: {e.Message}");
                        }
                    }
                }
            }
            else
            {
            }
            
            // Remove from manifest
            _manifest.packs.RemoveAll(p => p.handle == handle);
            Save();

            // Let Unity notice the deleted files/folders.
            try { AssetDatabase.Refresh(); } catch { }

            int shared = total - deleted;
            if (shared < 0) shared = 0;
            return (deleted, shared, total);
        }
        
        /// <summary>
        /// Clear all manifest data.
        /// </summary>
        public static void ClearAll()
        {
            _manifest = new ManifestData();
            Save();
        }
        
        /// <summary>
        /// Check if a pack has a manifest entry (i.e. is installed).
        /// </summary>
        public static bool HasManifest(string handle)
        {
            return _manifest.packs.Any(p => p.handle == handle);
        }
        
        /// <summary>
        /// Check if an update is available (different filename than what was installed).
        /// </summary>
        public static bool HasUpdate(string handle, string currentBucketFilename, string catalogVersion = null)
        {
            if (string.IsNullOrEmpty(handle))
                return false;
            
            var entry = _manifest.packs.Find(p => p.handle == handle);
            if (entry == null) return false;
            
            // Version-based comparison (preferred)
            if (!string.IsNullOrEmpty(catalogVersion) && !string.IsNullOrEmpty(entry.installedVersion))
            {
                return CompareVersions(catalogVersion, entry.installedVersion) > 0;
            }
            
            // Fallback to filename comparison
            if (string.IsNullOrEmpty(currentBucketFilename))
                return false;
            
            return !string.IsNullOrEmpty(entry.filename) &&
                   !entry.filename.Equals(currentBucketFilename, StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Compare two version strings (e.g. "1.0.6" vs "1.0.5"). Returns &gt;0 if a is newer.
        /// </summary>
        private static int CompareVersions(string a, string b)
        {
            var aParts = a.Split('.');
            var bParts = b.Split('.');
            int len = System.Math.Max(aParts.Length, bParts.Length);
            for (int i = 0; i < len; i++)
            {
                int av = i < aParts.Length ? (int.TryParse(aParts[i], out int ai) ? ai : 0) : 0;
                int bv = i < bParts.Length ? (int.TryParse(bParts[i], out int bi) ? bi : 0) : 0;
                if (av != bv) return av - bv;
            }
            return 0;
        }
        
        /// <summary>
        /// Remove a manifest entry without deleting any files.
        /// </summary>
        public static void RemoveEntry(string handle)
        {
            _manifest.packs.RemoveAll(p => p.handle == handle);
            Save();
        }
        
        /// <summary>
        /// Get the manifest entry for a pack.
        /// </summary>
        public static PackEntry GetEntry(string handle)
        {
            return _manifest.packs.Find(p => p.handle == handle);
        }

        /// <summary>
        /// Records a baseline installed version for an asset that is installed but has no version
        /// recorded yet (e.g. installed before version tracking existed). Only fills an EMPTY
        /// version — never overwrites a real one — so genuine future updates are still detected.
        /// Returns true if it stamped (and saved) a value.
        /// </summary>
        public static bool StampInstalledVersionIfMissing(string handle, string version)
        {
            if (string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(version)) return false;
            var entry = _manifest.packs.Find(p => p.handle == handle);
            if (entry == null) return false;                       // not installed — nothing to stamp
            if (!string.IsNullOrEmpty(entry.installedVersion)) return false; // already has a version
            entry.installedVersion = version;
            Save();
            return true;
        }
        
        /// <summary>
        /// Get all pack entries.
        /// </summary>
        public static List<PackEntry> GetAllEntries()
        {
            return _manifest.packs;
        }
        
        /// <summary>
        /// Get all top-level folders under Assets/ (for before/after import comparison).
        /// </summary>
        public static HashSet<string> GetTopLevelAssetFolders()
        {
            var folders = new HashSet<string>();
            
            if (!Directory.Exists("Assets")) return folders;
            
            // Scan ALL directories recursively under Assets
            try
            {
                foreach (var dir in Directory.GetDirectories("Assets", "*", SearchOption.AllDirectories))
                {
                    string clean = dir.Replace("\\", "/");
                    // Skip our own tool folder
                    if (clean.StartsWith("Assets/Synty/Importer")) continue;
                    folders.Add(clean);
                }
            }
            catch { }
            
            return folders;
        }
        
        /// <summary>
        /// Update the installed folders for a pack (detected after import).
        /// </summary>
        public static void UpdateInstalledFolders(string handle, List<string> folders)
        {
            var entry = _manifest.packs.Find(p => p.handle == handle);
            if (entry == null) return;
            
            entry.installedFolders = folders ?? new List<string>();
            Save();
        }
        
        // ===== PENDING IMPORT (survives domain reload) =====
        
        /// <summary>
        /// Save a folder snapshot before importing a package. After domain reload,
        /// CompletePendingFolderDetection will diff against current folders.
        /// </summary>
        public static void SavePendingImport(string handle)
        {
            try
            {
                var folders = GetTopLevelAssetFolders();
                var pending = new PendingImport
                {
                    handle = handle,
                    preFolders = new List<string>(folders)
                };
                string dir = Path.GetDirectoryName(PENDING_IMPORT_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(PENDING_IMPORT_PATH, JsonUtility.ToJson(pending, true));
            }
            catch (Exception)
            {
            }
        }
        
        /// <summary>
        /// Called from [InitializeOnLoad] after domain reload. Detects new folders from import.
        /// </summary>
        private static void CompletePendingFolderDetection()
        {
            CompletePendingFolderDetectionPublic();
        }
        
        /// <summary>
        /// Public version — also called from SyntyDownloadManager.CompleteImport for non-reload imports.
        /// </summary>
        public static void CompletePendingFolderDetectionPublic()
        {
            // Prefer the new (Library) path; fall back to the legacy in-Assets path if an import was
            // in progress across an upgrade.
            string activePath = File.Exists(PENDING_IMPORT_PATH) ? PENDING_IMPORT_PATH
                              : (File.Exists(PENDING_IMPORT_PATH_LEGACY) ? PENDING_IMPORT_PATH_LEGACY : null);
            if (activePath == null) return;
            
            try
            {
                string json = File.ReadAllText(activePath);
                var pending = JsonUtility.FromJson<PendingImport>(json);
                
                if (pending != null && !string.IsNullOrEmpty(pending.handle))
                {
                    var currentFolders = GetTopLevelAssetFolders();
                    var preFolders = new HashSet<string>(pending.preFolders);
                    var allNewFolders = new List<string>();
                    
                    foreach (var folder in currentFolders)
                    {
                        if (!preFolders.Contains(folder))
                            allNewFolders.Add(folder);
                    }
                    
                    // Filter to root-level new folders only
                    // e.g. keep "Assets/Synty/PolygonAdventure" but not its children
                    allNewFolders.Sort(); // shortest paths first
                    var rootFolders = new List<string>();
                    foreach (var folder in allNewFolders)
                    {
                        bool isChild = false;
                        foreach (var root in rootFolders)
                        {
                            if (folder.StartsWith(root + "/"))
                            {
                                isChild = true;
                                break;
                            }
                        }
                        if (!isChild) rootFolders.Add(folder);
                    }
                    
                    if (rootFolders.Count > 0)
                    {
                        UpdateInstalledFolders(pending.handle, rootFolders);
                    }
                    else
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try { File.Delete(PENDING_IMPORT_PATH); } catch { }
                // Clean up the old in-Assets location (and its .meta) from prior versions so it
                // stops producing orphaned-.meta warnings.
                try { if (File.Exists(PENDING_IMPORT_PATH_LEGACY)) File.Delete(PENDING_IMPORT_PATH_LEGACY); } catch { }
                try { if (File.Exists(PENDING_IMPORT_PATH_LEGACY + ".meta")) File.Delete(PENDING_IMPORT_PATH_LEGACY + ".meta"); } catch { }
            }
        }
        
        // ===== PACKAGE FILE EXTRACTION =====
        
        /// <summary>
        /// Reads a .unitypackage (tar.gz) and extracts the list of asset paths it contains.
        /// </summary>
        private static List<string> ExtractFileListFromPackage(string packagePath)
        {
            var paths = new List<string>();
            
            try
            {
                using (var fileStream = File.OpenRead(packagePath))
                using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
                {
                    // Read tar entries
                    byte[] buffer = new byte[512];
                    
                    while (true)
                    {
                        // Read tar header (512 bytes)
                        int bytesRead = ReadFull(gzipStream, buffer, 0, 512);
                        if (bytesRead < 512) break;
                        
                        // Check for empty block (end of archive)
                        bool allZero = true;
                        for (int i = 0; i < 512; i++)
                        {
                            if (buffer[i] != 0) { allZero = false; break; }
                        }
                        if (allZero) break;
                        
                        // Parse filename from header (first 100 bytes, null-terminated)
                        string name = System.Text.Encoding.ASCII.GetString(buffer, 0, 100).TrimEnd('\0', ' ');
                        
                        // Parse file size from header (bytes 124-135, octal)
                        string sizeStr = System.Text.Encoding.ASCII.GetString(buffer, 124, 12).TrimEnd('\0', ' ');
                        long size = 0;
                        if (!string.IsNullOrEmpty(sizeStr))
                        {
                            try { size = Convert.ToInt64(sizeStr, 8); } catch { }
                        }
                        
                        // Check if this is a "pathname" file (contains the asset path)
                        if (name.EndsWith("/pathname"))
                        {
                            // Read the file content (the asset path)
                            if (size > 0 && size < 4096)
                            {
                                byte[] content = new byte[size];
                                ReadFull(gzipStream, content, 0, (int)size);
                                string assetPath = System.Text.Encoding.UTF8.GetString(content).Trim();
                                
                                if (!string.IsNullOrEmpty(assetPath) && assetPath.StartsWith("Assets/"))
                                {
                                    paths.Add(assetPath);
                                }
                                
                                // Skip padding to 512-byte boundary
                                int remainder = (int)(size % 512);
                                if (remainder > 0)
                                {
                                    byte[] skip = new byte[512 - remainder];
                                    ReadFull(gzipStream, skip, 0, skip.Length);
                                }
                            }
                        }
                        else
                        {
                            // Skip file content + padding
                            if (size > 0)
                            {
                                long toSkip = size;
                                int remainder = (int)(size % 512);
                                if (remainder > 0) toSkip += (512 - remainder);
                                
                                byte[] skipBuf = new byte[4096];
                                while (toSkip > 0)
                                {
                                    int chunk = (int)Math.Min(toSkip, skipBuf.Length);
                                    int read = ReadFull(gzipStream, skipBuf, 0, chunk);
                                    if (read == 0) break;
                                    toSkip -= read;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            
            return paths;
        }
        
        /// <summary>
        /// Read exactly 'count' bytes from a stream (handles partial reads).
        /// </summary>
        private static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = stream.Read(buffer, offset + totalRead, count - totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
        }
        
        // ===== FOLDER CLEANUP =====
        
        private static void TryDeleteEmptyFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            
            // Don't delete root Synty folders
            if (folder == "Assets" || folder == "Assets/Synty" || folder == "Assets/Synty/Importer") return;
            
            try
            {
                var files = Directory.GetFiles(folder);
                var dirs = Directory.GetDirectories(folder);
                
                // Only .meta files remaining = effectively empty
                bool onlyMeta = files.All(f => f.EndsWith(".meta"));
                
                if (dirs.Length == 0 && (files.Length == 0 || onlyMeta))
                {
                    // Delete meta files first
                    foreach (var f in files) File.Delete(f);
                    
                    Directory.Delete(folder);
                    
                    // Delete folder's .meta file
                    string folderMeta = folder + ".meta";
                    if (File.Exists(folderMeta)) File.Delete(folderMeta);
                    
                    // Try parent too
                    string parent = Path.GetDirectoryName(folder)?.Replace("\\", "/");
                    if (!string.IsNullOrEmpty(parent))
                        TryDeleteEmptyFolder(parent);
                }
            }
            catch { }
        }
        
        // ===== PERSISTENCE =====
        
        private static void Load()
        {
            _manifest = new ManifestData();
            
            if (File.Exists(MANIFEST_PATH))
            {
                try
                {
                    string json = File.ReadAllText(MANIFEST_PATH);
                    _manifest = JsonUtility.FromJson<ManifestData>(json) ?? new ManifestData();
                }
                catch (Exception)
                {
                    _manifest = new ManifestData();
                }
            }
            else
            {
            }
        }
        
        private static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(MANIFEST_PATH);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                
                string json = JsonUtility.ToJson(_manifest, true);
                File.WriteAllText(MANIFEST_PATH, json);
            }
            catch (Exception)
            {
            }
        }
    }
}
