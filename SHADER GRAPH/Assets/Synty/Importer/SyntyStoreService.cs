#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;

namespace Synty.Tools
{
    /// <summary>
    /// Backend service for Synty Store integration
    /// Handles authentication via Shopify Storefront API and verified downloads via backend worker
    /// </summary>
    public class SyntyStoreService
    {
        #region Constants
        
        private const string STORE_URL = "https://syntystore.com";
        private const string CART_URL = "https://syntystore.com/cart";
        
        // Shopify Storefront API
        // All auth and downloads go through the Worker - no client-side secrets
        private const string DOWNLOAD_API_URL = "https://synty-downloads.syntystore.workers.dev";
        
        // EditorPrefs keys for persistent storage
        private const string PREF_ACCESS_TOKEN = "SyntyStore_AccessToken";
        private const string PREF_SESSION_TOKEN = "SyntyStore_SessionToken";
        private const string PREF_CUSTOMER_EMAIL = "SyntyStore_CustomerEmail";
        private const string PREF_REMEMBER_ME = "SyntyStore_RememberMe";
        private const string PREF_HAS_SYNTY_PASS = "SyntyStore_HasSyntyPass";
        private const string PREF_OWNED_PRODUCTS = "SyntyStore_OwnedProducts";
        
        #endregion
        
        #region Singleton
        
