using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using UnityEngine;
using UnityEditor;

namespace Synty.Tools.V2
{
    /// <summary>
    /// Bridge between the V2 UI tool and the backend services (SyntyStoreHelper, SyntyDownloadManager, SyntyImportTracker).
    /// Keeps all service/data logic out of the editor window code.
    /// </summary>
    public static class SyntyCatalogBridge
    {
        // ─── Constants ───────────────────────────────────────────────────
        
        // Cloudflare R2 base URL for asset icons (flat filenames: icon_{handle}.png)
        private const string ICON_BASE_URL = "https://synty-downloads.syntystore.workers.dev";
        private const string ICON_CACHE_FOLDER = "Assets/Synty/Importer/Editor/Images/AssetIcons";
        
        // ─── Catalog State ───────────────────────────────────────────────
        
        private static List<AssetPackData> _loadedPacks;
        private static bool _isLoading;
        private static string _lastError;
        
        public static bool IsLoading => _isLoading;
        public static bool IsLoaded => _loadedPacks != null && _loadedPacks.Count > 0;
        public static string LastError => _lastError;
        public static List<AssetPackData> LoadedPacks => _loadedPacks;
        
        /// <summary>
        /// Clear all cached catalog data (memory + disk) to force a fresh fetch.
        /// </summary>
        public static void ClearCache()
        {
            _loadedPacks = null;
            _packsByHandle.Clear();
            _lastError = null;
            SessionState.SetString(SESSION_PACKS_KEY, "");
            SyntyStoreService.Instance.ClearCatalogCache();
        }
        
        // Maps product handle → AssetPackData for quick lookup
        private static Dictionary<string, AssetPackData> _packsByHandle = new Dictionary<string, AssetPackData>();
        
        // ─── Catalog Loading ─────────────────────────────────────────────
        
        /// <summary>
        /// Try to load catalog from disk cache synchronously (no network).
        /// Returns true if cache was loaded successfully.
        /// </summary>
        private const string SESSION_PACKS_KEY = "SyntyPassport_ConvertedPacks";
        
        [System.Serializable]
        private class PackListWrapper { public List<AssetPackData> packs; }
        
        /// <summary>
        /// Save converted packs to SessionState so domain reloads skip the full JSON parse + conversion.
        /// </summary>
        private static void SavePacksToSession()
        {
            if (_loadedPacks == null || _loadedPacks.Count == 0) return;
            try
            {
                var wrapper = new PackListWrapper { packs = _loadedPacks };
                string json = JsonUtility.ToJson(wrapper);
                SessionState.SetString(SESSION_PACKS_KEY, json);
            }
            catch { }
        }
        
        /// <summary>
        /// Try to restore converted packs from SessionState (survives domain reloads, no disk IO or conversion).
        /// </summary>
        private static bool TryLoadFromSession()
        {
            string json = SessionState.GetString(SESSION_PACKS_KEY, "");
            if (string.IsNullOrEmpty(json)) return false;
            
            try
            {
                var wrapper = JsonUtility.FromJson<PackListWrapper>(json);
                if (wrapper?.packs != null && wrapper.packs.Count > 0)
                {
                    _loadedPacks = wrapper.packs;
                    _packsByHandle.Clear();
                    foreach (var pack in _loadedPacks)
                    {
                        string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                        if (!string.IsNullOrEmpty(handle))
                            _packsByHandle[handle] = pack;
                    }
                    return true;
                }
            }
            catch { }
            return false;
        }
        
        public static bool TryLoadFromDiskCache()
        {
            if (IsLoaded) return true;
            
            // Try SessionState first — no disk IO, no conversion, ~0ms
            if (TryLoadFromSession())
            {
                return true;
            }
            
            // Fall back to disk cache — requires JSON parse + ConvertCatalogToAssetPacks
            string cachePath = System.IO.Path.Combine(Application.temporaryCachePath, "synty_catalog_cache.json");
            if (!System.IO.File.Exists(cachePath)) { return false; }
            
            try
            {
                string json = System.IO.File.ReadAllText(cachePath);
                var catalog = JsonUtility.FromJson<SyntyStoreService.CatalogResponse>(json);
                
                if (catalog != null && catalog.success && catalog.products != null && catalog.products.Length > 0)
                {
                    var packs = SyntyStoreService.Instance.ConvertCatalogToAssetPacks(catalog);
                    if (packs != null && packs.Count > 0)
                    {
                        _loadedPacks = packs;
                        _packsByHandle.Clear();
                        foreach (var pack in _loadedPacks)
                        {
                            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                            if (!string.IsNullOrEmpty(handle))
                                _packsByHandle[handle] = pack;
                        }
                        // Cache converted packs for next domain reload
                        SavePacksToSession();
                        return true;
                    }
                }
            }
            catch (Exception)
            {
            }
            return false;
        }
        
