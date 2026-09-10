#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace Synty.Tools
{
    /// <summary>
    /// Tracks which Synty assets have been imported into the project,
    /// including the filename/version that was imported.
    /// Persists data using EditorPrefs.
    /// </summary>
    [InitializeOnLoad]
    public static class SyntyImportTracker
    {
        private const string PREFS_KEY = "SyntyImportTracker_ImportedAssets";
        
        // In-memory cache of imported assets
        // Key: product handle, Value: imported filename (contains version info)
        private static Dictionary<string, ImportedAssetInfo> _importedAssets;
        
        [Serializable]
        public class ImportedAssetInfo
        {
            public string productHandle;
            public string importedFilename;
            public string importedDate;
            public string productTitle;
        }
        
        [Serializable]
        private class SerializedData
        {
            public List<ImportedAssetInfo> assets = new List<ImportedAssetInfo>();
        }
        
        static SyntyImportTracker()
        {
            LoadFromPrefs();
        }
        
        /// <summary>
        /// Record that a package was imported
        /// </summary>
        public static void RecordImport(string productHandle, string filename, string productTitle = null)
        {
            if (string.IsNullOrEmpty(productHandle)) return;
            
            if (_importedAssets == null)
                _importedAssets = new Dictionary<string, ImportedAssetInfo>();
            
            _importedAssets[productHandle] = new ImportedAssetInfo
            {
                productHandle = productHandle,
                importedFilename = filename ?? "",
                importedDate = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                productTitle = productTitle ?? ""
            };
            
            SaveToPrefs();
            
            // Debug.Log($"[SyntyImportTracker] Recorded import: {productHandle} -> {filename}");
        }
        
        /// <summary>
        /// Check if a package has been imported
        /// </summary>
        public static bool IsImported(string productHandle)
        {
            if (string.IsNullOrEmpty(productHandle) || _importedAssets == null)
                return false;
            
            return _importedAssets.ContainsKey(productHandle);
        }
        
        /// <summary>
        /// Get the filename that was imported for a package
        /// </summary>
        public static string GetImportedFilename(string productHandle)
        {
            if (string.IsNullOrEmpty(productHandle) || _importedAssets == null)
                return null;
            
            if (_importedAssets.TryGetValue(productHandle, out var info))
                return info.importedFilename;
            
            return null;
        }
        
        /// <summary>
        /// Get full import info for a package
        /// </summary>
        public static ImportedAssetInfo GetImportInfo(string productHandle)
        {
            if (string.IsNullOrEmpty(productHandle) || _importedAssets == null)
                return null;
            
            _importedAssets.TryGetValue(productHandle, out var info);
            return info;
        }
        
        /// <summary>
        /// Check if an update is available (different filename in bucket than what was imported)
        /// </summary>
        public static bool HasUpdate(string productHandle, string currentBucketFilename)
        {
            if (string.IsNullOrEmpty(productHandle) || string.IsNullOrEmpty(currentBucketFilename))
                return false;
            
            if (_importedAssets == null || !_importedAssets.ContainsKey(productHandle))
                return false;
            
            string importedFilename = _importedAssets[productHandle].importedFilename;
            
            // If filenames are different, an update is available
            return !string.IsNullOrEmpty(importedFilename) && 
                   !importedFilename.Equals(currentBucketFilename, StringComparison.OrdinalIgnoreCase);
        }
        
        /// <summary>
        /// Get import status for display
        /// </summary>
        public static ImportStatus GetStatus(string productHandle, string currentBucketFilename)
        {
            if (string.IsNullOrEmpty(productHandle))
                return ImportStatus.NotImported;
            
            if (_importedAssets == null || !_importedAssets.ContainsKey(productHandle))
                return ImportStatus.NotImported;
            
            if (HasUpdate(productHandle, currentBucketFilename))
                return ImportStatus.UpdateAvailable;
            
            return ImportStatus.InProject;
        }
        
        /// <summary>
        /// Remove import record (e.g., if user wants to re-import)
        /// </summary>
        public static void ClearImport(string productHandle)
        {
            if (string.IsNullOrEmpty(productHandle) || _importedAssets == null)
                return;
            
            if (_importedAssets.Remove(productHandle))
            {
                SaveToPrefs();
                // Debug.Log($"[SyntyImportTracker] Cleared import record for: {productHandle}");
            }
        }
        
        /// <summary>
        /// Clear all import records
        /// </summary>
        public static void ClearAll()
        {
            _importedAssets = new Dictionary<string, ImportedAssetInfo>();
            SaveToPrefs();
            // Debug.Log("[SyntyImportTracker] Cleared all import records");
        }
        
        /// <summary>
        /// Get all imported assets
        /// </summary>
        public static Dictionary<string, ImportedAssetInfo> GetAllImported()
        {
            if (_importedAssets == null)
                _importedAssets = new Dictionary<string, ImportedAssetInfo>();
            
            return new Dictionary<string, ImportedAssetInfo>(_importedAssets);
        }
        
        /// <summary>
        /// Get count of imported assets
        /// </summary>
        public static int ImportedCount => _importedAssets?.Count ?? 0;
        
        private static void LoadFromPrefs()
        {
            _importedAssets = new Dictionary<string, ImportedAssetInfo>();
            
            try
            {
                string json = EditorPrefs.GetString(PREFS_KEY, "");
                if (!string.IsNullOrEmpty(json))
                {
                    var data = JsonUtility.FromJson<SerializedData>(json);
                    if (data?.assets != null)
                    {
                        foreach (var asset in data.assets)
                        {
                            if (!string.IsNullOrEmpty(asset.productHandle))
                            {
                                _importedAssets[asset.productHandle] = asset;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyImportTracker] Failed to load: {ex.Message}");
            }
        }
        
        private static void SaveToPrefs()
        {
            try
            {
                var data = new SerializedData();
                if (_importedAssets != null)
                {
                    foreach (var kvp in _importedAssets)
                    {
                        data.assets.Add(kvp.Value);
                    }
                }
                
                string json = JsonUtility.ToJson(data);
                EditorPrefs.SetString(PREFS_KEY, json);
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyImportTracker] Failed to save: {ex.Message}");
            }
        }
        
        public enum ImportStatus
        {
            NotImported,
            InProject,
            UpdateAvailable
        }
    }
}
#endif
