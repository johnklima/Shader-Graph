using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Synty.Tools.V2;
using Synty.Passport.Compat;

namespace Synty.Passport.Editor
{
    [InitializeOnLoad]
    public static class SyntyToolbarButton
    {
#pragma warning disable CS0414 // used only on the pre-6.3 reflection path (compiled out on 6.3+)
        private static readonly Type ToolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");
#pragma warning restore CS0414

        private const string PREFS_NOTIFICATIONS  = "SyntyDownloaderV2_Notifications";
        // Set true the first time Passport is opened IN THIS PROJECT. Until then the toolbar button
        // shows the orange notification colour to invite a first open. Project-scoped (via ProjectKey)
        // so every fresh install lights up — a plain global pref would stay "opened" across all
        // projects once the tool had been opened anywhere, so new installs never highlighted.
        private static readonly string PREFS_OPENED_ONCE = "SyntyDownloaderV2_OpenedOnce_" + ProjectKey();
        private const string PREFS_PREFIX         = "SyntyDownloaderV2_TB_";
        private const string PREFS_POSITION       = "SyntyDownloaderV2_TB_Position";

        // Stable per-project hash of the data path (mirrors the installer), used to scope the
        // first-run highlight pref so each project tracks its own "opened once" state.
        private static string ProjectKey()
        {
            string p = Application.dataPath;
            int h = 17;
            foreach (char c in p) h = h * 31 + c;
            return (h & 0x7fffffff).ToString();
        }
        
        // Toolbar position: 0=Left, 1=Center (default), 2=Right, 3=Off
        public static int ToolbarPosition { get; private set; } = 1;

        // ── Persistent settings (EditorPrefs) ─────────────────────────────
        public static Color  DefaultBgColor       { get; private set; } = new Color(55f/255f, 55f/255f, 55f/255f, 1f);
        public static Color  DefaultHoverColor    { get; private set; } = new Color(72f/255f, 72f/255f, 72f/255f, 1f);
        public static Color  DefaultPressColor    { get; private set; } = new Color(97f/255f, 97f/255f, 97f/255f, 1f);
        public static Color  DefaultTextColor     { get; private set; } = Color.white;
        public static Color  NotifBgColor         { get; private set; } = new Color(255f/255f, 227f/255f, 8f/255f, 1f);
        public static Color  NotifHoverColor      { get; private set; } = new Color(253f/255f, 207f/255f, 1f/255f, 1f);
        public static Color  NotifPressColor      { get; private set; } = new Color(253f/255f, 194f/255f, 1f/255f, 1f);
        public static Color  NotifTextColor       { get; private set; } = new Color(12f/255f, 13f/255f, 16f/255f, 1f);
        public static Color  BadgeColor           { get; private set; } = new Color(236f/255f, 28f/255f, 73f/255f, 1f);
        public static string LabelText            { get; private set; } = "Synty Importer";
        public static float  FontSize             { get; private set; } = 12f;
        public static float  BtnHeight            { get; private set; } = 20f;
        public static float  BorderRadius         { get; private set; } = 4f;
        public static float  PaddingH             { get; private set; } = 10.87f;
        public static float  BadgeSize            { get; private set; } = 10.46f;
        public static bool   ShowIcon             { get; private set; } = true;
        public static float  IconSize             { get; private set; } = 18.36f;
        public static float  IconX               { get; private set; } = -5.65f;
        public static float  LabelX              { get; private set; } = -1.73f;

        private static ScriptableObject _currentToolbar;
        private static bool  _initialized;
        private static Font  _newakeFont;
#pragma warning disable CS0414 // used only on the pre-6.3 reflection path (compiled out on 6.3+)
        private static int   _retryCount;
#pragma warning restore CS0414
        private const  int   MaxRetries = 100;

        private static VisualElement _container;
        private static VisualElement _notificationBadge;
        private static double _lastAnimTime;
        private static float  _animPhase;
        private static bool   _hasNotifications;
        private static bool   _isHovered;