        /// <summary>
        /// Load the product catalog from the backend worker.
        /// Calls onComplete with the list of packs (or empty list on error).
        /// </summary>
        public static void LoadCatalog(Action<List<AssetPackData>> onComplete, bool forceRefresh = false)
        {
            if (_isLoading)
            {
                return;
            }
            
            _isLoading = true;
            _lastError = null;
            
            SyntyStoreHelper.LoadCatalog(
                onSuccess: (packs) =>
                {
                    _loadedPacks = packs ?? new List<AssetPackData>();
                    _packsByHandle.Clear();
                    foreach (var pack in _loadedPacks)
                    {
                        string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                        if (!string.IsNullOrEmpty(handle))
                            _packsByHandle[handle] = pack;
                    }
                    _isLoading = false;
                    SavePacksToSession();
                    onComplete?.Invoke(_loadedPacks);
                },
                onError: (error) =>
                {
                    _lastError = error;
                    _isLoading = false;
                    // Debug.LogError($"[CatalogBridge] Catalog load failed: {error}");
                    onComplete?.Invoke(new List<AssetPackData>());
                },
                forceRefresh: forceRefresh
            );
        }
        
        // ─── Data Mapping ────────────────────────────────────────────────
        
        /// <summary>
        /// Get the V2 asset type string from a catalog pack's category.
        /// </summary>
        public static string GetAssetType(AssetPackData pack)
        {
            switch (pack.category)
            {
                case PackCategory.Polygon:    return "POLYGON";
                case PackCategory.Sidekicks:  return "SIDEKICK";
                case PackCategory.Interface:  return "INTERFACE";
                case PackCategory.Animation:  return "ANIMATION";
                case PackCategory.Simple:     return "SIMPLE";
                default:                      return "POLYGON";
            }
        }
        
        /// <summary>
        /// Get the primary theme string from a catalog pack.
        /// </summary>
        public static string GetTheme(AssetPackData pack)
        {
            if (pack.sciFi)       return "Sci-Fi";
            if (pack.apocalypse)  return "Apocalypse";
            if (pack.horror)      return "Horror";
            if (pack.fantasy)     return "Fantasy";
            if (pack.pirates)     return "Pirates";
            if (pack.samurai)     return "Samurai";
            if (pack.vikings)     return "Vikings";
            if (pack.modern)      return "Modern";
            if (pack.battle)      return "Battle";
            if (pack.western)     return "Western";
            if (pack.biomes)      return "Nature";
            if (pack.ancient)     return "Ancient";
            return "Other";
        }

        /// <summary>
        /// Get ALL themes assigned to a pack (a pack can belong to several). Returns { "Other" } when
        /// none are set. Order matches GetTheme's precedence.
        /// </summary>
        public static System.Collections.Generic.List<string> GetThemes(AssetPackData pack)
        {
            var list = new System.Collections.Generic.List<string>();
            if (pack.sciFi)       list.Add("Sci-Fi");
            if (pack.apocalypse)  list.Add("Apocalypse");
            if (pack.horror)      list.Add("Horror");
            if (pack.fantasy)     list.Add("Fantasy");
            if (pack.pirates)     list.Add("Pirates");
            if (pack.samurai)     list.Add("Samurai");
            if (pack.vikings)     list.Add("Vikings");
            if (pack.modern)      list.Add("Modern");
            if (pack.battle)      list.Add("Battle");
            if (pack.western)     list.Add("Western");
            if (pack.biomes)      list.Add("Nature");
            if (pack.ancient)     list.Add("Ancient");
            if (list.Count == 0)  list.Add("Other");
            return list;
        }
        
