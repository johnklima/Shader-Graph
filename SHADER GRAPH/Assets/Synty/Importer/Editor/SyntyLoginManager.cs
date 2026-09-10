using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;
using Synty.Passport.Compat;

namespace Synty.Tools.V2
{
    /// <summary>
    /// Handles login state, validation, and login UI for the Synty Asset Downloader V2.
    /// 
    /// Login is a two-step email OTP flow:
    ///   Step 1: user enters their email, we call /auth/request-code and the worker
    ///           emails them a 6-digit sign-in code.
    ///   Step 2: user enters the code, we call /auth/verify-code which mints the same
    ///           30-day session token the old single-step login produced.
    /// 
    /// Session persistence/restore is unchanged and handled by SyntyStoreService
    /// (EditorPrefs SessionToken) via SyntyStoreHelper.
    /// </summary>
    public static class SyntyLoginManager
    {
        // Cached UI refs
        private static Label _errorLabel;
        private static Button _primaryBtn;
        private static string _email = "";
        private static string _code = "";
        private static bool _rememberMe = true;
        
        // Two-step flow state. Step 1 = email entry, Step 2 = code entry.
        private static string _pendingEmail = "";
        private static double _lastCodeSentTime = 0; // EditorApplication.timeSinceStartup of last send, for resend cooldown
        private const double RESEND_COOLDOWN_SECONDS = 30;
        
        // Email history
        private const string PREF_EMAIL_HISTORY = "SyntyStore_EmailHistory";
        private const int MAX_EMAIL_HISTORY = 5;
        private static VisualElement _emailDropdown;
        
        // ─── Login UI Debug Settings ─────────────────────────────────────
        private static float _cardWidth = 350f;
        private static float _cardPadLeft = 32f;
        private static float _cardPadRight = 32f;
        private static float _cardPadTop = 40f;
        private static float _cardPadBottom = 32f;
        private static float _cardRadius = 12f;
        private static Color _cardBgColor = new Color(0.12f, 0.11f, 0.18f, 1f);
        private static Color _rootBgColor = new Color(0.094f, 0.078f, 0.145f, 1f);
        
        private static float _accentBarHeight = 71f;
        
        private static float _logoMarginTop = -32f;
        // Text logo (Synty_Passport_TextLogo) overlay, configured from the tool's debug panel.
        public static Texture2D TextLogo;
        public static float TextLogoWidth = 200f;
        public static float TextLogoHeight = 56.6f;
        public static float TextLogoX = 0f;
        public static float TextLogoY = 0f;
        public static float TextLogoScale = 1f;
        // Main downloader logo position/size on the card.
        public static float LogoX = 0f;
        public static float LogoY = 0f;
        public static float LogoScaleOverride = 1f;
        public static float LogoWidth = 200f;
        public static float LogoHeight = 56.6f;
        // Optional debug toggle button (only wired when the dev unlock script is present).
        public static bool ShowDebugButton = false;
        public static bool DebugPanelOn = false;
        public static System.Action OnDebugToggle = null;
        // Optional mini debug panel (built by the tool) shown beside the login card when debug is on.
        public static VisualElement DebugMiniPanel = null;
        private static float _logoMarginBottom = 8f;
        
        private static float _subtitleFontSize = 14f;
        private static float _subtitleOpacity = 0.75f;
        private static float _subtitleMarginBottom = 12.4f;
        private static float _subtitleMarginTop = 9f;
        private static float _subtitleMarginLeft = 0f;
        
        private static float _emailLabelFontSize = 14f;
        private static float _emailLabelOpacity = 0.75f;
        
        private static float _btnHeight = 39.6f;
        private static float _btnFontSize = 14f;
        private static float _btnRadius = 5f;
        private static float _btnMarginTop = 5.4f;
        private static float _btnMarginBottom = 17.2f;
        
        private static float _createAccFontSize = 12f;
        
        private static float _rememberFontSize = 12f;
        private static float _rememberOpacity = 0.5f;
        private static float _rememberMarginBottom = 2.6f;
        private static float _rememberMarginTop = -10f;
        private static float _rememberMarginLeft = 1.8f;
        

        // ─── State Queries ───────────────────────────────────────────────
        
        /// <summary>
        /// Check if user is logged in via SyntyStoreHelper (checks Shopify token)
        /// </summary>
        public static bool IsLoggedIn => SyntyStoreHelper.IsLoggedIn;
        
        /// <summary>
        /// Get logged in user's email
        /// </summary>
        public static string Email => SyntyStoreHelper.LoggedInEmail;
        
        /// <summary>
        /// Whether the logged in user has the Synty Pass (All Access)
        /// </summary>
        public static bool HasSyntyPass => SyntyStoreHelper.HasSyntyPass;
        
        // ─── State Mutations ─────────────────────────────────────────────
        
        /// <summary>
        /// Logout via SyntyStoreHelper (clears Shopify token and session)
        /// </summary>
        public static void Logout()
        {
            SyntyStoreHelper.Logout();
        }
        
        /// <summary>
        /// Try to restore a saved session. Verifies the token is still valid with the backend.
        /// </summary>
        public static void TryRestoreSession(System.Action onSuccess, System.Action onNoSession)
        {
            SyntyStoreHelper.TryRestoreSession(onSuccess, onNoSession);
        }
        
        // ─── Login UI ────────────────────────────────────────────────────
        