        static SyntyToolbarButton()
        {
            InstallToolbarWarningFilter();
            LoadPrefs();
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
        }

        // ── Toolbar-warning suppression ───────────────────────────────────
        // Unity 6 logs a warning when it detects custom elements added to the main toolbar via
        // unsupported methods (the toolbar-zone injection this class uses). There is no public API
        // to register such an element with the styling we need, so we install a narrow log filter
        // that drops ONLY that specific message and passes everything else through unchanged. The
        // original handler is restored automatically on domain reload.
        private static ILogHandler _originalLogHandler;
        private static bool _filterInstalled;

        private static void InstallToolbarWarningFilter()
        {
            if (_filterInstalled) return;
            _filterInstalled = true;
            try
            {
                _originalLogHandler = Debug.unityLogger.logHandler;
                if (_originalLogHandler is SyntyToolbarLogFilter) return; // already wrapped
                Debug.unityLogger.logHandler = new SyntyToolbarLogFilter(_originalLogHandler);
                AssemblyReloadEvents.beforeAssemblyReload -= RestoreLogHandler;
                AssemblyReloadEvents.beforeAssemblyReload += RestoreLogHandler;
            }
            catch { /* if anything goes wrong, leave logging untouched */ }
        }

        private static void RestoreLogHandler()
        {
            try
            {
                if (Debug.unityLogger.logHandler is SyntyToolbarLogFilter f && _originalLogHandler != null)
                    Debug.unityLogger.logHandler = _originalLogHandler;
            }
            catch { }
        }

        // Wraps the editor's log handler and swallows only the specific unsupported-toolbar warning.
        private sealed class SyntyToolbarLogFilter : ILogHandler
        {
            private readonly ILogHandler _inner;
            public SyntyToolbarLogFilter(ILogHandler inner) { _inner = inner; }

            public void LogFormat(LogType logType, UnityEngine.Object context, string format, params object[] args)
            {
                // Debug.Log/LogWarning pass format="{0}" with the real text in args[0], so reconstruct
                // the actual message before matching.
                string message = format;
                if (args != null && args.Length > 0)
                {
                    try { message = string.Format(format, args); }
                    catch { message = format + " " + string.Join(" ", args); }
                }

                if (logType == LogType.Warning && message != null &&
                    message.Contains("synty-toolbar-button") &&
                    message.Contains("main toolbar"))
                {
                    return; // drop only this exact warning
                }
                // When "Debug Logging" (profile ▸ Advanced) is off, swallow the importer's own logs so
                // the console stays quiet for customers. Real errors/exceptions always pass through.
                if (logType != LogType.Error && logType != LogType.Exception && logType != LogType.Assert &&
                    message != null &&
                    (message.Contains("[Synty Importer]") || message.Contains("ExamplePackConfig")) &&
                    !EditorPrefs.GetBool("SyntyImporter_DebugLogging", false))
                {
                    return;
                }
                _inner.LogFormat(logType, context, format, args);
            }

            public void LogException(Exception exception, UnityEngine.Object context)
            {
                _inner.LogException(exception, context);
            }
        }

        // ── Public API ────────────────────────────────────────────────────
        public static void SetNotifications(bool active)
        {
            // Keep the button lit while the first-run highlight is still active, even when there are
            // no server notifications — the install highlight must persist until the user clicks the
            // toolbar button itself (which sets OPENED_ONCE). Otherwise the notification service's
            // "no notifications" call on load would immediately clear the fresh-install highlight.
            bool firstRunHighlight = !EditorPrefs.GetBool(PREFS_OPENED_ONCE, false);
            _hasNotifications = active || firstRunHighlight;
            if (_container == null) return;
            ApplyButtonColor(_isHovered ? 1 : 0);
            if (_notificationBadge != null)
                _notificationBadge.style.display = _hasNotifications ? DisplayStyle.Flex : DisplayStyle.None;
        }