        /// <summary>
        /// Get display name for the asset, cleaned for the V2 UI.
        /// Strips "POLYGON - " and similar prefixes.
        /// </summary>
        public static string GetDisplayName(AssetPackData pack)
        {
            string name = pack.GetDisplayName();
            // Strip common prefixes
            string[] prefixes = { "POLYGON - ", "POLYGON ", "Sidekick Characters - ", "Simple - ", "INTERFACE - ", "Animation - " };
            foreach (var prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(prefix.Length);
                    break;
                }
            }
            return name;
        }
        
        /// <summary>
        /// Get the local icon filename for this pack.
        /// Matches the flat R2 filename: icon_{handle}.png
        /// </summary>
        public static string GetIconFilename(AssetPackData pack)
        {
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            return $"icon_{handle}.png";
        }
        
        /// <summary>
        /// Get the R2 download URL for this pack's icon.
        /// Uses pack.iconUrl if set by admin, otherwise constructs from handle.
        /// </summary>
        public static string GetIconUrl(AssetPackData pack)
        {
            // Always use convention-based URL with cache buster
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            long cacheBuster = System.DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300; // Changes every 5 min
            return $"{ICON_BASE_URL}/icon_{handle}.png?v={cacheBuster}";
        }
        
        // ─── Import Status ───────────────────────────────────────────────
        
        /// <summary>
        /// Check if a pack is imported (installed) in the project.
        /// Uses the install manifest as the single source of truth.
        /// Falls back to legacy import tracker for older installs.
        /// </summary>
        public static bool IsInstalled(AssetPackData pack)
        {
            string handle = pack.packName;
            if (string.IsNullOrEmpty(handle))
                handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            
            // Check manifest (primary)
            if (!string.IsNullOrEmpty(handle) && SyntyInstallManifest.HasManifest(handle)) return true;
            
            // Also check with URL-extracted handle (might differ from packName)
            string urlHandle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (!string.IsNullOrEmpty(urlHandle) && urlHandle != handle && SyntyInstallManifest.HasManifest(urlHandle)) return true;
            
            // Install status comes ONLY from the per-project manifest file
            // (Assets/Synty/Importer/install_manifest.json). The old SyntyImportTracker fallback
            // read from global EditorPrefs, which leaked install status across projects, so it is
            // no longer consulted here.
            
            return false;
        }
        
        /// <summary>
        /// Check if a pack has an update available.
        /// </summary>
        public static bool HasUpdate(AssetPackData pack)
        {
            string handle = pack.packName;
            if (string.IsNullOrEmpty(handle))
                handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            
            return SyntyInstallManifest.HasUpdate(handle, pack.bucketFilename, pack.version);
        }
        
        /// <summary>
        /// Get owned state for a pack.
        /// With Synty Pass: all packs are owned.
        /// Without Synty Pass: check if individually purchased.
        /// </summary>
        public static bool IsOwned(AssetPackData pack)
        {
            if (!SyntyLoginManager.IsLoggedIn) return false;
            if (SyntyLoginManager.HasSyntyPass) return true;
            
            // Check individual ownership from order history
            return SyntyStoreService.Instance.OwnedProductHandles.Contains(pack.packName);
        }
        
        // ─── Download Actions ────────────────────────────────────────────
        
        /// <summary>
        /// Start downloading a pack via the download manager.
        /// </summary>
        public static void StartDownload(AssetPackData pack)
        {
            SyntyStoreHelper.StartDownload(pack);
        }
        
        /// <summary>
        /// Start downloading multiple packs.
        /// </summary>
        public static void StartDownloads(List<AssetPackData> packs)
        {
            SyntyDownloadManager.Instance.AddToQueueAndStart(packs);
        }
        
        /// <summary>
        /// Get download progress for a pack (0-1).
        /// </summary>
        public static float GetDownloadProgress(AssetPackData pack)
        {
            return SyntyStoreHelper.GetDownloadProgress(pack);
        }
        
        /// <summary>
        /// Check if a pack is currently downloading.
        /// </summary>
        public static bool IsDownloading(AssetPackData pack)
        {
            return SyntyStoreHelper.IsDownloading(pack);
        }
        