        /// <summary>
        /// Builds the login screen UI into the given root element. Starts at the email
        /// step; after a code is successfully requested the card swaps to the code step.
        /// </summary>
        public static void BuildLoginUI(
            VisualElement root,
            Texture2D logoTexture,
            Font titleFont,
            Font bodyFont,
            Color accentColor,
            System.Action onLoginSuccess)
        {
            _email = Email; // load saved email if any
            _code = "";

            _emailDropdown = null; // clear stale reference from previous login screen
            root.style.flexGrow = 1;
            root.style.flexDirection = FlexDirection.Row;
            
            
            // Main login content area
            var loginContent = new VisualElement();
            loginContent.style.flexGrow = 1;
            loginContent.style.backgroundColor = _rootBgColor;
            loginContent.style.flexDirection = FlexDirection.Column;
            loginContent.style.alignItems = Align.Center;
            loginContent.style.justifyContent = Justify.Center;
            
            // Centre card
            var card = new VisualElement();
            card.style.width = _cardWidth;
            card.style.paddingLeft = _cardPadLeft;
            card.style.paddingRight = _cardPadRight;
            card.style.paddingTop = _cardPadTop;
            card.style.paddingBottom = _cardPadBottom;
            card.style.backgroundColor = _cardBgColor;
            card.style.borderTopLeftRadius = _cardRadius;
            card.style.borderTopRightRadius = _cardRadius;
            card.style.borderBottomLeftRadius = _cardRadius;
            card.style.borderBottomRightRadius = _cardRadius;
            card.style.alignItems = Align.Center;
            loginContent.Add(card);
            
            // Orange accent bar at top of card (behind logo)
            if (logoTexture != null)
            {
                var accentBar = new VisualElement();
                accentBar.name = "login-accent-bar";
                accentBar.style.position = Position.Absolute;
                accentBar.style.top = 0;
                accentBar.style.left = 0;
                accentBar.style.right = 0;
                accentBar.style.height = _accentBarHeight;
                // Use the corner background texture instead of a flat accent colour. Falls back to
                // the accent colour if the texture can't be loaded.
                var cornerTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    "Assets/Synty/Importer/Editor/Images/Logo/Login_Background.png");
                if (cornerTex != null)
                {
                    accentBar.style.backgroundImage = new StyleBackground(cornerTex);
                    // unityBackgroundScaleMode is deprecated in Unity 6 in favour of background-size,
                    // but that property doesn't exist on the older Unity versions this tool still
                    // supports (2021.3+). Keep using it and suppress the obsolete warning.
#pragma warning disable 618
                    accentBar.style.unityBackgroundScaleMode = ScaleMode.ScaleAndCrop;
#pragma warning restore 618
                }
                else
                {
                    accentBar.style.backgroundColor = accentColor;
                }
                accentBar.style.borderTopLeftRadius = _cardRadius;
                accentBar.style.borderTopRightRadius = _cardRadius;
                accentBar.pickingMode = PickingMode.Ignore;
                card.Add(accentBar);
                
                // Logo (on top of bar, pulled up into orange area)
                var logo = new VisualElement();
                logo.name = "login-main-logo";
                logo.style.width = LogoWidth;
                logo.style.height = LogoHeight;
                logo.style.marginTop = _logoMarginTop;
                logo.style.marginBottom = _logoMarginBottom;
                logo.style.translate = new Translate(new Length(LogoX), new Length(LogoY));
                logo.style.scale = new Scale(new Vector3(LogoScaleOverride, LogoScaleOverride, 1f));
                logo.style.backgroundImage = new StyleBackground(logoTexture);
                logo.style.SetBackgroundScaleToFit();
                card.Add(logo);
            }
            // Text logo overlay (Synty_Importer_TextLogo), positioned/scaled from debug settings.
            if (TextLogo != null)
            {
                var textLogo = new VisualElement();
                textLogo.name = "login-text-logo";
                textLogo.style.position = Position.Absolute;
                textLogo.style.width = TextLogoWidth;
                textLogo.style.height = TextLogoHeight;
                textLogo.style.left = Length.Percent(50);
                textLogo.style.top = 0;
                // Explicitly pin the transform pivot to the element's center. The scale below (1.18)
                // pivots around transformOrigin, and the DEFAULT origin resolves differently between
                // Unity 2021.3 and Unity 6 — which shifted the scaled logo on Unity 6. Setting it
                // explicitly makes both versions scale around the same point.
                textLogo.style.transformOrigin = new TransformOrigin(Length.Percent(50), Length.Percent(50));
                // Unity 6 positions this text logo 30px further right than Unity 2021.3; nudge it
                // back left on newer Unity only. 2021.3 is left untouched.
                float textLogoU6X = 0f;
#if UNITY_2022_2_OR_NEWER
                textLogoU6X = -30f;
#endif
                textLogo.style.translate = new Translate(
                    new Length(-TextLogoWidth / 2f + TextLogoX + textLogoU6X), new Length(TextLogoY));
                textLogo.style.scale = new Scale(new Vector3(TextLogoScale, TextLogoScale, 1f));
                textLogo.style.backgroundImage = new StyleBackground(TextLogo);
                textLogo.style.SetBackgroundScaleToFit();
                textLogo.pickingMode = PickingMode.Ignore;
                card.Add(textLogo);
            }
            