        public static void ApplySettings(
            Color defaultBg, Color defaultHover, Color defaultPress, Color defaultText,
            Color notifBg,   Color notifHover,   Color notifPress,   Color notifText,
            Color badge,     string label,        float fontSize,     float height,
            float radius,    float paddingH,      float badgeSize,
            bool showIcon,   float iconSize,  float iconX, float labelX)
        {
            DefaultBgColor    = defaultBg;
            DefaultHoverColor = defaultHover;
            DefaultPressColor = defaultPress;
            DefaultTextColor  = defaultText;
            NotifBgColor      = notifBg;
            NotifHoverColor   = notifHover;
            NotifPressColor   = notifPress;
            NotifTextColor    = notifText;
            BadgeColor        = badge;
            LabelText         = label;
            FontSize          = fontSize;
            BtnHeight         = height;
            BorderRadius      = radius;
            PaddingH          = paddingH;
            BadgeSize         = badgeSize;
            ShowIcon          = showIcon;
            IconSize          = iconSize;
            IconX             = iconX;
            LabelX            = labelX;

            SavePrefs();
            Rebuild();
        }

        // ── Prefs persistence ─────────────────────────────────────────────
        private static void SavePrefs()
        {
            SaveColor("DefaultBg",    DefaultBgColor);
            SaveColor("DefaultHover", DefaultHoverColor);
            SaveColor("DefaultPress", DefaultPressColor);
            SaveColor("DefaultText",  DefaultTextColor);
            SaveColor("NotifBg",      NotifBgColor);
            SaveColor("NotifHover",   NotifHoverColor);
            SaveColor("NotifPress",   NotifPressColor);
            SaveColor("NotifText",    NotifTextColor);
            SaveColor("Badge",        BadgeColor);
            EditorPrefs.SetString(PREFS_PREFIX + "Label",       LabelText);
            EditorPrefs.SetFloat (PREFS_PREFIX + "FontSize",    FontSize);
            EditorPrefs.SetFloat (PREFS_PREFIX + "Height",      BtnHeight);
            EditorPrefs.SetFloat (PREFS_PREFIX + "Radius",      BorderRadius);
            EditorPrefs.SetFloat (PREFS_PREFIX + "PaddingH",    PaddingH);
            EditorPrefs.SetFloat (PREFS_PREFIX + "BadgeSize",   BadgeSize);
            EditorPrefs.SetBool  (PREFS_PREFIX + "ShowIcon",    ShowIcon);
            EditorPrefs.SetFloat (PREFS_PREFIX + "IconSize",    IconSize);
            EditorPrefs.SetFloat (PREFS_PREFIX + "IconX",       IconX);
            EditorPrefs.SetFloat (PREFS_PREFIX + "LabelX",      LabelX);
        }