        // ─── Cart Actions ────────────────────────────────────────────────
        
        /// <summary>
        /// Add a single pack to cart (opens browser).
        /// </summary>
        // Single affiliate tracking suffix appended to every Synty store link. This affiliate system
        // (BixGrow) tracks via the bg_ref query param on the real store/product URL, plus the tool's
        // referral UTM — the whole tool shares one ref; the URL path decides the destination.
        public const string AFFILIATE_PARAMS = "bg_ref=XCOW7ZDbbx&utm_source=Importer%20Synty&utm_medium=referral&utm_campaign=Synty%20Internal%20Program";
        // Store-wide (general/browse) and Synty Pass links, ready to open.
        public const string STORE_AFFILIATE_URL = "https://syntystore.com/?" + AFFILIATE_PARAMS;
        public const string PASS_AFFILIATE_URL = "https://syntystore.com/products/syntypass?" + AFFILIATE_PARAMS;

        // Append the affiliate tracking params to any Synty store URL (handles existing query strings).
        public static string Affiliate(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return url + (url.Contains("?") ? "&" : "?") + AFFILIATE_PARAMS;
        }

        public static void AddToCart(AssetPackData pack)
        {
            // Open TWO pages so the customer gets BOTH attribution and a pre-filled cart:
            //  1) the affiliate asset/product page — loads with bg_ref, so BixGrow registers the
            //     referral and sets the cookie (a cart-add redirect can't carry the ref; Shopify
            //     strips it), then
            //  2) the cart-add — adds the item and lands on the cart. Same browser session, so the
            //     cookie from (1) attributes the order even though the cart-add itself has no ref.
            // The cart is opened a short beat AFTER the asset page so it's the last tab the browser
            // opens and ends up focused/on top.
            OpenProductPage(pack);
            if (pack != null && !string.IsNullOrEmpty(pack.shopifyVariantId))
            {
                string variantId = pack.shopifyVariantId;
                double openAt = EditorApplication.timeSinceStartup + 0.6;
                EditorApplication.CallbackFunction openCart = null;
                openCart = () =>
                {
                    if (EditorApplication.timeSinceStartup < openAt) return;
                    EditorApplication.update -= openCart;
                    // Cart permalink with a cart attribute — Shopify carries attributes[...] through
                    // to the order's note_attributes permanently, so attribution doesn't depend on the
                    // fragile landing_site (which the buyer's prior store session overwrites).
                    Application.OpenURL($"https://syntystore.com/cart/{variantId}:1?attributes[synty_source]=importer");
                };
                EditorApplication.update += openCart;
            }
        }
        
        /// <summary>
        /// Add multiple packs to cart (opens browser), affiliate-tracked.
        /// </summary>
        public static void AddMultipleToCart(List<AssetPackData> packs)
        {
            var withVariant = packs.Where(p => !string.IsNullOrEmpty(p.shopifyVariantId)).ToList();
            if (withVariant.Count == 0)
            {
                Application.OpenURL(STORE_AFFILIATE_URL);
                return;
            }
            // Same two-page pattern as single add-to-cart: open ONE affiliate product page first so
            // it loads with bg_ref (BixGrow registers the referral + sets the cookie), then a beat
            // later open the multi-item cart permalink — it adds all items and lands on the cart,
            // focused/on top. The cart permalink is a redirect and can't carry the ref, but the
            // cookie from the product page attributes the whole order.
            OpenProductPage(withVariant[0]);
            string items = string.Join(",", withVariant.Select(p => $"{p.shopifyVariantId}:1"));
            // Cart attribute carried to the order's note_attributes (reliable, landing-independent).
            string cartUrl = $"https://syntystore.com/cart/{items}?attributes[synty_source]=importer";
            double openAt = EditorApplication.timeSinceStartup + 0.6;
            EditorApplication.CallbackFunction openCart = null;
            openCart = () =>
            {
                if (EditorApplication.timeSinceStartup < openAt) return;
                EditorApplication.update -= openCart;
                Application.OpenURL(cartUrl);
            };
            EditorApplication.update += openCart;
        }
        