            // Step container - everything below the logo lives in here so the email and
            // code steps can swap without rebuilding the whole card.
            var stepContainer = new VisualElement();
            stepContainer.style.width = Length.Percent(100);
            stepContainer.style.alignItems = Align.Center;
            card.Add(stepContainer);
            
            BuildEmailStep(stepContainer, root, titleFont, bodyFont, accentColor, onLoginSuccess);
            
            root.Add(loginContent);
            // Mount the optional mini debug panel beside the login content (dev/QA only).
            if (ShowDebugButton && DebugPanelOn && DebugMiniPanel != null)
            {
                root.Add(DebugMiniPanel);
            }
        }
        
        // ─── Step 1: email entry ─────────────────────────────────────────
        
        private static void BuildEmailStep(
            VisualElement stepContainer,
            VisualElement root,
            Font titleFont,
            Font bodyFont,
            Color accentColor,
            System.Action onLoginSuccess)
        {
            stepContainer.Clear();
            HideEmailDropdown();
            
            // Subtitle
            var subtitle = new Label("Sign in with your Synty Store email");
            subtitle.style.color = new Color(1f, 1f, 1f, _subtitleOpacity);
            subtitle.style.fontSize = _subtitleFontSize;
            subtitle.style.marginBottom = _subtitleMarginBottom;
            subtitle.style.marginTop = _subtitleMarginTop;
            subtitle.style.marginLeft = _subtitleMarginLeft;
            subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            if (bodyFont != null) subtitle.style.unityFontDefinition = FontDefinition.FromFont(bodyFont);
            stepContainer.Add(subtitle);
            
            // Email label
            var emailLabel = new Label("Email");
            emailLabel.style.color = new Color(1f, 1f, 1f, _emailLabelOpacity);
            emailLabel.style.fontSize = _emailLabelFontSize;
            emailLabel.style.marginBottom = 4;
            emailLabel.style.alignSelf = Align.FlexStart;
            if (titleFont != null) emailLabel.style.unityFontDefinition = FontDefinition.FromFont(titleFont);
            stepContainer.Add(emailLabel);
            
            // Email field wrapper (for absolute-positioned dropdown overlay)
            var emailWrapper = new VisualElement();
            emailWrapper.style.width = Length.Percent(100);
            emailWrapper.style.marginBottom = 16;
            emailWrapper.style.overflow = Overflow.Visible;
            
            var emailField = new TextField();
            emailField.value = _email;
            emailField.style.width = Length.Percent(100);
            StyleTextField(emailField);
            emailField.RegisterValueChangedCallback(evt => {
                _email = evt.newValue;
                UpdateSendButtonEnabledLook(accentColor);
            });
            
            // Show dropdown on click (toggle) - use TrickleDown to catch before TextField consumes
            emailField.RegisterCallback<PointerDownEvent>(_ => {
                // Clear stale reference if dropdown was removed from tree (e.g. root.Clear)
                if (_emailDropdown != null && _emailDropdown.panel == null)
                    _emailDropdown = null;
                
                if (_emailDropdown != null)
                {
                    HideEmailDropdown();
                }
                else
                {
                    ShowEmailDropdown(emailField, root, (selected) => {
                        _email = selected;
                        emailField.SetValueWithoutNotify(selected);                        UpdateSendButtonEnabledLook(accentColor);
                        HideEmailDropdown();
                    });
                }
            }, TrickleDown.TrickleDown);
            
            // Also show on focus (keyboard tab, first click after login screen rebuild)
            emailField.RegisterCallback<FocusInEvent>(_ => {
                if (_emailDropdown != null && _emailDropdown.panel == null)
                    _emailDropdown = null;
                if (_emailDropdown == null)
                {
                    ShowEmailDropdown(emailField, root, (selected) => {
                        _email = selected;
                        emailField.SetValueWithoutNotify(selected);                        UpdateSendButtonEnabledLook(accentColor);
                        HideEmailDropdown();
                    });
                }
            });
            emailWrapper.Add(emailField);
            stepContainer.Add(emailWrapper);
            
            // Click anywhere (except dropdown/email) dismisses dropdown
            root.RegisterCallback<PointerDownEvent>(evt => {
                if (_emailDropdown == null) return;
                var target = evt.target as VisualElement;
                while (target != null)
                {
                    if (target == _emailDropdown || target == emailField) return;
                    target = target.parent;
                }
                HideEmailDropdown();
            });
            
            // Remember me toggle
            var rememberRow = new VisualElement();
            rememberRow.style.flexDirection = FlexDirection.Row;
            rememberRow.style.alignItems = Align.Center;
            rememberRow.style.alignSelf = Align.FlexStart;
            rememberRow.style.marginBottom = _rememberMarginBottom;
            rememberRow.style.marginTop = _rememberMarginTop;
            rememberRow.style.marginLeft = _rememberMarginLeft;
            
            var rememberToggle = new Toggle();
            rememberToggle.value = _rememberMe;
            rememberToggle.RegisterValueChangedCallback(evt => _rememberMe = evt.newValue);
            rememberRow.Add(rememberToggle);
            
            var rememberLabel = new Label("Remember me");
            rememberLabel.style.color = new Color(1f, 1f, 1f, _rememberOpacity);
            rememberLabel.style.fontSize = _rememberFontSize;
            rememberLabel.style.marginLeft = 4;
            rememberRow.Add(rememberLabel);
            
            stepContainer.Add(rememberRow);
            
            // Error label (hidden by default)
            _errorLabel = new Label();
            _errorLabel.style.color = new Color(1f, 0.3f, 0.3f, 1f);
            _errorLabel.style.fontSize = 11;
            _errorLabel.style.marginBottom = 8;
            _errorLabel.style.display = DisplayStyle.None;
            _errorLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _errorLabel.style.whiteSpace = WhiteSpace.Normal;
            _errorLabel.style.width = Length.Percent(100);
            stepContainer.Add(_errorLabel);
            
            // Send Code button (primary action for step 1)
            _primaryBtn = new Button();
            _primaryBtn.text = "Send Code";
            StylePrimaryButton(_primaryBtn, accentColor);
            // Grey out the Send Code button until an email has been entered.
            UpdateSendButtonEnabledLook(accentColor);
            
            var capturedCallback = onLoginSuccess;
            System.Action submitEmail = () =>
            {
                if (string.IsNullOrEmpty(_email))
                {
                    ShowError("Please enter your email address.");
                    return;
                }
                if (!_email.Contains("@"))
                {
                    ShowError("Please enter a valid email address.");
                    return;
                }
                
                _primaryBtn.text = "Sending code...";
                _primaryBtn.SetEnabled(false);
                _errorLabel.style.display = DisplayStyle.None;
                
                RequestCodeAsync(_email.Trim(), stepContainer, root, titleFont, bodyFont, accentColor, capturedCallback);
            };
            
            _primaryBtn.clicked += submitEmail;
            // Enter key in the email field submits too
            emailField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    submitEmail();
                }
            });
            stepContainer.Add(_primaryBtn);
            
            // Divider
            var divider = new VisualElement();
            divider.style.height = 1;
            divider.style.width = Length.Percent(100);
            divider.style.backgroundColor = new Color(1f, 1f, 1f, 0.1f);
            divider.style.marginBottom = 16;
            stepContainer.Add(divider);
            
            // Create account row
            var createRow = new VisualElement();
            createRow.style.flexDirection = FlexDirection.Row;
            createRow.style.justifyContent = Justify.Center;
            createRow.style.alignItems = Align.Center;
            
            var noAccountLabel = new Label("Don't have an account?");
            noAccountLabel.style.color = new Color(1f, 1f, 1f, 0.4f);
            noAccountLabel.style.fontSize = _createAccFontSize;
            createRow.Add(noAccountLabel);
            
            var createBtn = new Button();
            createBtn.text = "Create Account";
            createBtn.style.color = accentColor;
            createBtn.style.fontSize = _createAccFontSize;
            createBtn.style.backgroundColor = Color.clear;
            createBtn.style.borderTopWidth = 0;
            createBtn.style.borderBottomWidth = 0;
            createBtn.style.borderLeftWidth = 0;
            createBtn.style.borderRightWidth = 0;
            createBtn.style.paddingLeft = 4;
            createBtn.style.paddingRight = 0;
            createBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            // Show the link/pointer cursor on hover so it reads as clickable. UI Toolkit's
            // built-in "link" cursor is exposed through the USS `cursor: link` keyword; we
            // apply it by adding a class that carries that rule (defined in the inline style
            // sheet attached to the login root). Falls back gracefully if the sheet is absent.
            createBtn.AddToClassList("link-cursor");
            createBtn.clicked += () =>
            {
                Application.OpenURL("https://syntystore.com/account/register");
            };
            createRow.Add(createBtn);
            
            stepContainer.Add(createRow);
            
            // Privacy policy link at the bottom of the sign-in section.
            var analyticsNotice = new Label("We collect anonymous usage analytics to improve the tool. You can turn this off or delete your data anytime from your profile.");
            analyticsNotice.style.color = new Color(1f, 1f, 1f, 0.35f);
            analyticsNotice.style.fontSize = Mathf.Max(9f, _createAccFontSize - 1f);
            analyticsNotice.style.whiteSpace = WhiteSpace.Normal;
            analyticsNotice.style.unityTextAlign = TextAnchor.MiddleCenter;
            analyticsNotice.style.marginTop = 10;
            analyticsNotice.style.marginLeft = 8;
            analyticsNotice.style.marginRight = 8;
            stepContainer.Add(analyticsNotice);

            var privacyLink = new Button();
            privacyLink.text = "Privacy Policy";
            privacyLink.style.color = new Color(1f, 1f, 1f, 0.4f);
            privacyLink.style.fontSize = _createAccFontSize;
            privacyLink.style.backgroundColor = Color.clear;
            privacyLink.style.borderTopWidth = 0;
            privacyLink.style.borderBottomWidth = 0;
            privacyLink.style.borderLeftWidth = 0;
            privacyLink.style.borderRightWidth = 0;
            privacyLink.style.marginTop = 8;
            privacyLink.style.alignSelf = Align.Center;
            privacyLink.AddToClassList("link-cursor");
            privacyLink.clicked += () =>
            {
                Application.OpenURL("https://syntystore.com/pages/privacy-policy");
            };
            stepContainer.Add(privacyLink);

            // Debug toggle (dev/QA only): shows when the dev unlock script is present. Toggles the
            // debug options panel so it's available immediately after login.
            if (ShowDebugButton)
            {
                var debugBtn = new Button();
                debugBtn.text = DebugPanelOn ? "Debug Panel: On" : "Debug Panel: Off";
                debugBtn.style.marginTop = 10;
                debugBtn.style.height = 24;
                debugBtn.style.fontSize = _createAccFontSize;
                debugBtn.style.alignSelf = Align.Center;
                debugBtn.style.paddingLeft = 12;
                debugBtn.style.paddingRight = 12;
                debugBtn.style.color = Color.white;
                debugBtn.style.backgroundColor = DebugPanelOn
                    ? new Color(0.18f, 0.55f, 0.30f, 1f)   // green when on
                    : new Color(0.255f, 0.255f, 0.388f, 1f); // #414163 when off
                debugBtn.style.borderTopLeftRadius = 6;
                debugBtn.style.borderTopRightRadius = 6;
                debugBtn.style.borderBottomLeftRadius = 6;
                debugBtn.style.borderBottomRightRadius = 6;
                debugBtn.style.borderTopWidth = 0;
                debugBtn.style.borderBottomWidth = 0;
                debugBtn.style.borderLeftWidth = 0;
                debugBtn.style.borderRightWidth = 0;
                debugBtn.AddToClassList("link-cursor");
                debugBtn.clicked += () =>
                {
                    DebugPanelOn = !DebugPanelOn;
                    debugBtn.text = DebugPanelOn ? "Debug Panel: On" : "Debug Panel: Off";
                    debugBtn.style.backgroundColor = DebugPanelOn
                        ? new Color(0.18f, 0.55f, 0.30f, 1f)
                        : new Color(0.255f, 0.255f, 0.388f, 1f);
                    OnDebugToggle?.Invoke();
                };
                stepContainer.Add(debugBtn);
            }
        }
        
        // Calls /auth/request-code and on acceptance swaps the card to the code step.
        // The worker answers generically whether or not the email matched a customer
        // (anti-enumeration), so we always advance to the code step on success - a
        // non-customer simply never receives a code.
        private static async void RequestCodeAsync(
            string email,
            VisualElement stepContainer,
            VisualElement root,
            Font titleFont,
            Font bodyFont,
            Color accentColor,
            System.Action onLoginSuccess)
        {
            try
            {
                var result = await SyntyStoreService.Instance.RequestLoginCodeAsync(email);
                
                if (result.Success)
                {
                    _pendingEmail = email;
                    _lastCodeSentTime = EditorApplication.timeSinceStartup;
                    SaveEmailToHistory(email);
                    _code = "";
                    Synty.Tools.SyntyLog.Info($"Sign-in code sent to {email}");
                    BuildCodeStep(stepContainer, root, titleFont, bodyFont, accentColor, onLoginSuccess);
                }
                else
                {
                    if (_primaryBtn != null)
                    {
                        _primaryBtn.text = "Send Code";
                        _primaryBtn.SetEnabled(true);
                    }
                    ShowError(result.Error ?? "Could not send a sign-in code. Please try again.");
                }
            }
            catch (System.Exception)
            {
                if (_primaryBtn != null)
                {
                    _primaryBtn.text = "Send Code";
                    _primaryBtn.SetEnabled(true);
                }
                ShowError("Could not send a sign-in code. Please try again.");
                // Debug.LogError($"[SyntyLoginManager] Request code failed: {ex.Message}");
            }
        }
        
        // ─── Step 2: code entry ──────────────────────────────────────────
        
        private static void BuildCodeStep(
            VisualElement stepContainer,
            VisualElement root,
            Font titleFont,
            Font bodyFont,
            Color accentColor,
            System.Action onLoginSuccess)
        {
            stepContainer.Clear();
            HideEmailDropdown();
            
            // Subtitle - tells the user where the code went
            var subtitle = new Label($"Enter the 6-digit code sent to\n{_pendingEmail}");
            subtitle.style.color = new Color(1f, 1f, 1f, _subtitleOpacity);
            subtitle.style.fontSize = _subtitleFontSize;
            subtitle.style.marginBottom = _subtitleMarginBottom;
            subtitle.style.marginTop = _subtitleMarginTop;
            subtitle.style.marginLeft = _subtitleMarginLeft;
            subtitle.style.unityTextAlign = TextAnchor.MiddleCenter;
            subtitle.style.whiteSpace = WhiteSpace.Normal;
            // No font override - uses Unity's default editor font, matching the
            // "Use a different email" link below.
            stepContainer.Add(subtitle);
            
            // Code label
            var codeLabel = new Label("Sign-in code");
            codeLabel.style.color = new Color(1f, 1f, 1f, _emailLabelOpacity);
            codeLabel.style.fontSize = _emailLabelFontSize;
            codeLabel.style.marginBottom = 4;
            codeLabel.style.alignSelf = Align.FlexStart;
            // No font override - matches the default-font links.
            stepContainer.Add(codeLabel);
            
            // Code field - digits only, max 6, centred large text for readability
            var codeField = new TextField();
            codeField.maxLength = 6;
            codeField.value = _code;
            codeField.style.width = Length.Percent(100);
            codeField.style.marginBottom = 16;
            StyleTextField(codeField);
            codeField.RegisterValueChangedCallback(evt =>
            {
                // Strip non-digits as the user types (e.g. pasted with spaces)
                string digits = System.Text.RegularExpressions.Regex.Replace(evt.newValue ?? "", "[^0-9]", "");
                if (digits != evt.newValue) codeField.SetValueWithoutNotify(digits);
                _code = digits;
            });
            // Centre + enlarge the inner input text once it exists
            codeField.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var input = codeField.Q<VisualElement>("unity-text-input");
                if (input != null)
                {
                    input.style.unityTextAlign = TextAnchor.MiddleCenter;
                    input.style.fontSize = 20;
                    input.style.letterSpacing = 8;
                }
            });
            stepContainer.Add(codeField);
            
            // Error label
            _errorLabel = new Label();
            _errorLabel.style.color = new Color(1f, 0.3f, 0.3f, 1f);
            _errorLabel.style.fontSize = 11;
            _errorLabel.style.marginBottom = 8;
            _errorLabel.style.display = DisplayStyle.None;
            _errorLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _errorLabel.style.whiteSpace = WhiteSpace.Normal;
            _errorLabel.style.width = Length.Percent(100);
            stepContainer.Add(_errorLabel);
            
            // Verify button (primary action for step 2)
            _primaryBtn = new Button();
            _primaryBtn.text = "Verify and Sign In";
            StylePrimaryButton(_primaryBtn, accentColor);
            
            var capturedCallback = onLoginSuccess;
            System.Action submitCode = () =>
            {
                if (string.IsNullOrEmpty(_code) || _code.Length != 6)
                {
                    ShowError("Please enter the 6-digit code from your email.");
                    return;
                }
                
                _primaryBtn.text = "Verifying...";
                _primaryBtn.SetEnabled(false);
                _errorLabel.style.display = DisplayStyle.None;
                
                VerifyCodeAsync(_pendingEmail, _code, capturedCallback);
            };
            
            _primaryBtn.clicked += submitCode;
            codeField.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                {
                    evt.StopPropagation();
                    submitCode();
                }
            });
            stepContainer.Add(_primaryBtn);
            
            // Resend + back row
            var actionsRow = new VisualElement();
            actionsRow.style.flexDirection = FlexDirection.Row;
            actionsRow.style.justifyContent = Justify.Center;
            actionsRow.style.alignItems = Align.Center;
            actionsRow.style.marginBottom = 8;
            
            var resendBtn = new Button();
            resendBtn.text = "Resend code";
            resendBtn.style.color = accentColor;
            resendBtn.style.fontSize = _createAccFontSize;
            resendBtn.style.backgroundColor = Color.clear;
            resendBtn.style.borderTopWidth = 0;
            resendBtn.style.borderBottomWidth = 0;
            resendBtn.style.borderLeftWidth = 0;
            resendBtn.style.borderRightWidth = 0;
            resendBtn.style.unityFontStyleAndWeight = FontStyle.Bold;
            resendBtn.clicked += () =>
            {
                // Local cooldown so the button cannot hammer the (already rate limited)
                // endpoint or spam the user's inbox from impatient clicking.
                double sinceLast = EditorApplication.timeSinceStartup - _lastCodeSentTime;
                if (sinceLast < RESEND_COOLDOWN_SECONDS)
                {
                    ShowError($"Please wait {(int)(RESEND_COOLDOWN_SECONDS - sinceLast)}s before requesting another code.");
                    return;
                }
                resendBtn.SetEnabled(false);
                resendBtn.text = "Sending...";
                ResendCodeAsync(resendBtn);
            };
            actionsRow.Add(resendBtn);
            
            var sepLabel = new Label("|");
            sepLabel.style.color = new Color(1f, 1f, 1f, 0.25f);
            sepLabel.style.fontSize = _createAccFontSize;
            sepLabel.style.marginLeft = 6;
            sepLabel.style.marginRight = 6;
            actionsRow.Add(sepLabel);
            
            var backBtn = new Button();
            backBtn.text = "Use a different email";
            backBtn.style.color = new Color(1f, 1f, 1f, 0.55f);
            backBtn.style.fontSize = _createAccFontSize;
            backBtn.style.backgroundColor = Color.clear;
            backBtn.style.borderTopWidth = 0;
            backBtn.style.borderBottomWidth = 0;
            backBtn.style.borderLeftWidth = 0;
            backBtn.style.borderRightWidth = 0;
            backBtn.clicked += () =>
            {
                _code = "";
                BuildEmailStep(stepContainer, root, titleFont, bodyFont, accentColor, capturedCallback);
            };
            actionsRow.Add(backBtn);
            
            stepContainer.Add(actionsRow);
            
            // Hint about spam folders - small, dim, harmless
            var hint = new Label("Code not arriving? Check your spam folder.");
            hint.style.color = new Color(1f, 1f, 1f, 0.3f);
            hint.style.fontSize = 10;
            hint.style.unityTextAlign = TextAnchor.MiddleCenter;
            stepContainer.Add(hint);
        }
        
        private static async void VerifyCodeAsync(string email, string code, System.Action onLoginSuccess)
        {
            try
            {
                var result = await SyntyStoreService.Instance.VerifyLoginCodeAsync(email, code, _rememberMe);
                
                if (result.Success)
                {
                    Synty.Tools.SyntyLog.Info($"Signed in successfully as {email}");
                    onLoginSuccess?.Invoke();
                }
                else
                {
                    if (_primaryBtn != null)
                    {
                        _primaryBtn.text = "Verify and Sign In";
                        _primaryBtn.SetEnabled(true);
                    }
                    ShowError(result.Error ?? "Sign-in failed. Please try again.");
                }
            }
            catch (System.Exception)
            {
                if (_primaryBtn != null)
                {
                    _primaryBtn.text = "Verify and Sign In";
                    _primaryBtn.SetEnabled(true);
                }
                ShowError("Sign-in failed. Please try again.");
                // Debug.LogError($"[SyntyLoginManager] Verify code failed: {ex.Message}");
            }
        }
        
        private static async void ResendCodeAsync(Button resendBtn)
        {
            try
            {
                var result = await SyntyStoreService.Instance.RequestLoginCodeAsync(_pendingEmail);
                if (result.Success)
                {
                    _lastCodeSentTime = EditorApplication.timeSinceStartup;
                    ShowError("");
                    if (_errorLabel != null)
                    {
                        _errorLabel.text = "A new code has been sent.";
                        _errorLabel.style.color = new Color(0.4f, 0.9f, 0.5f, 1f);
                        _errorLabel.style.display = DisplayStyle.Flex;
                    }
                }
                else
                {
                    ShowError(result.Error ?? "Could not resend the code. Please try again.");
                }
            }
            catch (System.Exception)
            {
                ShowError("Could not resend the code. Please try again.");
                // Debug.LogError($"[SyntyLoginManager] Resend code failed: {ex.Message}");
            }
            finally
            {
                if (resendBtn != null)
                {
                    resendBtn.text = "Resend code";
                    resendBtn.SetEnabled(true);
                }
            }
        }
        
        // ─── Helpers ─────────────────────────────────────────────────────
        
        private static void ShowError(string message)
        {
            if (_errorLabel != null)
            {
                _errorLabel.style.color = new Color(1f, 0.3f, 0.3f, 1f);
                _errorLabel.text = message;
                _errorLabel.style.display = string.IsNullOrEmpty(message) ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }
        
        // Shared styling for the step's primary action button (Send Code / Verify).
        // Greys out the Send Code button (#404061) when the email field is empty and
        // restores the accent color once an address is entered, so it reads as inactive
        // until there is something to submit. Text color also dims when greyed.
        private static readonly Color _sendDisabledColor = new Color(64f/255f, 64f/255f, 97f/255f, 1f); // #404061
        private static void UpdateSendButtonEnabledLook(Color accentColor)
        {
            if (_primaryBtn == null) return;
            bool hasEmail = !string.IsNullOrEmpty(_email) && _email.Trim().Length > 0;
            _primaryBtn.style.backgroundColor = hasEmail ? accentColor : _sendDisabledColor;
            _primaryBtn.style.color = hasEmail ? Color.black : new Color(1f, 1f, 1f, 0.5f);
        }
        
        private static void StylePrimaryButton(Button btn, Color accentColor)
        {
            btn.style.width = Length.Percent(100);
            btn.style.height = _btnHeight;
            btn.style.marginTop = _btnMarginTop;
            btn.style.marginBottom = _btnMarginBottom;
            btn.style.backgroundColor = accentColor;
            btn.style.color = Color.black;
            btn.style.fontSize = _btnFontSize;
            btn.style.unityFontStyleAndWeight = FontStyle.Bold;
            btn.style.borderTopWidth = 1;
            btn.style.borderBottomWidth = 1;
            btn.style.borderLeftWidth = 1;
            btn.style.borderRightWidth = 1;
            // Borders start transparent so the default look is unchanged; on hover they turn
            // white to give a clear interactive outline. Keeping the width constant (1px) at
            // rest avoids any layout shift when the color appears on hover.
            var transparentBorder = new Color(1f, 1f, 1f, 0f);
            var hoverBorder = new Color(1f, 1f, 1f, 0.9f);
            btn.style.borderTopColor = transparentBorder;
            btn.style.borderBottomColor = transparentBorder;
            btn.style.borderLeftColor = transparentBorder;
            btn.style.borderRightColor = transparentBorder;
            btn.style.borderTopLeftRadius = _btnRadius;
            btn.style.borderTopRightRadius = _btnRadius;
            btn.style.borderBottomLeftRadius = _btnRadius;
            btn.style.borderBottomRightRadius = _btnRadius;
            btn.style.unityTextAlign = TextAnchor.MiddleCenter;
            btn.AddToClassList("link-cursor");
            btn.RegisterCallback<MouseEnterEvent>(_ =>
            {
                btn.style.borderTopColor = hoverBorder;
                btn.style.borderBottomColor = hoverBorder;
                btn.style.borderLeftColor = hoverBorder;
                btn.style.borderRightColor = hoverBorder;
            });
            btn.RegisterCallback<MouseLeaveEvent>(_ =>
            {
                btn.style.borderTopColor = transparentBorder;
                btn.style.borderBottomColor = transparentBorder;
                btn.style.borderLeftColor = transparentBorder;
                btn.style.borderRightColor = transparentBorder;
            });
            // No font override - uses Unity's default editor font (still bold), matching
            // the "Use a different email" link rather than the heavy title font.
        }
        
        private static void StyleTextField(TextField field)
        {
            field.style.backgroundColor = Color.clear;
            field.style.borderTopWidth = 0;
            field.style.borderBottomWidth = 0;
            field.style.borderLeftWidth = 0;
            field.style.borderRightWidth = 0;
            
            field.RegisterCallback<GeometryChangedEvent>(evt =>
            {
                var input = field.Q<VisualElement>("unity-text-input");
                if (input != null)
                {
                    input.style.backgroundColor = new Color(0.07f, 0.06f, 0.11f, 1f);
                    input.style.borderTopLeftRadius = 6;
                    input.style.borderTopRightRadius = 6;
                    input.style.borderBottomLeftRadius = 6;
                    input.style.borderBottomRightRadius = 6;
                    input.style.borderTopWidth = 1;
                    input.style.borderBottomWidth = 1;
                    input.style.borderLeftWidth = 1;
                    input.style.borderRightWidth = 1;
                    input.style.borderTopColor = new Color(1f, 1f, 1f, 0.1f);
                    input.style.borderBottomColor = new Color(1f, 1f, 1f, 0.1f);
                    input.style.borderLeftColor = new Color(1f, 1f, 1f, 0.1f);
                    input.style.borderRightColor = new Color(1f, 1f, 1f, 0.1f);
                    input.style.paddingLeft = 10;
                    input.style.paddingRight = 10;
                    input.style.paddingTop = 8;
                    input.style.paddingBottom = 8;
                    input.style.color = Color.white;
                    input.style.fontSize = 13;
                }
            });
        }
        
        // ─── Email History ───────────────────────────────────────────────
        
        private static System.Collections.Generic.List<string> LoadEmailHistory()
        {
            var emails = new System.Collections.Generic.List<string>();
            string json = EditorPrefs.GetString(PREF_EMAIL_HISTORY, "");
            if (!string.IsNullOrEmpty(json))
            {
                // Simple JSON array parse: ["a@b.com","c@d.com"]
                json = json.Trim('[', ']');
                foreach (var part in json.Split(','))
                {
                    string email = part.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(email) && email.Contains("@"))
                        emails.Add(email);
                }
            }
            return emails;
        }
        
        public static void SaveEmailToHistory(string email)
        {
            if (string.IsNullOrEmpty(email) || !email.Contains("@")) return;
            
            var emails = LoadEmailHistory();
            emails.Remove(email); // remove if already exists (will re-add at top)
            emails.Insert(0, email);
            
            // Limit to max
            while (emails.Count > MAX_EMAIL_HISTORY)
                emails.RemoveAt(emails.Count - 1);
            
            // Save as JSON array
            string json = "[" + string.Join(",", emails.ConvertAll(e => $"\"{e}\"")) + "]";
            EditorPrefs.SetString(PREF_EMAIL_HISTORY, json);
        }
        
        private static void ShowEmailDropdown(TextField emailField, VisualElement root, System.Action<string> onSelect)
        {
            HideEmailDropdown();
            
            var emails = LoadEmailHistory();
            if (emails.Count == 0) return;
            
            _emailDropdown = new VisualElement();
            _emailDropdown.style.position = Position.Absolute;
            _emailDropdown.style.backgroundColor = new Color(0.07f, 0.06f, 0.11f, 0.98f);
            _emailDropdown.style.borderBottomLeftRadius = 6;
            _emailDropdown.style.borderBottomRightRadius = 6;
            _emailDropdown.style.borderTopWidth = 0;
            _emailDropdown.style.borderBottomWidth = 1;
            _emailDropdown.style.borderLeftWidth = 1;
            _emailDropdown.style.borderRightWidth = 1;
            _emailDropdown.style.borderBottomColor = new Color(1f, 1f, 1f, 0.15f);
            _emailDropdown.style.borderLeftColor = new Color(1f, 1f, 1f, 0.15f);
            _emailDropdown.style.borderRightColor = new Color(1f, 1f, 1f, 0.15f);
            _emailDropdown.style.overflow = Overflow.Hidden;
            
            foreach (var email in emails)
            {
                var row = new VisualElement();
                row.style.backgroundColor = Color.clear;
                row.style.height = 30;
                row.style.justifyContent = Justify.Center;
                row.style.paddingLeft = 10;
                row.pickingMode = PickingMode.Position;
                
                var label = new Label(email);
                label.style.color = new Color(1f, 1f, 1f, 0.7f);
                label.style.fontSize = 12;
                label.pickingMode = PickingMode.Ignore;
                row.Add(label);
                
                string capturedEmail = email;
                row.RegisterCallback<MouseEnterEvent>(_ => row.style.backgroundColor = new Color(1f, 1f, 1f, 0.08f));
                row.RegisterCallback<MouseLeaveEvent>(_ => row.style.backgroundColor = Color.clear);
                row.RegisterCallback<PointerDownEvent>(evt => {
                    evt.StopPropagation();
                    onSelect?.Invoke(capturedEmail);
                });
                
                _emailDropdown.Add(row);
            }
            
            // Add to root (topmost element) so it draws above everything
            root.Add(_emailDropdown);
            
            // Position below the email field using world coordinates
            emailField.schedule.Execute(() => {
                if (_emailDropdown == null) return;
                var fieldRect = emailField.worldBound;
                var rootRect = root.worldBound;
                _emailDropdown.style.top = fieldRect.yMax - rootRect.y;
                _emailDropdown.style.left = fieldRect.x - rootRect.x;
                _emailDropdown.style.width = fieldRect.width;
            });
        }
        
        private static void HideEmailDropdown()
        {
            if (_emailDropdown != null)
            {
                _emailDropdown.RemoveFromHierarchy();
                _emailDropdown = null;
            }
        }
    }
}