        private static void LoadPrefs()
        {
            // One-time reseed: when the baked defaults change, bump this version so saved prefs from
            // an older default set are cleared once and the new code defaults take effect. After the
            // reseed the user's own panel edits persist as normal.
            const int DefaultsVersion = 3;
            if (EditorPrefs.GetInt(PREFS_PREFIX + "DefaultsVersion", 0) < DefaultsVersion)
            {
                EditorPrefs.SetInt(PREFS_PREFIX + "DefaultsVersion", DefaultsVersion);
                // Also re-arm the first-run highlight so the (renamed) button lights up again
                // until the user opens it once.
                EditorPrefs.SetBool(PREFS_OPENED_ONCE, false);
                SavePrefs(); // write current (new) code defaults over any stale saved values
                return;       // skip loading the old values this once
            }
            DefaultBgColor    = LoadColor("DefaultBg",    DefaultBgColor);
            DefaultHoverColor = LoadColor("DefaultHover", DefaultHoverColor);
            DefaultPressColor = LoadColor("DefaultPress", DefaultPressColor);
            DefaultTextColor  = LoadColor("DefaultText",  DefaultTextColor);
            NotifBgColor      = LoadColor("NotifBg",      NotifBgColor);
            NotifHoverColor   = LoadColor("NotifHover",   NotifHoverColor);
            NotifPressColor   = LoadColor("NotifPress",   NotifPressColor);
            NotifTextColor    = LoadColor("NotifText",    NotifTextColor);
            BadgeColor        = LoadColor("Badge",        BadgeColor);
            LabelText         = EditorPrefs.GetString(PREFS_PREFIX + "Label",    LabelText);
            FontSize          = EditorPrefs.GetFloat (PREFS_PREFIX + "FontSize", FontSize);
            BtnHeight         = EditorPrefs.GetFloat (PREFS_PREFIX + "Height",   BtnHeight);
            BorderRadius      = EditorPrefs.GetFloat (PREFS_PREFIX + "Radius",   BorderRadius);
            PaddingH          = EditorPrefs.GetFloat (PREFS_PREFIX + "PaddingH", PaddingH);
            BadgeSize         = EditorPrefs.GetFloat (PREFS_PREFIX + "BadgeSize",BadgeSize);
            ShowIcon          = EditorPrefs.GetBool  (PREFS_PREFIX + "ShowIcon", ShowIcon);
            IconSize          = EditorPrefs.GetFloat (PREFS_PREFIX + "IconSize", IconSize);
            IconX             = EditorPrefs.GetFloat (PREFS_PREFIX + "IconX",    IconX);
            LabelX            = EditorPrefs.GetFloat (PREFS_PREFIX + "LabelX",   LabelX);
            ToolbarPosition   = EditorPrefs.GetInt   (PREFS_POSITION, 1);
        }

        public static void SetToolbarPosition(int position)
        {
            ToolbarPosition = position;
            EditorPrefs.SetInt(PREFS_POSITION, position);
            
            if (position == 3) // Off
            {
                if (_container != null)
                {
                    _container.RemoveFromHierarchy();
                    _container = null;
                }
                _initialized = true; // prevent retry
            }
            else
            {
                Rebuild();
            }
        }

        private static void SaveColor(string key, Color c) =>
            EditorPrefs.SetString(PREFS_PREFIX + key,
                $"{c.r:F4},{c.g:F4},{c.b:F4},{c.a:F4}");