        /// <summary>
        /// Open a pack's store (product) page in the browser, affiliate-tracked.
        /// </summary>
        public static void OpenProductPage(AssetPackData pack)
        {
            // storeUrl is the product URL (https://syntystore.com/products/{handle}); appending the
            // affiliate params attributes the click to the tool.
            if (!string.IsNullOrEmpty(pack?.storeUrl))
            {
                Application.OpenURL(Affiliate(pack.storeUrl));
            }
            else
            {
                string handle = SyntyStoreService.ExtractHandleFromUrl(pack?.storeUrl);
                if (!string.IsNullOrEmpty(handle))
                    Application.OpenURL(Affiliate($"https://syntystore.com/products/{handle}"));
                else
                    Application.OpenURL(STORE_AFFILIATE_URL);
            }
        }
        
        // ─── Icon Downloading ────────────────────────────────────────────
        
        private static HashSet<string> _downloadingIcons = new HashSet<string>();
        
        // Icon version tracking — persists downloaded icon versions locally
        private const string ICON_VERSIONS_FILE = "Assets/Synty/Importer/Editor/Images/AssetIcons/icon_versions.json";
        
        [System.Serializable]
        private class IconVersionMap
        {
            public List<string> handles = new List<string>();
            public List<int> versions = new List<int>();
        }
        
        private static Dictionary<string, int> LoadIconVersions()
        {
            var dict = new Dictionary<string, int>();
            if (!File.Exists(ICON_VERSIONS_FILE)) return dict;
            try
            {
                string json = File.ReadAllText(ICON_VERSIONS_FILE);
                var map = JsonUtility.FromJson<IconVersionMap>(json);
                if (map != null)
                    for (int i = 0; i < map.handles.Count && i < map.versions.Count; i++)
                        dict[map.handles[i]] = map.versions[i];
            }
            catch { }
            return dict;
        }
        
