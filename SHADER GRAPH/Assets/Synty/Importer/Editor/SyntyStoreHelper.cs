using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace Synty.Tools
{
    /// <summary>
    /// Helper class to integrate SyntyStoreService with the Asset Downloader UI
    /// </summary>
    public static class SyntyStoreHelper
    {
        #region Authentication
        
        /// <summary>
        /// Check if user is logged in
        /// </summary>
        public static bool IsLoggedIn => SyntyStoreService.Instance.IsAuthenticated;
        
        /// <summary>Whether the logged-in user has the Synty Pass (All Access)</summary>
        public static bool HasSyntyPass => SyntyStoreService.Instance.HasSyntyPass;
        
        /// <summary>
        /// Get logged in user's email
        /// </summary>
        public static string LoggedInEmail => SyntyStoreService.Instance.CustomerEmail ?? "";
        
        /// <summary>
        /// Try to restore session from saved credentials and verify access
        /// </summary>
        public static async void TryRestoreSession(Action onSuccess, Action onNoSession)
        {
            // Session is automatically restored in SyntyStoreService constructor
            if (IsLoggedIn)
            {
                // Verify the session is still valid
                bool valid = await SyntyStoreService.Instance.VerifyRestoredSessionAsync();
                if (valid)
                {
                    onSuccess?.Invoke();
                }
                else
                {
                    onNoSession?.Invoke();
                }
            }
            else
            {
                onNoSession?.Invoke();
            }
        }
        
        /// <summary>
        /// Login with email and password
        /// </summary>
        public static async void Login(string email, string password, bool rememberMe, 
            Action onSuccess, Action<string> onError, Action onRequires2FA = null)
        {
            try
            {
                var result = await SyntyStoreService.Instance.LoginAsync(email, password, rememberMe);
                
                if (result.Success)
                {
                    onSuccess?.Invoke();
                }
                else
                {
                    onError?.Invoke(result.Error ?? "Login failed");
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }
        
        /// <summary>
        /// Login with email only (passwordless, Shopify New Customer Accounts)
        /// </summary>
        public static async void LoginWithEmail(string email, bool rememberMe,
            Action onSuccess, Action<string> onError)
        {
            try
            {
                var result = await SyntyStoreService.Instance.LoginWithEmailAsync(email, rememberMe);
                
                if (result.Success)
                {
                    onSuccess?.Invoke();
                }
                else
                {
                    onError?.Invoke(result.Error ?? "Login failed");
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }
        
        /// <summary>
        /// 2FA is not used with Shopify Storefront API
        /// </summary>
        public static void Verify2FA(string code, Action onSuccess, Action<string> onError)
        {
            onError?.Invoke("2FA is not required");
        }
        
        /// <summary>
        /// Cancel 2FA - not used
        /// </summary>
        public static void Cancel2FA() { }
        
        /// <summary>
        /// 2FA is never pending with this auth method
        /// </summary>
        public static bool IsPending2FA => false;
        
        /// <summary>
        /// Logout
        /// </summary>
        public static void Logout()
        {
            SyntyStoreService.Instance.Logout();
        }
        
        #endregion
        
        #region Browser Actions
        
        /// <summary>
        /// Open the Synty Store homepage
        /// </summary>
        public static void OpenStorePage()
        {
            SyntyStoreService.Instance.OpenStorePage();
        }
        
        /// <summary>
        /// Open cart page
        /// </summary>
        public static void OpenCartPage()
        {
            SyntyStoreService.Instance.OpenCartPage();
        }
        
        /// <summary>
        /// Open a specific product page
        /// </summary>
        public static void OpenProductPage(string handle)
        {
            SyntyStoreService.Instance.OpenProductPage(handle);
        }
        
        /// <summary>
        /// Open login/account page
        /// </summary>
        public static void OpenLoginPage()
        {
            SyntyStoreService.Instance.OpenLoginPage();
        }
        
        /// <summary>
        /// Alias for OpenLoginPage
        /// </summary>
        public static void OpenDownloadsPage()
        {
            SyntyStoreService.Instance.OpenLoginPage();
        }
        
        #endregion
        
        #region Asset Ownership
        
        /// <summary>
        /// Update ownership status for asset packs
        /// With the backend verification approach, we mark all as potentially owned if logged in
        /// Actual ownership is verified at download time
        /// </summary>
        public static void UpdateAssetOwnership(List<AssetPackData> assetPacks)
        {
            if (assetPacks == null) return;
            
            // With backend verification, we don't know ownership until download
            // If logged in, user can attempt to download any pack
            // The backend will verify ownership and return appropriate error if not owned
            
            // For UI purposes, we can leave notOwned as-is from the database
            // Or mark all as potentially downloadable if logged in
            
            // This is a no-op - ownership is verified at download time
        }
        
        #endregion
        
        #region Downloads
        
        /// <summary>
        /// Start downloading an asset pack (uses download manager for proper queuing)
        /// </summary>
        public static void StartDownload(AssetPackData pack)
        {
            if (pack == null) return;
            
            if (!IsLoggedIn)
            {
                return;
            }
            
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (string.IsNullOrEmpty(handle))
            {
                return;
            }
            
            // Use the download manager for proper sequential downloading with import waiting
            SyntyDownloadManager.Instance.AddToQueue(pack);
            SyntyDownloadManager.Instance.StartProcessing();
        }
        
        /// <summary>
        /// Download a product by handle
        /// </summary>
        public static async void DownloadProduct(string productHandle, string destinationFolder,
            Action<string> onSuccess, Action<string> onError)
        {
            try
            {
                var result = await SyntyStoreService.Instance.DownloadProductAsync(productHandle, destinationFolder);
                
                if (result.Success)
                {
                    onSuccess?.Invoke(result.FilePath);
                }
                else
                {
                    onError?.Invoke(result.Error ?? "Download failed");
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }
        
        /// <summary>
        /// Cancel an active download
        /// </summary>
        public static void CancelDownload(string productHandle)
        {
            SyntyDownloadManager.Instance.CancelDownload(productHandle);
        }
        
        /// <summary>
        /// Check if a download is in progress for a pack
        /// </summary>
        public static bool IsDownloading(AssetPackData pack)
        {
            if (pack == null) return false;
            
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (string.IsNullOrEmpty(handle)) return false;
            
            var item = SyntyDownloadManager.Instance.GetItem(handle);
            return item != null && item.status == SyntyDownloadManager.DownloadStatus.Downloading;
        }
        
        /// <summary>
        /// Check if a pack is in the download queue
        /// </summary>
        public static bool IsInQueue(AssetPackData pack)
        {
            if (pack == null) return false;
            
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (string.IsNullOrEmpty(handle)) return false;
            
            var item = SyntyDownloadManager.Instance.GetItem(handle);
            return item != null && (item.status == SyntyDownloadManager.DownloadStatus.Queued || 
                                    item.status == SyntyDownloadManager.DownloadStatus.Downloading ||
                                    item.status == SyntyDownloadManager.DownloadStatus.Importing);
        }
        
        /// <summary>
        /// Get download progress for a pack (0-1)
        /// </summary>
        public static float GetDownloadProgress(AssetPackData pack)
        {
            if (pack == null) return 0f;
            
            string handle = SyntyStoreService.ExtractHandleFromUrl(pack.storeUrl);
            if (string.IsNullOrEmpty(handle)) return 0f;
            
            var item = SyntyDownloadManager.Instance.GetItem(handle);
            if (item != null)
            {
                return item.progress;
            }
            
            return 0f;
        }
        
        #endregion
        
        #region Cart
        
        /// <summary>
        /// Add a product to cart (opens browser)
        /// </summary>
        public static void AddToCart(string variantId)
        {
            SyntyStoreService.Instance.AddToCart(variantId);
        }
        
        /// <summary>
        /// Add multiple products to cart (opens browser)
        /// </summary>
        public static void AddMultipleToCart(List<string> variantIds)
        {
            SyntyStoreService.Instance.AddMultipleToCart(variantIds);
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Format file size for display
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            return SyntyStoreService.FormatFileSize(bytes);
        }
        
        #endregion
        
        #region Catalog
        
        private static List<AssetPackData> _catalogPacks;
        private static List<string> _catalogThemes;
        private static bool _isCatalogLoading;
        
        /// <summary>
        /// Whether the catalog is currently loading
        /// </summary>
        public static bool IsCatalogLoading => _isCatalogLoading;
        
        /// <summary>
        /// Get cached catalog packs (null if not loaded)
        /// </summary>
        public static List<AssetPackData> CatalogPacks => _catalogPacks;
        
        /// <summary>
        /// Get available themes from catalog
        /// </summary>
        public static List<string> CatalogThemes => _catalogThemes;
        
        /// <summary>
        /// Load product catalog from the worker
        /// </summary>
        public static async void LoadCatalog(Action<List<AssetPackData>> onSuccess, Action<string> onError, bool forceRefresh = false)
        {
            if (_isCatalogLoading)
            {
                return;
            }
            
            _isCatalogLoading = true;
            
            try
            {
                var catalog = await SyntyStoreService.Instance.FetchCatalogAsync(forceRefresh);
                
                if (catalog != null && catalog.success)
                {
                    _catalogPacks = SyntyStoreService.Instance.ConvertCatalogToAssetPacks(catalog);
                    _catalogThemes = catalog.themes != null ? new List<string>(catalog.themes) : new List<string>();
                    
                    onSuccess?.Invoke(_catalogPacks);
                }
                else
                {
                    string error = catalog?.error ?? "Failed to load catalog";
                    Debug.LogError($"[SyntyStoreHelper] Catalog load failed: {error}");
                    onError?.Invoke(error);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SyntyStoreHelper] Catalog load exception: {ex.Message}");
                onError?.Invoke(ex.Message);
            }
            finally
            {
                _isCatalogLoading = false;
            }
        }
        
        /// <summary>
        /// Check if catalog is loaded
        /// </summary>
        public static bool IsCatalogLoaded => _catalogPacks != null && _catalogPacks.Count > 0;
        
        #endregion
    }
}