        private static Color LoadColor(string key, Color fallback)
        {
            string s = EditorPrefs.GetString(PREFS_PREFIX + key, "");
            if (string.IsNullOrEmpty(s)) return fallback;
            var p = s.Split(',');
            if (p.Length != 4) return fallback;
            return new Color(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3]));
        }

        // ── Core ──────────────────────────────────────────────────────────
        private static void Rebuild()
        {
            _initialized = false;
            _retryCount  = 0;
        }

        private static void ApplyButtonColor(int state)
        {
            if (_container == null) return;
            Color bg = _hasNotifications
                ? (state == 2 ? NotifPressColor : state == 1 ? NotifHoverColor : NotifBgColor)
                : (state == 2 ? DefaultPressColor : state == 1 ? DefaultHoverColor : DefaultBgColor);
            _container.style.backgroundColor = bg;
            var lbl = _container.Q<Label>();
            if (lbl != null)
                lbl.style.color = _hasNotifications ? NotifTextColor : DefaultTextColor;
        }

        private static void OnUpdate()
        {
            if (_notificationBadge != null && _hasNotifications)
                UpdateNotificationAnimation();

            if (_initialized) return;

#if UNITY_6000_3_OR_NEWER
            // Unity 6.3+ uses the official MainToolbarElement button (SyntyImporterMainToolbar.cs),
            // which is an overlay Unity lets the user reposition (ctrl+drag) and show/hide via the
            // ⋮ menu. The legacy reflection injection below doesn't apply on those versions, so skip
            // it (and stop retrying every editor tick).
            _initialized = true;
            return;
#else
            // Find the internal Toolbar instance if its type is available (used by the pre-6.3
            // m_Root path). Not fatal if missing — GetToolbarRoot() falls back to scanning editor
            // windows for the toolbar container.
            if (ToolbarType != null)
            {
                var toolbars = Resources.FindObjectsOfTypeAll(ToolbarType);
                if (toolbars != null && toolbars.Length > 0)
                    _currentToolbar = (ScriptableObject)toolbars[0];
            }

            if (_newakeFont == null)
            {
                _newakeFont = LoadFont("Newake");
                if (_newakeFont == null)
                {
                    _retryCount++;
                    if (_retryCount < MaxRetries) return;
                }
            }

            if (TryUIToolkitApproach())
            {
                _initialized = true;
                _lastAnimTime = EditorApplication.timeSinceStartup;
            }
#endif
        }

        private static void UpdateNotificationAnimation()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - _lastAnimTime);
            _lastAnimTime = now;
            _animPhase += dt * 3.14159f;
            if (_animPhase > 6.28318f) _animPhase -= 6.28318f;
            float s = 1f + 0.2f * Mathf.Sin(_animPhase);
            _notificationBadge.style.scale = new Scale(new Vector3(s, s, 1f));
        }

        private static Font LoadFont(string fontName)
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Font"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.ToLower().Contains(fontName.ToLower()))
                {
                    var font = AssetDatabase.LoadAssetAtPath<Font>(path);
                    if (font != null) return font;
                }
            }
            return null;
        }

        private static Texture2D LoadTexture(string name)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{name} t:Texture2D"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == name)
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            return null;
        }

        // Returns the main toolbar's root VisualElement. Tries the internal Toolbar.m_Root field
        // first (fast path for known versions), then falls back to scanning every editor window for
        // the one that contains the toolbar zone elements. The fallback doesn't depend on the internal
        // Toolbar type or field names, so it keeps working when Unity renames them (e.g. Unity 6.5).
        private static VisualElement GetToolbarRoot()
        {
            if (_currentToolbar != null)
            {
                try
                {
                    var rootField = _currentToolbar.GetType()
                        .GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (rootField != null && rootField.GetValue(_currentToolbar) is VisualElement r &&
                        (r.Q("ToolbarZoneLeftAlign") != null || r.Q("ToolbarZoneRightAlign") != null))
                        return r;
                }
                catch { }
            }
            // Version-resilient fallback: the toolbar lives in some EditorWindow's rootVisualElement;
            // identify it by the presence of the align zones (stable names across 2021 → 6.x).
            foreach (var w in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                var root = w != null ? w.rootVisualElement : null;
                if (root == null) continue;
                // 6.3+ overlay toolbar (MainToolbarWindow) OR pre-6.3 align zones.
                if (root.Q(className: "unity-main-toolbar-overlay-container") != null) return root;
                if (root.Q("ToolbarZoneLeftAlign") != null || root.Q("ToolbarZoneRightAlign") != null)
                    return root;
            }
            return null;
        }

        private static bool TryUIToolkitApproach()
        {
            try
            {
                var root = GetToolbarRoot();
                if (root == null) return false;

                var existing = root.Q("synty-toolbar-button");
                if (existing != null) existing.RemoveFromHierarchy();

                // First-run highlight: if Passport has never been opened in this project,
                // light the toolbar button in the orange notification colour to draw the
                // user in. Cleared the first time they open it (see OpenSyntyAssetDownloader).
                // This is separate from server-driven notifications - either reason lights
                // the button.
                bool firstRunHighlight = !EditorPrefs.GetBool(PREFS_OPENED_ONCE, false);
                _hasNotifications = firstRunHighlight || EditorPrefs.GetBool(PREFS_NOTIFICATIONS, false);

                _container = new VisualElement();
                _container.name = "synty-toolbar-button";
                _container.pickingMode = PickingMode.Position;
                _container.style.position       = Position.Relative;
                _container.style.flexDirection   = FlexDirection.Row;
                _container.style.alignItems      = Align.Center;
                _container.style.justifyContent  = Justify.Center;
                _container.style.marginLeft      = 6;
                _container.style.marginRight     = 2;
                _container.style.paddingLeft     = PaddingH;
                _container.style.paddingRight    = PaddingH;
                _container.style.paddingTop      = 2;
                _container.style.paddingBottom   = 2;
                _container.style.height          = BtnHeight;
                _container.style.alignSelf       = Align.Center;
                _container.style.borderTopLeftRadius     = BorderRadius;
                _container.style.borderTopRightRadius    = BorderRadius;
                _container.style.borderBottomLeftRadius  = BorderRadius;
                _container.style.borderBottomRightRadius = BorderRadius;

                // Optional logo icon
                if (ShowIcon)
                {
                    var iconEl = new VisualElement();
                    iconEl.name = "synty-toolbar-icon";
                    iconEl.pickingMode = PickingMode.Ignore;
                    iconEl.style.width  = IconSize;
                    iconEl.style.height = IconSize;
                    iconEl.style.marginRight = 4;
                    iconEl.style.marginLeft  = IconX;
                    iconEl.style.alignSelf = Align.Center;
                    iconEl.style.flexShrink = 0;
                    iconEl.style.SetBackgroundScaleToFit();
                    var logoTex = LoadTexture("Synty_Logo");
                    if (logoTex != null)
                        iconEl.style.backgroundImage = new StyleBackground(logoTex);
                    _container.Add(iconEl);
                }

                var label = new Label(LabelText);
                label.pickingMode = PickingMode.Ignore;
                label.style.unityFontStyleAndWeight = FontStyle.Normal;
                label.style.fontSize         = FontSize;
                label.style.unityTextAlign   = TextAnchor.MiddleCenter;
                label.style.marginTop = label.style.marginBottom = 0;
                label.style.marginLeft = LabelX;
                label.style.marginRight = 0;
                label.style.paddingTop = label.style.paddingBottom = 0;
                label.style.paddingLeft = label.style.paddingRight = 0;
                if (_newakeFont != null)
                    label.style.unityFontDefinition = FontDefinition.FromFont(_newakeFont);
                _container.Add(label);

                _notificationBadge = new VisualElement();
                _notificationBadge.name = "notification-badge";
                _notificationBadge.pickingMode = PickingMode.Ignore;
                _notificationBadge.style.position = Position.Absolute;
                _notificationBadge.style.top   = -3;
                _notificationBadge.style.right  = -3;
                _notificationBadge.style.width  = BadgeSize;
                _notificationBadge.style.height = BadgeSize;
                _notificationBadge.style.backgroundColor         = BadgeColor;
                _notificationBadge.style.borderTopLeftRadius     = BadgeSize / 2f;
                _notificationBadge.style.borderTopRightRadius    = BadgeSize / 2f;
                _notificationBadge.style.borderBottomLeftRadius  = BadgeSize / 2f;
                _notificationBadge.style.borderBottomRightRadius = BadgeSize / 2f;
                _notificationBadge.style.display = _hasNotifications ? DisplayStyle.Flex : DisplayStyle.None;
                _container.Add(_notificationBadge);

                ApplyButtonColor(0);

                _container.RegisterCallback<MouseDownEvent>(evt  => { if (evt.button == 0) ApplyButtonColor(2); });
                _container.RegisterCallback<MouseUpEvent>(evt    => {
                    if (evt.button == 0) { _isHovered = true; ApplyButtonColor(1); OpenSyntyAssetDownloader(); evt.StopPropagation(); }
                });
                _container.RegisterCallback<MouseEnterEvent>(evt => { _isHovered = true;  ApplyButtonColor(1); });
                _container.RegisterCallback<MouseLeaveEvent>(evt => { _isHovered = false; ApplyButtonColor(0); });

                // Skip if toolbar button is turned off
                // Unity 6.3+ overlay toolbar: inject the button straight into the container section
                // for the chosen position. This renders it by default — it bypasses Unity's
                // per-element "enable via the ⋮ menu" system (which hides custom elements otherwise).
                var overlayContainer = root.Q(className: "unity-main-toolbar-overlay-container");
                if (overlayContainer != null)
                {
                    if (ToolbarPosition == 3) return true; // Off — built but not injected
                    string sectionClass =
                        ToolbarPosition == 0 ? "unity-overlay-container__before-spacer-container" :
                        ToolbarPosition == 2 ? "unity-overlay-container__after-spacer-container" :
                                               "unity-overlay-container__middle-container";
                    var section = overlayContainer.Q(className: sectionClass)
                                  ?? overlayContainer.Q(className: "unity-overlay-container__middle-container");
                    if (section == null) return false;
                    if (section.Q("synty-toolbar-button") != null) return true; // already injected
                    _container.style.marginLeft = 4;
                    _container.style.marginRight = 4;
                    _container.style.alignSelf = Align.Center;
                    section.Add(_container);
                    return true;
                }

                if (ToolbarPosition == 3)
                    return true; // return true to stop retrying
                
                var stepButton = root.Q("Step");
                
                if (ToolbarPosition == 0) // Left
                {
                    var leftZone = root.Q("ToolbarZoneLeftAlign");
                    if (leftZone != null) { leftZone.Add(_container); return true; }
                }
                else if (ToolbarPosition == 2) // Right
                {
                    var rightZone = root.Q("ToolbarZoneRightAlign");
                    if (rightZone != null) { rightZone.Insert(0, _container); return true; }
                }
                
                // Center (default) — insert after Step button
                if (stepButton?.parent != null)
                {
                    var p = stepButton.parent;
                    p.Insert(p.IndexOf(stepButton) + 1, _container);
                    return true;
                }

                var playMode = root.Q("PlayMode") ?? root.Q("ToolbarZonePlayMode");
                if (playMode != null) { playMode.Add(_container); return true; }

                var zone = root.Q("ToolbarZoneRightAlign") ?? root.Q("ToolbarZoneLeftAlign");
                if (zone != null) { zone.Insert(0, _container); return true; }

                return false;
            }
            catch (Exception)
            {
                // Debug.LogWarning($"[SyntyToolbarButton] Failed: {e.Message}");
                return false;
            }
        }

        // Public entry point for the Unity 6.3+ official main-toolbar button (SyntyImporterMainToolbar).
        public static void OpenFromMainToolbar() => OpenSyntyAssetDownloader();

        private static void OpenSyntyAssetDownloader()
        {
            // Clear the first-run highlight: once the user opens Passport, the toolbar
            // button returns to its default colour (unless a server notification is also
            // active, which SetNotifications governs independently).
            if (!EditorPrefs.GetBool(PREFS_OPENED_ONCE, false))
            {
                EditorPrefs.SetBool(PREFS_OPENED_ONCE, true);
                bool serverNotif = EditorPrefs.GetBool(PREFS_NOTIFICATIONS, false);
                if (!serverNotif) SetNotifications(false);
            }

            // If the Passport bootstrapper is present, route through its update-aware open
            // so the toolbar button checks for (and installs) a newer tool version before
            // showing the window - same behaviour as the menu item. The bootstrapper is a
            // separate file that may not exist in every project, so we locate it by name
            // via reflection and fall back to opening the window directly if it is absent.
            try
            {
                var bootstrapType = Type.GetType("Synty.Passport.Bootstrap.SyntyImporter_Installer, Assembly-CSharp-Editor");
                if (bootstrapType != null)
                {
                    var method = bootstrapType.GetMethod("OpenPassportLatest",
                        BindingFlags.Public | BindingFlags.Static);
                    if (method != null)
                    {
                        method.Invoke(null, null);
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Debug.LogWarning($"[SyntyToolbarButton] Update-check open failed, opening directly: {e.Message}");
            }

            // Fallback: no bootstrapper present (or reflection failed) - open directly.
            EditorWindow.GetWindow<SyntyAssetDownloaderV2>(false, "Synty Importer");
        }
    }
}