        private static void SaveIconVersions(Dictionary<string, int> dict)
        {
            try
            {
                var map = new IconVersionMap();
                foreach (var kv in dict)
                {
                    map.handles.Add(kv.Key);
                    map.versions.Add(kv.Value);
                }
                string dir = Path.GetDirectoryName(ICON_VERSIONS_FILE);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ICON_VERSIONS_FILE, JsonUtility.ToJson(map));
            }
            catch { }
        }
        
        /// <summary>
        /// Quick check: do all packs have icons on disk with current versions?
        /// </summary>
        public static bool HasAllIcons(List<AssetPackData> packs)
        {
            if (packs == null || packs.Count == 0) return true;
            if (!Directory.Exists(ICON_CACHE_FOLDER)) return false;
            
            var localVersions = LoadIconVersions();
            
            foreach (var pack in packs)
            {
                string filename = GetIconFilename(pack);
                string path = Path.Combine(ICON_CACHE_FOLDER, filename);
                if (!File.Exists(path))
                {
                    return false;
                }
                
                // Check version
                string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                int serverVersion = pack.iconVersion;
                int localVersion = 0;
                if (!string.IsNullOrEmpty(handle))
                    localVersions.TryGetValue(handle, out localVersion);
                if (serverVersion > localVersion)
                {
                    return false;
                }
            }
            return true;
        }
        
        /// <summary>
        /// Ensure all catalog icons exist locally with correct versions.
        /// Downloads missing or outdated icons from Cloudflare.
        /// Calls onProgress(current, total) as icons download, onComplete when done.
        /// </summary>
        public static void EnsureIcons(List<AssetPackData> packs, 
            Action<int, int> onProgress = null, 
            Action onComplete = null)
        {
            if (packs == null || packs.Count == 0)
            {
                onComplete?.Invoke();
                return;
            }
            
            // Ensure folder exists
            if (!Directory.Exists(ICON_CACHE_FOLDER))
                Directory.CreateDirectory(ICON_CACHE_FOLDER);
            
            // Find missing or outdated icons
            var localVersions = LoadIconVersions();
            var missing = new List<AssetPackData>();
            foreach (var pack in packs)
            {
                string filename = GetIconFilename(pack);
                string localPath = Path.Combine(ICON_CACHE_FOLDER, filename);
                string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                
                if (!File.Exists(localPath))
                {
                    missing.Add(pack);
                }
                else if (pack.iconVersion > 0 && !string.IsNullOrEmpty(handle))
                {
                    int localVer = 0;
                    localVersions.TryGetValue(handle, out localVer);
                    if (pack.iconVersion > localVer)
                    {
                        // Delete outdated icon so it re-downloads
                        try { File.Delete(localPath); } catch { }
                        try { File.Delete(localPath + ".meta"); } catch { }
                        missing.Add(pack);
                    }
                }
            }
            
            if (missing.Count == 0)
            {
                // Ensure version tracking is up to date even when nothing needs downloading
                bool versionsDirty = false;
                foreach (var pack in packs)
                {
                    string h = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                    if (!string.IsNullOrEmpty(h) && pack.iconVersion > 0)
                    {
                        int cur = 0;
                        localVersions.TryGetValue(h, out cur);
                        if (cur != pack.iconVersion) { localVersions[h] = pack.iconVersion; versionsDirty = true; }
                    }
                }
                if (versionsDirty) SaveIconVersions(localVersions);
                
                onComplete?.Invoke();
                return;
            }
            
            int completed = 0;
            int total = missing.Count;
            var downloadedPaths = new List<string>();
            
            foreach (var pack in missing)
            {
                string filename = GetIconFilename(pack);
                if (_downloadingIcons.Contains(filename)) continue;
                _downloadingIcons.Add(filename);
                
                string url = GetIconUrl(pack);
                string localPath = Path.Combine(ICON_CACHE_FOLDER, filename);
                
                DownloadIcon(url, localPath, filename, () =>
                {
                    completed++;
                    if (File.Exists(localPath))
                        downloadedPaths.Add(localPath);
                    onProgress?.Invoke(completed, total);
                    if (completed >= total)
                    {
                        // Use targeted imports instead of full AssetDatabase.Refresh()
                        if (downloadedPaths.Count > 0)
                        {
                            // Pass 1: Import all files into AssetDatabase
                            var assetPaths = new List<string>();
                            AssetDatabase.StartAssetEditing();
                            try
                            {
                                foreach (var path in downloadedPaths)
                                {
                                    string assetPath = path.Replace("\\", "/");
                                    if (assetPath.StartsWith(Application.dataPath))
                                        assetPath = "Assets" + assetPath.Substring(Application.dataPath.Length);
                                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.Default);
                                    assetPaths.Add(assetPath);
                                }
                            }
                            finally
                            {
                                AssetDatabase.StopAssetEditing();
                            }
                            
                            // Pass 2: Set import settings (must be after StopAssetEditing so importers exist)
                            foreach (var assetPath in assetPaths)
                            {
                                var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
                                if (importer != null)
                                {
                                    importer.textureType = TextureImporterType.GUI;
                                    importer.mipmapEnabled = false;
                                    importer.maxTextureSize = 512;
                                    importer.textureCompression = TextureImporterCompression.Uncompressed;
                                    
                                    var platformSettings = importer.GetDefaultPlatformTextureSettings();
                                    platformSettings.format = TextureImporterFormat.RGBA32;
                                    platformSettings.maxTextureSize = 512;
                                    importer.SetPlatformTextureSettings(platformSettings);
                                    
                                    importer.SaveAndReimport();
                                }
                            }
                            
                        }
                        
                        // Update local icon version tracking
                        foreach (var pack in missing)
                        {
                            string h = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
                            if (!string.IsNullOrEmpty(h) && pack.iconVersion > 0)
                                localVersions[h] = pack.iconVersion;
                        }
                        SaveIconVersions(localVersions);
                        
                        onComplete?.Invoke();
                    }
                });
            }
        }
        
        private static async void DownloadIcon(string url, string localPath, string filename, Action onDone)
        {
            try
            {
                using (var client = new WebClient())
                {
                    byte[] data = await client.DownloadDataTaskAsync(new Uri(url));
                    
                    if (data != null && data.Length > 8)
                    {
                        bool isPng = data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
                        bool isJpeg = data[0] == 0xFF && data[1] == 0xD8;
                        
                        if (isPng || isJpeg)
                        {
                            System.IO.File.WriteAllBytes(localPath, data);
                        }
                        else
                        {
                            string preview = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(200, data.Length));
                            _downloadingIcons.Remove(filename); // Allow retry
                        }
                    }
                    else
                    {
                        _downloadingIcons.Remove(filename);
                    }
                }
            }
            catch (WebException webEx)
            {
                var response = webEx.Response as System.Net.HttpWebResponse;
                int status = response != null ? (int)response.StatusCode : 0;
                _downloadingIcons.Remove(filename); // Allow retry
            }
            catch (Exception)
            {
                _downloadingIcons.Remove(filename);
            }
            finally
            {
                if (System.IO.File.Exists(localPath))
                {
                    var info = new System.IO.FileInfo(localPath);
                    if (info.Length < 100)
                    {
                        System.IO.File.Delete(localPath);
                    }
                }
                
                _downloadingIcons.Remove(filename);
                onDone?.Invoke();
            }
        }
        
        /// <summary>
        /// Get a pack by its product handle.
        /// </summary>
        public static AssetPackData GetPack(string handle)
        {
            if (string.IsNullOrEmpty(handle)) return null;
            _packsByHandle.TryGetValue(handle, out var pack);
            return pack;
        }
        
        /// <summary>
        /// Get a pack by its index in the V2 _assetItems list.
        /// Requires the mapping dictionary from the tool.
        /// </summary>
        public static AssetPackData GetPackByIndex(Dictionary<int, AssetPackData> mapping, int index)
        {
            if (mapping == null) return null;
            mapping.TryGetValue(index, out var pack);
            return pack;
        }
        
        // ─── Tool Update ─────────────────────────────────────────────────
        
        /// <summary>
        /// Check for tool updates and auto-download if available.
        ///
        /// Customers (no SyntyDebugUnlock marker in the project) silently get the LIVE channel.
        /// Internal/QA projects (SyntyDebugUnlock present) get a popup to choose Live or the newer
        /// Dev build, so a version can be tested before it is made live in the admin panel.
        /// </summary>
        // The accurate installed version: the installer records it from the package filename
        // (SyntyPassport_DisplayVersion) — the baked-in SyntyToolVersion.VERSION constant can be
        // stale if a build didn't bump it. Falls back to the constant when the pref is absent.
        private static string InstalledVersion()
        {
            string v = EditorPrefs.GetString("SyntyPassport_DisplayVersion", "");
            return string.IsNullOrEmpty(v) ? SyntyToolVersion.VERSION : v;
        }

        public static void CheckAndAutoUpdate(Action<bool, string> onResult = null)
        {
            string currentVersion = InstalledVersion();
            bool isDevProject = HasDebugUnlockMarker();

            if (!isDevProject)
            {
                // Customer path: live channel, silent auto-update.
                DownloadAndImportChannel("live", currentVersion, onResult);
                return;
            }

            // Dev/QA path: look up both channels, then prompt.
            SyntyStoreService.Instance.CheckToolUpdate(currentVersion,
                onSuccess: (liveHasUpdate, liveVersion, liveFilename) =>
                {
                    SyntyStoreService.Instance.CheckToolUpdate(currentVersion,
                        onSuccess: (devHasUpdate, devVersion, devFilename) =>
                        {
                            EditorApplication.delayCall += () =>
                                PromptDevOrLive(currentVersion, liveHasUpdate, liveVersion, liveFilename, devHasUpdate, devVersion, devFilename, onResult);
                        },
                        onError: (err) => { onResult?.Invoke(false, err); },
                        channel: "dev");
                },
                onError: (err) => { onResult?.Invoke(false, err); },
                channel: "live");
        }

        // True when the SyntyDebugUnlock marker type exists in the project (internal/QA build).
        public static bool HasDebugUnlockMarker()
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (asm.GetType("Synty.Passport.Dev.SyntyDebugUnlock") != null) return true; }
                catch { }
            }
            return false;
        }

        private static void PromptDevOrLive(string currentVersion,
            bool liveHasUpdate, string liveVersion, string liveFilename,
            bool devHasUpdate, string devVersion, string devFilename,
            Action<bool, string> onResult)
        {
            // Nothing newer than what's installed on either channel — do nothing (no prompt, no reinstall).
            if (!liveHasUpdate && !devHasUpdate)
            {
                onResult?.Invoke(false, "Up to date");
                return;
            }

            bool separateDevBuild = !string.IsNullOrEmpty(devFilename) && devFilename != liveFilename;

            if (separateDevBuild)
            {
                // A distinct dev/QA build is waiting — let the developer choose which to install.
                string message =
                    $"Current installed version: v{currentVersion}\n\n" +
                    $"LIVE (what customers get): v{liveVersion}\n" +
                    $"DEV (newest upload, for QA): v{devVersion}\n\n" +
                    "Which version do you want to install?";
                int choice = EditorUtility.DisplayDialogComplex(
                    "Synty Importer — Tool Update",
                    message,
                    "Install Dev (QA)",   // 0
                    "Cancel",             // 1
                    "Install Live");      // 2
                if (choice == 0) DownloadAndImportFile(devFilename, devVersion, onResult);
                else if (choice == 2) DownloadAndImportFile(liveFilename, liveVersion, onResult);
                else onResult?.Invoke(false, "Cancelled");
            }
            else
            {
                // No separate dev build — just update to live (same as the customer path).
                DownloadAndImportFile(liveFilename, liveVersion, onResult);
            }
        }

        // Resolves a channel to its file via tool-info, then downloads+imports if it's an update.
        // Forces a download+import of the newest DEV-channel build regardless of whether it's a
        // higher version than what's installed (used by the dev "Force Download Dev" button).
        public static void ForceDownloadDev(Action<bool, string> onResult = null)
        {
            string currentVersion = InstalledVersion();
            Debug.Log($"[Synty Importer] [ForceDev] checking dev channel (installed v{currentVersion})…");
            SyntyStoreService.Instance.CheckToolUpdate(currentVersion,
                onSuccess: (hasUpdate, newVersion, filename) =>
                {
                    Debug.Log($"[Synty Importer] [ForceDev] dev channel → filename='{filename}' version='{newVersion}' hasUpdate={hasUpdate}");
                    if (string.IsNullOrEmpty(filename))
                    {
                        onResult?.Invoke(false, "No dev build available on the dev channel.");
                        return;
                    }
                    DownloadAndImportFile(filename, newVersion, onResult); // silent auto-import (no manual Import dialog to click)
                },
                onError: (err) => { Debug.LogError($"[Synty Importer] [ForceDev] dev channel error: {err}"); onResult?.Invoke(false, err); },
                channel: "dev");
        }

        private static void DownloadAndImportChannel(string channel, string currentVersion, Action<bool, string> onResult)
        {
            SyntyStoreService.Instance.CheckToolUpdate(currentVersion,
                onSuccess: (hasUpdate, newVersion, filename) =>
                {
                    if (hasUpdate) DownloadAndImportFile(filename, newVersion, onResult);
                    else onResult?.Invoke(false, currentVersion);
                },
                onError: (err) => { onResult?.Invoke(false, err); },
                channel: channel);
        }

        // Downloads a specific package file and imports it.
        private static void DownloadAndImportFile(string filename, string version, Action<bool, string> onResult, bool interactive = false)
        {
            if (string.IsNullOrEmpty(filename)) { onResult?.Invoke(false, "No package filename"); return; }
            string downloadUrl = SyntyStoreService.Instance.GetToolDownloadUrl(filename);
            string destPath = Path.Combine(Application.dataPath, "..", "Library", "SyntyToolUpdate", filename);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
            Debug.Log($"[Synty Importer] downloading '{filename}' from {downloadUrl}");
            SyntyStoreService.Instance.DownloadFile(downloadUrl, destPath,
                onProgress: (progress) => { },
                onSuccess: (path) =>
                {
                    Debug.Log($"[Synty Importer] downloaded to {path} — importing (interactive={interactive})…");
                    AssetDatabase.ImportPackage(path, interactive);
                    // Record the installed version so the footer + update check reflect it immediately
                    // (don't rely on the imported package's installer to set it — the dev build wasn't).
                    if (!string.IsNullOrEmpty(version))
                        EditorPrefs.SetString("SyntyPassport_DisplayVersion", version);
                    onResult?.Invoke(true, version);
                },
                onError: (error) =>
                {
                    Debug.LogError($"[Synty Importer] Tool update download failed: {error}");
                    onResult?.Invoke(false, error);
                }
            );
        }
    }
}