        private static SyntyStoreService _instance;
        public static SyntyStoreService Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new SyntyStoreService();
                }
                return _instance;
            }
        }
        
        private SyntyStoreService()
        {
            TryRestoreSession();
        }
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Whether the user is currently authenticated
        /// </summary>
        public bool IsAuthenticated { get; private set; }
        
        /// <summary>
        /// The authenticated user's email
        /// </summary>
        public string CustomerEmail { get; private set; }
        
        /// <summary>
        /// Access token for API calls (Shopify customer access token)
        /// </summary>
        private string AccessToken { get; set; }
        
        /// <summary>
        /// Session token from worker email login (New Customer Accounts)
        /// </summary>
        private string SessionToken { get; set; }
        
        /// <summary>
        /// Whether the authenticated user has the Synty Pass (All Access)
        /// </summary>
        public bool HasSyntyPass { get; private set; }
        private int _lastLoggedPassState = -1; // -1 none, 0 no-pass, 1 pass — dedups the console log
        public string CountryCode { get; private set; } // ISO country from account (e.g. "NZ"), for currency auto-detect
        
        #endregion
        
        #region Events
        
        public event Action OnLoginSuccess;
        public event Action OnLogout;
        public event Action OnLibraryUpdated;
        public event Action<string, float> OnDownloadProgress; // productHandle, progress 0-1
        public event Action<string> OnDownloadComplete; // productHandle
        public event Action<string, string> OnDownloadFailed; // productHandle, error
        public event Action<CatalogResponse> OnCatalogLoaded;
        public event Action<string> OnCatalogLoadFailed;
        
        #endregion
        
        #region Data Classes
        
        [Serializable]
        private class GraphQLRequest
        {
            public string query;
        }
        
        [Serializable]
        private class CustomerAccessTokenResponse
        {
            public CustomerAccessTokenData data;
        }
        
        [Serializable]
        private class CustomerAccessTokenData
        {
            public CustomerAccessTokenCreate customerAccessTokenCreate;
        }
        
        [Serializable]
        private class CustomerAccessTokenCreate
        {
            public CustomerAccessToken customerAccessToken;
            public List<CustomerUserError> customerUserErrors;
        }
        
        [Serializable]
        private class CustomerAccessToken
        {
            public string accessToken;
            public string expiresAt;
        }
        
        [Serializable]
        private class CustomerUserError
        {
            public string code;
            public string field;
            public string message;
        }
        
        [Serializable]
        private class CustomerQueryResponse
        {
            public CustomerQueryData data;
        }
        
        [Serializable]
        private class CustomerQueryData
        {
            public CustomerInfo customer;
        }
        
        [Serializable]
        private class CustomerInfo
        {
            public string id;
            public string email;
            public string firstName;
            public string lastName;
        }
        
        [Serializable]
        private class DownloadVerifyRequest
        {
            public string customer_token;
            public string session_token;
            public string product_handle;
        }
        
        [Serializable]
        private class DownloadVerifyResponse
        {
            public bool success;
            public bool owned;
            public string product_handle;
            public string download_url;
            public string filename;
            public string error;
        }
        
        public class LoginResult
        {
            public bool Success;
            public string Error;
            public string Email;
            public bool HasSyntyPass;
            public List<string> OwnedProducts;
            // True when the server requires a verification code before a session can be
            // minted (two-step OTP login). The UI should move to the code-entry step.
            public bool RequiresCode;
        }
        
        [Serializable]
        private class VerifyAccessResponse
        {
            public bool success;
            public string email;
            public bool hasSyntyPass;
            public string[] ownedProducts;
            public string countryCode;
            public BannerConfig banner;
            public string error;
        }
        
        /// <summary>
        /// Banner configuration returned by the worker, already resolved to the single banner
        /// this customer should see. Any empty string field means "use the tool's built-in
        /// default" for that field.
        /// </summary>
        [Serializable]
        public class BannerConfig
        {
            public string type;        // general | pass | newRelease
            public string tint;        // hex e.g. #FFF2C9 (empty = default)
            public string line1;
            public string line1Color;
            public string line2;
            public string line2Color;
            public string iconColor;
            public string link;        // URL the banner opens when clicked (empty = not clickable)
            public string titleX;      // Line 1 horizontal offset (empty = tool default)
            public string titleY;      // Line 1 Y from top
            public string titleFontSize;
            public string line2X;      // Line 2 (shop-now) horizontal offset
            public bool timerEnabled;  // replace the shop-now pill with a countdown timer
            public string timerEndTime; // ISO UTC end time for the countdown
            public string timerTextColor; // hex, overrides timer text/box colors (empty = tool default)
            public string timerLabelColor; // hex, overrides the DAYS/HOURS unit label color
            public string timerBoxColor;   // hex, overrides the box background (default red)
            public string timerColonColor; // hex, overrides the ":" separator color
            public string timerX;       // horizontal offset (empty = tool default)
            public string timerY;       // vertical offset (empty = tool default)
            public string timerScale;   // uniform scale (empty = tool default)
            public bool hasImage;
            public string imageVersion; // changes when the admin uploads a new image; used for disk caching
            public string imageUrl;    // absolute URL to the banner image, if hasImage
            public string productHandle;
        }
        
        /// <summary>
        /// The banner the server selected for this customer (null if none / not yet verified).
        /// </summary>
        public BannerConfig Banner { get; private set; }
        
        /// <summary>
        /// Set of product handles the customer owns individually (from order history)
        /// </summary>
        public HashSet<string> OwnedProductHandles { get; private set; } = new HashSet<string>();
        
        public class DownloadResult
        {
            public bool Success;
            public string Error;
            public string FilePath;
            public string FileName;
        }
        
        // Catalog response classes
        [Serializable]
        public class CatalogResponse
        {
            public bool success;
            public CatalogProduct[] products;
            public string[] themes;
            public int total;
            public string error;
            public SyntyPassPricing syntyPass;
        }
        
        [Serializable]
        public class SyntyPassPricing
        {
            public string monthly;  // e.g. "40.0"
            public string annual;   // e.g. "30.0"
            public string currency; // e.g. "USD"
        }
        
        [Serializable]
        public class CatalogProduct
        {
            public string handle;
            public string title;
            public string type;
            public string image;
            public bool hasDownload;
            public bool notDownloadable;
            public string releaseDate;      // Defaults to createdAt from Shopify
            public string createdAt;        // Original Shopify creation date
            public string[] themes;
            public string filename;         // Current filename in bucket for version tracking
            public string version;          // Package version (e.g. "1.0.6")
            public string price;            // USD price as string (e.g. "29.99")
            public string compareAtPrice;   // Original price when on sale (e.g. "39.99"), null if not on sale
            public string currency;         // Currency code (e.g. "USD")
            public string variantId;        // Shopify variant GID for cart
            public int iconVersion;         // Manual icon version - increment to force re-download
        }
        
        [Serializable]
        public class ToolInfoResponse
        {
            public bool success;
            public string version;
            public string filename;
            public string error;
        }
        
        /// <summary>
        /// Metadata encoded in themes array by admin page as __meta:{json}
        /// </summary>
        [Serializable]
        private class CatalogMeta
        {
            public string type;
            public string storeUrl;
            public string icon;
        }
        
        #endregion
        
        #region Session Management
        
        private void TryRestoreSession()
        {
            bool rememberMe = EditorPrefs.GetBool(PREF_REMEMBER_ME, false);
            if (!rememberMe) return;
            
            string token = EditorPrefs.GetString(PREF_ACCESS_TOKEN, "");
            string sessionToken = EditorPrefs.GetString(PREF_SESSION_TOKEN, "");
            string email = EditorPrefs.GetString(PREF_CUSTOMER_EMAIL, "");
            
            bool hasAuth = (!string.IsNullOrEmpty(token) || !string.IsNullOrEmpty(sessionToken)) && !string.IsNullOrEmpty(email);
            
            if (hasAuth)
            {
                AccessToken = string.IsNullOrEmpty(token) ? null : token;
                SessionToken = string.IsNullOrEmpty(sessionToken) ? null : sessionToken;
                CustomerEmail = email;
                IsAuthenticated = true;
                
                // Restore pass status and owned products from prefs (available immediately, before async verify)
                HasSyntyPass = EditorPrefs.GetBool(PREF_HAS_SYNTY_PASS, false);
                
                string ownedCsv = EditorPrefs.GetString(PREF_OWNED_PRODUCTS, "");
                OwnedProductHandles.Clear();
                if (!string.IsNullOrEmpty(ownedCsv))
                {
                    foreach (var handle in ownedCsv.Split(','))
                    {
                        string h = handle.Trim();
                        if (!string.IsNullOrEmpty(h))
                            OwnedProductHandles.Add(h);
                    }
                }
                
            }
        }
        
        /// <summary>
        /// Verify the restored session is still valid and check pass status.
        /// Returns true if the session token is valid (regardless of pass status).
        /// </summary>
        public async Task<bool> VerifyRestoredSessionAsync(bool force = false)
        {
            // Prefer SessionToken (email login) over AccessToken (legacy password login)
            bool hasSession = !string.IsNullOrEmpty(SessionToken);
            bool hasAccess = !string.IsNullOrEmpty(AccessToken);
            
            if (!IsAuthenticated || (!hasSession && !hasAccess))
            {
                return false;
            }
            
            // Capture the current token so we can detect if login changed during the async verify
            string tokenAtStart = SessionToken ?? AccessToken ?? "";
            
            // Pass whichever token is available - VerifyAllAccessPass sends
            // session_token if SessionToken is set, otherwise customer_token
            var result = await VerifyAllAccessPass(hasAccess ? AccessToken : "", force);
            
            // If the session changed while we were verifying (user logged in as different account),
            // discard this stale response
            string tokenNow = SessionToken ?? AccessToken ?? "";
            if (tokenNow != tokenAtStart)
            {
                return false;
            }
            
            if (!result.Success)
            {
                Synty.Tools.SyntyLog.Warn("Session expired, please sign in again");
                Logout();
                return false;
            }
            
            // Update state from verified response
            HasSyntyPass = result.HasSyntyPass;
            
            // Update email from server response to prevent stale account data
            if (!string.IsNullOrEmpty(result.Email))
            {
                CustomerEmail = result.Email;
                if (EditorPrefs.GetBool(PREF_REMEMBER_ME, false))
                    EditorPrefs.SetString(PREF_CUSTOMER_EMAIL, CustomerEmail);
            }
            
            // Persist updated ownership to EditorPrefs for instant restore on next domain reload
            SaveOwnershipToPrefs();
            
            return true;
        }
        
        // Returns the customer's current order count from the backend, or -1 if it can't be
        // determined (not logged in, network error, etc.). Used by the tool to cheaply detect
        // a new purchase on user interaction without re-fetching all owned products.
        public async Task<int> GetOrderCountAsync()
        {
            if (string.IsNullOrEmpty(SessionToken)) return -1;
            string tokenAtStart = SessionToken;
            try
            {
                string countUrl = DOWNLOAD_API_URL + "/account/order-count";
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(countUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                string jsonBody = $"{{\"session_token\":\"{SessionToken}\"}}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                using (Stream requestStream = await request.GetRequestStreamAsync())
                    await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = await reader.ReadToEndAsync();
                    if (SessionToken != tokenAtStart) return -1; // session changed mid-call
                    var parsed = JsonUtility.FromJson<OrderCountResponse>(responseBody);
                    return parsed != null ? parsed.orderCount : -1;
                }
            }
            catch
            {
                return -1; // treat any failure as "unknown" -> no refresh
            }
        }
        
        [System.Serializable]
        private class OrderCountResponse
        {
            public bool success;
            public int orderCount;
        }
        
        private void SaveSession(bool rememberMe)
        {
            EditorPrefs.SetBool(PREF_REMEMBER_ME, rememberMe);
            
            if (rememberMe)
            {
                EditorPrefs.SetString(PREF_ACCESS_TOKEN, AccessToken ?? "");
                EditorPrefs.SetString(PREF_SESSION_TOKEN, SessionToken ?? "");
                EditorPrefs.SetString(PREF_CUSTOMER_EMAIL, CustomerEmail ?? "");
                EditorPrefs.SetBool(PREF_HAS_SYNTY_PASS, HasSyntyPass);
                EditorPrefs.SetString(PREF_OWNED_PRODUCTS, string.Join(",", OwnedProductHandles));
            }
            else
            {
                EditorPrefs.DeleteKey(PREF_ACCESS_TOKEN);
                EditorPrefs.DeleteKey(PREF_SESSION_TOKEN);
                EditorPrefs.DeleteKey(PREF_CUSTOMER_EMAIL);
                EditorPrefs.DeleteKey(PREF_HAS_SYNTY_PASS);
                EditorPrefs.DeleteKey(PREF_OWNED_PRODUCTS);
            }
        }
        
        /// <summary>
        /// Save ownership data to EditorPrefs (called after verify updates ownership).
        /// </summary>
        private void SaveOwnershipToPrefs()
        {
            if (!EditorPrefs.GetBool(PREF_REMEMBER_ME, false)) return;
            EditorPrefs.SetBool(PREF_HAS_SYNTY_PASS, HasSyntyPass);
            EditorPrefs.SetString(PREF_OWNED_PRODUCTS, string.Join(",", OwnedProductHandles));
        }
        
        private void ClearSession()
        {
            EditorPrefs.DeleteKey(PREF_ACCESS_TOKEN);
            EditorPrefs.DeleteKey(PREF_SESSION_TOKEN);
            EditorPrefs.DeleteKey(PREF_CUSTOMER_EMAIL);
            EditorPrefs.DeleteKey(PREF_REMEMBER_ME);
            EditorPrefs.DeleteKey(PREF_HAS_SYNTY_PASS);
            EditorPrefs.DeleteKey(PREF_OWNED_PRODUCTS);
        }
        
        #endregion
        
        #region Authentication
        
        /// <summary>
        /// Login with email only via worker (for Shopify New Customer Accounts - no password).
        /// Worker looks up customer by email and creates a session.
        /// </summary>
        /// <summary>
        /// Step 1 of two-step OTP login: ask the worker to email a 6-digit sign-in code
        /// to this address. The worker always responds generically (it never reveals
        /// whether the email belongs to a customer), so a true result here only means
        /// "the request was accepted", not "the account exists".
        /// </summary>
        public async Task<LoginResult> RequestLoginCodeAsync(string email)
        {
            try
            {
                string requestUrl = DOWNLOAD_API_URL + "/auth/request-code";
                
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(requestUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 30000;
                
                string jsonBody = $"{{\"email\":\"{EscapeJsonString(email)}\"}}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                
                using (Stream requestStream = await request.GetRequestStreamAsync())
                {
                    await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                }
                
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = await reader.ReadToEndAsync();
                    var parsed = JsonUtility.FromJson<OtpRequestResponse>(responseBody);
                    
                    if (parsed != null && parsed.success)
                    {
                        return new LoginResult { Success = true, RequiresCode = true, Email = email };
                    }
                    return new LoginResult { Success = false, Error = parsed?.error ?? "Could not send a sign-in code. Please try again." };
                }
            }
            catch (WebException webEx)
            {
                string serverError = ExtractServerError(webEx);
                if (!string.IsNullOrEmpty(serverError))
                    return new LoginResult { Success = false, Error = serverError };
                return new LoginResult { Success = false, Error = "Unable to connect. Please check your internet connection." };
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyStoreService] Request code failed: {ex.Message}");
                return new LoginResult { Success = false, Error = "Could not send a sign-in code. Please try again." };
            }
        }
        
        /// <summary>
        /// Step 2 of two-step OTP login: submit the emailed code. On success the worker
        /// mints the same 30-day session token the old single-step login produced, and
        /// this method establishes local session state exactly the way the old
        /// LoginWithEmailAsync did (SessionToken, EditorPrefs persistence, events).
        /// </summary>
        public async Task<LoginResult> VerifyLoginCodeAsync(string email, string code, bool rememberMe)
        {
            try
            {
                // Clear any previous session to prevent stale data
                AccessToken = null;
                SessionToken = null;
                IsAuthenticated = false;
                
                string verifyUrl = DOWNLOAD_API_URL + "/auth/verify-code";
                
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(verifyUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 30000;
                
                string jsonBody = $"{{\"email\":\"{EscapeJsonString(email)}\",\"code\":\"{EscapeJsonString(code)}\"}}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                
                using (Stream requestStream = await request.GetRequestStreamAsync())
                {
                    await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                }
                
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = await reader.ReadToEndAsync();
                    
                    var loginResponse = JsonUtility.FromJson<EmailLoginResponse>(responseBody);
                    
                    if (loginResponse.success && !string.IsNullOrEmpty(loginResponse.session_token))
                    {
                        SessionToken = loginResponse.session_token;
                        AccessToken = null; // not used with email login
                        CustomerEmail = email;
                        IsAuthenticated = true;
                        HasSyntyPass = loginResponse.hasSyntyPass;
                        
                        // Store owned products
                        OwnedProductHandles.Clear();
                        if (loginResponse.ownedProducts != null)
                        {
                            foreach (var handle in loginResponse.ownedProducts)
                                OwnedProductHandles.Add(handle);
                        }
                        
                        SaveSession(rememberMe);
                        OnLoginSuccess?.Invoke();
                        OnLibraryUpdated?.Invoke();
                        
                        return new LoginResult { Success = true, HasSyntyPass = HasSyntyPass, OwnedProducts = new List<string>(OwnedProductHandles) };
                    }
                    
                    return new LoginResult { Success = false, Error = "Sign-in failed" };
                }
            }
            catch (WebException webEx)
            {
                string serverError = ExtractServerError(webEx);
                if (!string.IsNullOrEmpty(serverError))
                {
                    // Debug.LogError($"[SyntyStoreService] Verify code error: {serverError}");
                    return new LoginResult { Success = false, Error = serverError };
                }
                return new LoginResult { Success = false, Error = "Unable to connect. Please check your internet connection." };
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyStoreService] Verify code failed: {ex.Message}");
                return new LoginResult { Success = false, Error = "Sign-in failed. Please try again." };
            }
        }
        
        /// <summary>
        /// Legacy single-step email login. The worker no longer issues sessions from a
        /// bare email (that allowed anyone knowing a customer's address to log in as
        /// them), so this now just requests a verification code and reports
        /// RequiresCode so callers can move to the code-entry step. Use
        /// RequestLoginCodeAsync + VerifyLoginCodeAsync directly in new code.
        /// </summary>
        public async Task<LoginResult> LoginWithEmailAsync(string email, bool rememberMe)
        {
            var result = await RequestLoginCodeAsync(email);
            if (result.Success)
            {
                result.Success = false; // no session yet - a code is required to finish
                result.RequiresCode = true;
                result.Error = "A verification code has been sent to your email.";
            }
            return result;
        }
        
        // Pulls the {"error":"..."} message out of an HTTP error response body, if present.
        private static string ExtractServerError(WebException webEx)
        {
            try
            {
                if (webEx.Response == null) return null;
                using (StreamReader reader = new StreamReader(webEx.Response.GetResponseStream()))
                {
                    string errorBody = reader.ReadToEnd();
                    int errorStart = errorBody.IndexOf("\"error\":\"") + 9;
                    int errorEnd = errorBody.IndexOf("\"", errorStart);
                    if (errorStart > 8 && errorEnd > errorStart)
                        return errorBody.Substring(errorStart, errorEnd - errorStart);
                }
            }
            catch { }
            return null;
        }
        
        [Serializable]
        private class OtpRequestResponse
        {
            public bool success;
            public string message;
            public string error;
        }
        
        [Serializable]
        private class EmailLoginResponse
        {
            public bool success;
            public string session_token;
            public string email;
            public bool hasSyntyPass;
            public string[] ownedProducts;
        }
        
        /// <summary>
        /// Legacy password login - redirects to email-only login via Worker.
        /// Shopify New Customer Accounts have no passwords.
        /// </summary>
        public async Task<LoginResult> LoginAsync(string email, string password, bool rememberMe)
        {
            // Password login is no longer supported with Shopify New Customer Accounts.
            // Redirect to email-only login via the Worker.
            return await LoginWithEmailAsync(email, rememberMe);
        }
        
        /// <summary>
        /// Logout and clear session
        /// </summary>
        public void Logout()
        {
            AccessToken = null;
            SessionToken = null;
            CustomerEmail = null;
            IsAuthenticated = false;
            HasSyntyPass = false;
            OwnedProductHandles.Clear();
            
            ClearSession();
            
            OnLogout?.Invoke();
        }
        
        /// <summary>
        /// Verify user has All Access Pass via backend
        /// </summary>
        private async Task<LoginResult> VerifyAllAccessPass(string customerToken, bool force = false)
        {
            // Capture token at start to detect session changes during async call
            string tokenAtStart = SessionToken ?? AccessToken ?? "";
            
            try
            {
                string verifyUrl = DOWNLOAD_API_URL + "/verify-access";
                
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(verifyUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                
                string forceField = force ? ",\"force\":true" : "";
                string jsonBody;
                if (!string.IsNullOrEmpty(SessionToken))
                    jsonBody = $"{{\"session_token\":\"{SessionToken}\"{forceField}}}";
                else
                    jsonBody = $"{{\"customer_token\":\"{customerToken}\"{forceField}}}";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                
                using (Stream requestStream = await request.GetRequestStreamAsync())
                {
                    await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                }
                
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = await reader.ReadToEndAsync();
                    
                    // Check if session changed while we were waiting for the response
                    string tokenNow = SessionToken ?? AccessToken ?? "";
                    if (tokenNow != tokenAtStart)
                    {
                        return new LoginResult { Success = false, Error = "Session changed" };
                    }
                    
                    var verifyResponse = JsonUtility.FromJson<VerifyAccessResponse>(responseBody);
                    
                    // Store owned products
                    OwnedProductHandles.Clear();
                    if (verifyResponse.ownedProducts != null)
                    {
                        foreach (var handle in verifyResponse.ownedProducts)
                            OwnedProductHandles.Add(handle);
                    }
                    
                    HasSyntyPass = verifyResponse.hasSyntyPass;
                    // Only log when the pass state actually changes, so repeated verify calls
                    // (login + refresh ticks) don't spam the same line.
                    if (_lastLoggedPassState != (verifyResponse.hasSyntyPass ? 1 : 0))
                    {
                        _lastLoggedPassState = verifyResponse.hasSyntyPass ? 1 : 0;
                        Synty.Tools.SyntyLog.Info(verifyResponse.hasSyntyPass
                            ? "Synty Pass active"
                            : "No active Synty Pass on this account");
                    }
                    if (!string.IsNullOrEmpty(verifyResponse.countryCode))
                        CountryCode = verifyResponse.countryCode;
                    Banner = verifyResponse.banner; // may be null
                    
                    return new LoginResult 
                    { 
                        Success = true, 
                        Email = verifyResponse.email,
                        HasSyntyPass = verifyResponse.hasSyntyPass,
                        OwnedProducts = new List<string>(OwnedProductHandles)
                    };
                }
            }
            catch (WebException webEx)
            {
                if (webEx.Response != null)
                {
                    using (StreamReader reader = new StreamReader(webEx.Response.GetResponseStream()))
                    {
                        string errorBody = reader.ReadToEnd();
                        // Debug.LogError($"[SyntyStoreService] Verify error: {errorBody}");
                        
                        try
                        {
                            int errorStart = errorBody.IndexOf("\"error\":\"") + 9;
                            int errorEnd = errorBody.IndexOf("\"", errorStart);
                            if (errorStart > 8 && errorEnd > errorStart)
                            {
                                string errorMsg = errorBody.Substring(errorStart, errorEnd - errorStart);
                                return new LoginResult { Success = false, Error = errorMsg };
                            }
                        }
                        catch { }
                    }
                }
                return new LoginResult { Success = false, Error = "Your account does not have an active All Access Pass." };
            }
            catch (Exception)
            {
                // Debug.LogError($"[SyntyStoreService] Verify access failed: {ex.Message}");
                return new LoginResult { Success = false, Error = "Unable to verify account access." };
            }
        }
        
        #endregion
        
        #region Catalog Management
        
        private CatalogResponse _cachedCatalog;
        private DateTime _catalogCacheTime;
        private const int CATALOG_CACHE_MINUTES = 5; // memory cache
        private const int CATALOG_DISK_CACHE_MINUTES = 30; // disk cache: 30 min (balances fast startup vs freshness)
        private static readonly string CATALOG_DISK_CACHE = Path.Combine(Application.temporaryCachePath, "synty_catalog_cache.json");
        
        /// <summary>
        /// Fetch product catalog from the worker. Uses disk cache for instant restore after domain reload.
        /// </summary>
        public async Task<CatalogResponse> FetchCatalogAsync(bool forceRefresh = false)
        {
            // Return memory-cached catalog if still valid
            if (!forceRefresh && _cachedCatalog != null && 
                (DateTime.Now - _catalogCacheTime).TotalMinutes < CATALOG_CACHE_MINUTES)
            {
                return _cachedCatalog;
            }
            
            // Try disk cache for instant restore (e.g. after domain reload)
            if (!forceRefresh && _cachedCatalog == null && File.Exists(CATALOG_DISK_CACHE))
            {
                try
                {
                    var cacheInfo = new FileInfo(CATALOG_DISK_CACHE);
                    if ((DateTime.Now - cacheInfo.LastWriteTime).TotalMinutes < CATALOG_DISK_CACHE_MINUTES)
                    {
                        // Read and parse on background thread
                        string diskJson = await System.Threading.Tasks.Task.Run(() => File.ReadAllText(CATALOG_DISK_CACHE));
                        var diskCatalog = JsonUtility.FromJson<CatalogResponse>(diskJson);
                        if (diskCatalog != null && diskCatalog.products != null && diskCatalog.products.Length > 0)
                        {
                            _cachedCatalog = diskCatalog;
                            _catalogCacheTime = cacheInfo.LastWriteTime;
                            return diskCatalog;
                        }
                    }
                }
                catch { }
            }
            
            try
            {
                
                using (var client = new WebClient())
                {
                    string url = $"{DOWNLOAD_API_URL}/products?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
                    
                    string response = await client.DownloadStringTaskAsync(url);
                    
                    if (string.IsNullOrEmpty(response))
                    {
                        // Debug.LogError("[SyntyStoreService] Empty response from catalog endpoint");
                        OnCatalogLoadFailed?.Invoke("Empty response from server");
                        return null;
                    }
                    
                    var catalog = JsonUtility.FromJson<CatalogResponse>(response);
                    
                    if (catalog == null)
                    {
                        // Debug.LogError("[SyntyStoreService] Failed to parse catalog JSON");
                        OnCatalogLoadFailed?.Invoke("Failed to parse catalog response");
                        return null;
                    }
                    
                    
                    if (catalog.success)
                    {
                        _cachedCatalog = catalog;
                        _catalogCacheTime = DateTime.Now;
                        
                        // Save to disk for instant restore after domain reload
                        try { File.WriteAllText(CATALOG_DISK_CACHE, response); } catch { }
                        
                        OnCatalogLoaded?.Invoke(catalog);
                        
                        return catalog;
                    }
                    else
                    {
                        string error = catalog.error ?? "Failed to load catalog";
                        // Debug.LogError($"[SyntyStoreService] Catalog error: {error}");
                        OnCatalogLoadFailed?.Invoke(error);
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                // Debug.LogError($"[SyntyStoreService] Catalog fetch failed: {ex.Message}\n{ex.StackTrace}");
                OnCatalogLoadFailed?.Invoke(ex.Message);
                return null;
            }
        }
        
        /// <summary>
        /// Get cached catalog without fetching
        /// </summary>
        public CatalogResponse GetCachedCatalog()
        {
            return _cachedCatalog;
        }
        
        /// <summary>
        /// Clear all catalog caches (memory + disk) to force a fresh server fetch.
        /// </summary>
        public void ClearCatalogCache()
        {
            _cachedCatalog = null;
            _catalogCacheTime = DateTime.MinValue;
            try
            {
                if (File.Exists(CATALOG_DISK_CACHE))
                    File.Delete(CATALOG_DISK_CACHE);
            }
            catch { }
        }
        
        /// <summary>
        /// Convert catalog products to AssetPackData list
        /// </summary>
        public List<AssetPackData> ConvertCatalogToAssetPacks(CatalogResponse catalog)
        {
            if (catalog?.products == null || catalog.products.Length == 0) 
            {
                return new List<AssetPackData>();
            }
            
            var packs = new List<AssetPackData>();
            int order = 1;
            int saleCount = 0;
            
            // Sort products by releaseDate descending (newest first) before assigning releaseOrder
            // Parse dates properly to handle different formats
            var sortedProducts = catalog.products
                .Where(p => p != null && p.hasDownload && !p.notDownloadable)
                .OrderByDescending(p => ParseReleaseDate(p.releaseDate))
                .ToArray();
            
            foreach (var product in sortedProducts)
            {
                // Parse __meta: entries from themes (encoded by admin page)
                string metaType = null;
                string metaStoreUrl = null;
                string metaIconUrl = null;
                var cleanThemes = new List<string>();
                
                if (product.themes != null)
                {
                    foreach (var theme in product.themes)
                    {
                        if (theme != null && theme.StartsWith("__meta:"))
                        {
                            try
                            {
                                string json = theme.Substring("__meta:".Length);
                                var meta = JsonUtility.FromJson<CatalogMeta>(json);
                                if (meta != null)
                                {
                                    metaType = meta.type;
                                    metaStoreUrl = meta.storeUrl;
                                    metaIconUrl = meta.icon;
                                }
                            }
                            catch { }
                        }
                        else if (theme != null)
                        {
                            cleanThemes.Add(theme);
                        }
                    }
                }
                
                // Use meta storeUrl if available, otherwise construct from handle
                string storeUrl = !string.IsNullOrEmpty(metaStoreUrl) 
                    ? metaStoreUrl 
                    : $"https://syntystore.com/products/{product.handle}";
                
                var pack = new AssetPackData
                {
                    packName = product.handle,
                    displayName = product.title,
                    imageUrl = product.image,
                    storeUrl = storeUrl,
                    releaseOrder = order++,
                    releaseDate = !string.IsNullOrEmpty(product.releaseDate) ? product.releaseDate : product.createdAt,
                    notOwned = false,
                    bucketFilename = product.filename,
                    version = product.version,
                    iconVersion = product.iconVersion
                };
                
                // Parse price
                if (!string.IsNullOrEmpty(product.price))
                {
                    float.TryParse(product.price, System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, out pack.price);
                }
                pack.currency = product.currency ?? "USD";
                
                // Detect sale: compareAtPrice is the original price, price is the sale price
                if (!string.IsNullOrEmpty(product.compareAtPrice))
                {
                    float compareAt = 0f;
                    float.TryParse(product.compareAtPrice, System.Globalization.NumberStyles.Float, 
                        System.Globalization.CultureInfo.InvariantCulture, out compareAt);
                    if (compareAt > pack.price)
                    {
                        pack.isOnSale = true;
                        pack.compareAtPrice = product.compareAtPrice;
                        saleCount++;
                    }
                }
                
                // Extract numeric variant ID from Shopify GID
                if (!string.IsNullOrEmpty(product.variantId))
                {
                    pack.shopifyVariantId = product.variantId.Contains("/") 
                        ? product.variantId.Substring(product.variantId.LastIndexOf('/') + 1) 
                        : product.variantId;
                }
                
                // Set category from meta type (admin page), catalog type field, or fall back to title guessing
                string effectiveType = metaType ?? product.type;
                if (!string.IsNullOrEmpty(effectiveType))
                {
                    switch (effectiveType.ToUpper())
                    {
                        case "POLYGON":   pack.category = PackCategory.Polygon; break;
                        case "SIDEKICK":  pack.category = PackCategory.Sidekicks; break;
                        case "INTERFACE": pack.category = PackCategory.Interface; break;
                        case "ANIMATION": pack.category = PackCategory.Animation; break;
                        case "SIMPLE":    pack.category = PackCategory.Simple; break;
                        default:          pack.category = PackCategory.Polygon; break;
                    }
                }
                else
                {
                    // Fall back to guessing from title
                    string titleLower = product.title?.ToLower() ?? "";
                    if (titleLower.Contains("polygon"))
                        pack.category = PackCategory.Polygon;
                    else if (titleLower.Contains("sidekick"))
                        pack.category = PackCategory.Sidekicks;
                    else if (titleLower.Contains("interface") || titleLower.Contains(" ui "))
                        pack.category = PackCategory.Interface;
                    else if (titleLower.Contains("animation"))
                        pack.category = PackCategory.Animation;
                    else if (titleLower.Contains("simple"))
                        pack.category = PackCategory.Simple;
                    else
                        pack.category = PackCategory.Polygon;
                }
                
                // Set themes from clean themes list (without __meta: entries)
                foreach (var theme in cleanThemes)
                {
                    string themeLower = theme.ToLower();
                    if (themeLower.Contains("sci-fi") || themeLower.Contains("scifi"))
                        pack.sciFi = true;
                    else if (themeLower.Contains("apocalypse"))
                        pack.apocalypse = true;
                    else if (themeLower.Contains("horror"))
                        pack.horror = true;
                    else if (themeLower.Contains("fantasy"))
                        pack.fantasy = true;
                    else if (themeLower.Contains("pirate"))
                        pack.pirates = true;
                    else if (themeLower.Contains("samurai"))
                        pack.samurai = true;
                    else if (themeLower.Contains("viking"))
                        pack.vikings = true;
                    else if (themeLower.Contains("modern") || themeLower.Contains("city") || themeLower.Contains("urban"))
                        pack.modern = true;
                    else if (themeLower.Contains("battle") || themeLower.Contains("war"))
                        pack.battle = true;
                    else if (themeLower.Contains("western") || themeLower.Contains("cowboy"))
                        pack.western = true;
                    else if (themeLower.Contains("biome") || themeLower.Contains("nature"))
                        pack.biomes = true;
                    else if (themeLower.Contains("ancient") || themeLower.Contains("medieval"))
                        pack.ancient = true;
                    else
                        pack.other = true;
                }
                
                // If no themes set, mark as other
                if (!pack.sciFi && !pack.apocalypse && !pack.horror && !pack.fantasy && 
                    !pack.pirates && !pack.samurai && !pack.vikings && !pack.modern && 
                    !pack.battle && !pack.western && !pack.biomes && !pack.ancient)
                {
                    pack.other = true;
                }
                
                packs.Add(pack);
            }
            
            
            return packs;
        }
        
        /// <summary>
        /// Parse a release date string into a DateTime for proper sorting
        /// Handles YYYY-MM-DD format and falls back to DateTime.MinValue for invalid/empty dates
        /// </summary>
        private static System.DateTime ParseReleaseDate(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr))
                return System.DateTime.MinValue;
            
            // Try YYYY-MM-DD format first (expected format from worker)
            if (System.DateTime.TryParseExact(dateStr, "yyyy-MM-dd", 
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, 
                out System.DateTime result))
            {
                return result;
            }
            
            // Try general parsing as fallback for other formats
            if (System.DateTime.TryParse(dateStr, out result))
            {
                return result;
            }
            
            // If all parsing fails, return MinValue (will sort to end/oldest)
            return System.DateTime.MinValue;
        }
        
        #endregion
        
        #region Download Management
        
        private Dictionary<string, DownloadOperation> _activeDownloads = new Dictionary<string, DownloadOperation>();
        
        private class DownloadOperation
        {
            public string productHandle;
            public string filePath;
            public bool isCancelled;
        }
        
        /// <summary>
        /// Download a product after verifying ownership via backend
        /// </summary>
        // True only if the file is a complete, valid Unity package: exists, is a sane size, matches
        // the expected length when known, and starts with the gzip magic bytes (0x1F 0x8B). Catches
        // truncated downloads and error bodies before they reach the importer.
        private static bool IsCompletePackage(string path, long expectedLen)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                long len = new FileInfo(path).Length;
                if (len < 1024) return false;
                if (expectedLen > 0 && len != expectedLen) return false;
                using (var fs = File.OpenRead(path))
                    return fs.ReadByte() == 0x1F && fs.ReadByte() == 0x8B;
            }
            catch { return false; }
        }

        public async Task<DownloadResult> DownloadProductAsync(string productHandle, string destinationFolder)
        {
            if (!IsAuthenticated)
            {
                return new DownloadResult { Success = false, Error = "Not logged in" };
            }
            
            if (string.IsNullOrEmpty(productHandle))
            {
                return new DownloadResult { Success = false, Error = "Invalid product handle" };
            }
            
            try
            {
                
                // Step 1: Call backend to verify ownership and get download URL
                var verifyResult = await VerifyAndGetDownloadUrl(productHandle);
                
                if (!verifyResult.success)
                {
                    string error = verifyResult.error ?? "Verification failed";
                    // Only show ownership error if the worker explicitly said not owned
                    if (verifyResult.error != null && verifyResult.error.Contains("All Access Pass"))
                    {
                        error = "This asset isn't in your library, purchase it at syntystore.com";
                    }
                    OnDownloadFailed?.Invoke(productHandle, error);
                    return new DownloadResult { Success = false, Error = error };
                }
                
                if (string.IsNullOrEmpty(verifyResult.download_url))
                {
                    OnDownloadFailed?.Invoke(productHandle, "No download URL returned");
                    return new DownloadResult { Success = false, Error = "No download URL returned" };
                }
                
                // Step 2: Download the file (or use cached version)
                string fileName = verifyResult.filename ?? $"{productHandle}.unitypackage";
                string destinationPath = Path.Combine(destinationFolder, fileName);
                
                Directory.CreateDirectory(destinationFolder);
                
                // Check if file already exists in cache (same filename = same version)
                if (File.Exists(destinationPath))
                {
                    if (IsCompletePackage(destinationPath, -1))
                    {
                        OnDownloadComplete?.Invoke(productHandle);
                        return new DownloadResult
                        {
                            Success = true,
                            FilePath = destinationPath,
                            FileName = fileName
                        };
                    }
                    // Corrupt/incomplete cached file — delete and re-download.
                    try { File.Delete(destinationPath); } catch { }
                }
                
                // Download to a temporary file and only promote it to the final cache path once it's
                // verified complete. This prevents a truncated/interrupted download (e.g. a domain
                // reload or dropped connection mid-write) from leaving a corrupt file at the final
                // path that would later fail import with "Couldn't decompress package".
                string tempPath = destinationPath + ".part";
                const int maxAttempts = 3;
                for (int attempt = 1; ; attempt++)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                    long expectedLen = -1;
                    bool stalled = false;
                    using (var client = new WebClient())
                    {
                        _activeDownloads[productHandle] = new DownloadOperation
                        {
                            productHandle = productHandle,
                            filePath = tempPath,
                            isCancelled = false
                        };

                        // Track the last time new bytes actually arrived, so we can detect a stalled
                        // connection (WebClient has no transfer timeout and will otherwise hang forever).
                        DateTime lastProgress = DateTime.UtcNow;
                        long lastBytes = 0;
                        client.DownloadProgressChanged += (s, e) =>
                        {
                            if (e.BytesReceived != lastBytes) { lastBytes = e.BytesReceived; lastProgress = DateTime.UtcNow; }
                            float progress = (float)e.ProgressPercentage / 100f;
                            OnDownloadProgress?.Invoke(productHandle, progress);
                        };

                        var downloadTask = client.DownloadFileTaskAsync(new Uri(verifyResult.download_url), tempPath);

                        // Stall watchdog: poll while downloading; if no new bytes for stallTimeoutSec,
                        // cancel so the loop below retries (or fails) instead of hanging the whole batch.
                        const double stallTimeoutSec = 90;
                        while (!downloadTask.IsCompleted)
                        {
                            var done = await Task.WhenAny(downloadTask, Task.Delay(2000));
                            if (done == downloadTask) break;
                            if ((DateTime.UtcNow - lastProgress).TotalSeconds > stallTimeoutSec)
                            {
                                stalled = true;
                                try { client.CancelAsync(); } catch { }
                                break;
                            }
                        }
                        try { await downloadTask; } catch { /* cancelled or errored — handled below */ }

                        bool wasCancelled = _activeDownloads.TryGetValue(productHandle, out var op) && op.isCancelled;
                        _activeDownloads.Remove(productHandle);
                        if (wasCancelled)
                        {
                            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                            return new DownloadResult { Success = false, Error = "Download cancelled" };
                        }

                        // The server's Content-Length lets us catch a silently-truncated download
                        // (connection closed early on a 200) that WebClient didn't throw on.
                        try
                        {
                            var cl = client.ResponseHeaders?["Content-Length"];
                            if (!string.IsNullOrEmpty(cl)) long.TryParse(cl, out expectedLen);
                        }
                        catch { }
                    }

                    // A stalled download is discarded and retried (or failed) rather than left hanging.
                    if (stalled)
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        if (attempt < maxAttempts)
                        {
                            Synty.Tools.SyntyLog.Warn($"Download for {productHandle} stalled (no data for 90s) — retrying ({attempt}/{maxAttempts}).");
                            continue;
                        }
                        OnDownloadFailed?.Invoke(productHandle, "Download stalled — no data received.");
                        return new DownloadResult { Success = false, Error = "Download stalled." };
                    }

                    if (IsCompletePackage(tempPath, expectedLen))
                    {
                        // Verified complete — promote temp to the final cache path.
                        try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { }
                        File.Move(tempPath, destinationPath);
                        break;
                    }

                    // Incomplete/corrupt download — discard and retry once before giving up.
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    if (attempt < maxAttempts)
                    {
                        Synty.Tools.SyntyLog.Warn($"Download for {productHandle} was incomplete — retrying ({attempt}/{maxAttempts}).");
                        continue;
                    }
                    OnDownloadFailed?.Invoke(productHandle, "Downloaded file was incomplete or corrupt.");
                    return new DownloadResult { Success = false, Error = "Downloaded file was incomplete or corrupt." };
                }

                OnDownloadComplete?.Invoke(productHandle);

                // Clean up old versions of the same product from cache
                try
                {
                    string baseName = productHandle.Replace("-", "_").ToLower();
                    foreach (var file in Directory.GetFiles(destinationFolder, "*.unitypackage"))
                    {
                        if (file == destinationPath) continue;
                        string fn = Path.GetFileName(file).ToLower();
                        // Match files with same base product name but different version
                        if (fn.Contains(baseName) || fn.StartsWith(baseName))
                        {
                            File.Delete(file);
                        }
                    }
                }
                catch { }

                return new DownloadResult
                {
                    Success = true,
                    FilePath = destinationPath,
                    FileName = fileName
                };
            }
            catch (Exception ex)
            {
                _activeDownloads.Remove(productHandle);
                
                string error = ex.Message;
                if (ex is WebException webEx && webEx.Response is HttpWebResponse response)
                {
                    if (response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        error = "Download access denied. Please try logging in again.";
                    }
                    else if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        error = "Download file not found.";
                    }
                }
                
                // Debug.LogError($"[SyntyStoreService] Download failed: {error}");
                OnDownloadFailed?.Invoke(productHandle, error);
                
                return new DownloadResult { Success = false, Error = error };
            }
        }
        
        /// <summary>
        /// Call backend to verify ownership and get download URL
        /// </summary>
        private async Task<DownloadVerifyResponse> VerifyAndGetDownloadUrl(string productHandle)
        {
            // Retry up to 3 times with 2-second delay for worker crashes (1102/503)
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var result = await VerifyAndGetDownloadUrlOnce(productHandle);
                
                // Success or definitive error (not a worker crash)
                if (result.success || (result.error?.Contains("1102") != true && result.error?.Contains("503") != true && result.error?.Contains("Service Unavailable") != true))
                    return result;
                
                if (attempt < 2) Synty.Tools.SyntyLog.Warn("Network error, retrying...");
                await Task.Delay(2000);
            }
            
            return new DownloadVerifyResponse { success = false, error = "Service temporarily unavailable. Please try again in a moment." };
        }
        
        private async Task<DownloadVerifyResponse> VerifyAndGetDownloadUrlOnce(string productHandle)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(DOWNLOAD_API_URL);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 15000; // 15 second timeout
                
                var requestData = new DownloadVerifyRequest
                {
                    customer_token = AccessToken,
                    session_token = SessionToken,
                    product_handle = productHandle
                };
                
                string jsonBody = JsonUtility.ToJson(requestData);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
                request.ContentLength = bodyBytes.Length;
                
                using (Stream requestStream = await request.GetRequestStreamAsync())
                {
                    await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
                }
                
                using (HttpWebResponse response = (HttpWebResponse)await request.GetResponseAsync())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    string responseBody = await reader.ReadToEndAsync();
                    
                    return JsonUtility.FromJson<DownloadVerifyResponse>(responseBody);
                }
            }
            catch (WebException webEx)
            {
                if (webEx.Response != null)
                {
                    using (StreamReader reader = new StreamReader(webEx.Response.GetResponseStream()))
                    {
                        string errorBody = reader.ReadToEnd();
                        // Debug.LogError($"[SyntyStoreService] Verify error: {errorBody}");
                        
                        try
                        {
                            return JsonUtility.FromJson<DownloadVerifyResponse>(errorBody);
                        }
                        catch { }
                        
                        // Return raw error text for retry detection
                        return new DownloadVerifyResponse { success = false, error = errorBody };
                    }
                }
                
                return new DownloadVerifyResponse { success = false, error = GetFriendlyErrorMessage(webEx) };
            }
            catch (Exception ex)
            {
                return new DownloadVerifyResponse { success = false, error = ex.Message };
            }
        }
        
        /// <summary>
        /// Cancel an active download
        /// </summary>
        public void CancelDownload(string productHandle)
        {
            if (_activeDownloads.TryGetValue(productHandle, out var operation))
            {
                operation.isCancelled = true;
            }
        }
        
        /// <summary>
        /// Check if a download is in progress
        /// </summary>
        public bool IsDownloading(string productHandle)
        {
            return _activeDownloads.ContainsKey(productHandle);
        }
        
        #endregion
        
        #region Browser Integration
        
        /// <summary>
        /// Open the Synty Store homepage
        /// </summary>
        public void OpenStorePage()
        {
            // Store-wide affiliate link (bg_ref + referral UTM) so general store visits are attributed
            // to the tool.
            Application.OpenURL("https://syntystore.com/?bg_ref=XCOW7ZDbbx&utm_source=Importer%20Synty&utm_medium=referral&utm_campaign=Synty%20Internal%20Program");
        }
        
        /// <summary>
        /// Open cart page
        /// </summary>
        public void OpenCartPage()
        {
            Application.OpenURL(CART_URL);
        }
        
        /// <summary>
        /// Open product page
        /// </summary>
        public void OpenProductPage(string handle)
        {
            if (!string.IsNullOrEmpty(handle))
            {
                Application.OpenURL($"{STORE_URL}/products/{handle}?utm_source=synty_passport&utm_medium=unity_editor&utm_campaign=passport_v2&utm_content=product_page");
            }
        }
        
        /// <summary>
        /// Open login page (account page)
        /// </summary>
        public void OpenLoginPage()
        {
            Application.OpenURL($"{STORE_URL}/account?utm_source=synty_passport&utm_medium=unity_editor&utm_campaign=passport_v2&utm_content=account");
        }
        
        #endregion
        
        #region Cart Integration
        
        /// <summary>
        /// Add product to cart via URL (opens in browser)
        /// </summary>
        public void AddToCart(string variantId)
        {
            if (string.IsNullOrEmpty(variantId))
            {
                return;
            }
            
            // Land the customer on /cart WITH the affiliate ref in the URL so BixGrow registers the
            // click. A plain cart/add?id=...&bg_ref=... loses the ref in Shopify's redirect to /cart,
            // so the click never tracks — carrying it through return_to fixes that.
            string returnTo = "/cart?bg_ref=XCOW7ZDbbx&utm_source=Importer Synty&utm_medium=referral&utm_campaign=Synty Internal Program";
            string cartUrl = $"{STORE_URL}/cart/add?id={variantId}&return_to={Uri.EscapeDataString(returnTo)}";
            Application.OpenURL(cartUrl);
        }
        
        /// <summary>
        /// Add multiple products to cart
        /// </summary>
        public void AddMultipleToCart(List<string> variantIds)
        {
            if (variantIds == null || variantIds.Count == 0) return;
            
            var items = string.Join(",", variantIds.ConvertAll(id => $"{id}:1"));
            string cartUrl = $"{STORE_URL}/cart/{items}?utm_source=synty_passport&utm_medium=unity_editor&utm_campaign=passport_v2&utm_content=add_to_cart_multi";
            Application.OpenURL(cartUrl);
        }
        
        #endregion
        
        #region Utility
        
        private string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            return str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }
        
        private string GetFriendlyErrorMessage(WebException webEx)
        {
            if (webEx.Response is HttpWebResponse response)
            {
                switch (response.StatusCode)
                {
                    case HttpStatusCode.NotFound:
                        return "Service not found. Please check configuration.";
                    case HttpStatusCode.Unauthorized:
                        return "Invalid credentials.";
                    case HttpStatusCode.Forbidden:
                        return "Access denied.";
                    case HttpStatusCode.InternalServerError:
                        return "Server error. Please try again later.";
                }
            }
            return webEx.Message;
        }
        
        /// <summary>
        /// Extract product handle from a store URL
        /// </summary>
        public static string ExtractHandleFromUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            
            try
            {
                var uri = new Uri(url);
                string path = uri.AbsolutePath;
                
                if (path.Contains("/products/"))
                {
                    int index = path.IndexOf("/products/") + "/products/".Length;
                    string handle = path.Substring(index);
                    
                    int queryIndex = handle.IndexOf('?');
                    if (queryIndex > 0) handle = handle.Substring(0, queryIndex);
                    
                    int fragmentIndex = handle.IndexOf('#');
                    if (fragmentIndex > 0) handle = handle.Substring(0, fragmentIndex);
                    
                    return handle.TrimEnd('/');
                }
            }
            catch { }
            
            if (url.Contains("/products/"))
            {
                int index = url.IndexOf("/products/") + "/products/".Length;
                string handle = url.Substring(index);
                int endIndex = handle.IndexOfAny(new char[] { '?', '#', '/' });
                if (endIndex > 0) handle = handle.Substring(0, endIndex);
                return handle;
            }
            
            return "";
        }
        
        /// <summary>
        /// Format file size for display
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            
            return $"{size:0.##} {sizes[order]}";
        }
        
        #endregion
        
        #region Tool Update
        
        /// <summary>
        /// Check if a tool update is available
        /// </summary>
        public async void CheckToolUpdate(string currentVersion, 
            Action<bool, string, string> onSuccess, 
            Action<string> onError,
            string channel = "live")
        {
            try
            {
                string url = $"{DOWNLOAD_API_URL}/tool-info?channel={channel}";
                
                var request = WebRequest.Create(url) as HttpWebRequest;
                request.Method = "GET";
                request.Timeout = 10000;
                
                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string json = await reader.ReadToEndAsync();
                    var toolInfo = JsonUtility.FromJson<ToolInfoResponse>(json);
                    
                    if (!toolInfo.success)
                    {
                        onError?.Invoke(toolInfo.error ?? "Unknown error");
                        return;
                    }
                    
                    // Update only when the server version is STRICTLY NEWER than what's installed.
                    // (Previously any difference counted, to allow rollbacks — but after the tool
                    // rename the package's internal version constant and its filename version can
                    // disagree, which made "different" always true and caused an infinite
                    // update/reload loop. Strictly-newer is loop-safe.)
                    bool hasUpdate = CompareVersions(toolInfo.version, currentVersion) > 0;
                    
                    onSuccess?.Invoke(hasUpdate, toolInfo.version, toolInfo.filename);
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }
        
        /// <summary>
        /// Compare two version strings (e.g., "1.0.35" vs "1.0.36")
        /// Returns: positive if v1 > v2, negative if v1 < v2, 0 if equal
        /// </summary>
        private int CompareVersions(string v1, string v2)
        {
            if (string.IsNullOrEmpty(v1)) return -1;
            if (string.IsNullOrEmpty(v2)) return 1;
            
            var parts1 = v1.Split('.');
            var parts2 = v2.Split('.');
            
            int maxLen = Math.Max(parts1.Length, parts2.Length);
            
            for (int i = 0; i < maxLen; i++)
            {
                int p1 = i < parts1.Length && int.TryParse(parts1[i], out int n1) ? n1 : 0;
                int p2 = i < parts2.Length && int.TryParse(parts2[i], out int n2) ? n2 : 0;
                
                if (p1 != p2) return p1 - p2;
            }
            
            return 0;
        }
        
        /// <summary>
        /// Get the download URL for the tool package
        /// </summary>
        public string GetToolDownloadUrl(string filename = null)
        {
            return string.IsNullOrEmpty(filename)
                ? $"{DOWNLOAD_API_URL}/tool-download"
                : $"{DOWNLOAD_API_URL}/tool-download?file={Uri.EscapeDataString(filename)}";
        }
        
        /// <summary>
        /// Download a file from a URL to a local path
        /// </summary>
        public async void DownloadFile(string url, string destinationPath,
            Action<float> onProgress,
            Action<string> onSuccess,
            Action<string> onError)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                
                using (var client = new WebClient())
                {
                    client.DownloadProgressChanged += (s, e) =>
                    {
                        float progress = (float)e.ProgressPercentage / 100f;
                        onProgress?.Invoke(progress);
                    };
                    
                    await client.DownloadFileTaskAsync(new Uri(url), destinationPath);
                    
                    onSuccess?.Invoke(destinationPath);
                }
            }
            catch (Exception ex)
            {
                onError?.Invoke(ex.Message);
            }
        }
        
        #endregion
    }
}
#endif
