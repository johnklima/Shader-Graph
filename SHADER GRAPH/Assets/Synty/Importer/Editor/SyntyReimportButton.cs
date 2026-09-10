using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Synty.Passport.Editor
{
    [InitializeOnLoad]
    public static class SyntyReimportButton
    {
        private static readonly Type ToolbarType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.Toolbar");

        private static readonly Color DefaultColor      = new Color(46f/255f, 46f/255f, 46f/255f, 1f);
        private static readonly Color DefaultHoverColor = new Color(62f/255f, 62f/255f, 62f/255f, 1f);
        private static readonly Color DefaultPressColor = new Color(30f/255f, 30f/255f, 30f/255f, 1f);
        private static readonly Color ActiveColor       = new Color(255f/255f, 160f/255f, 30f/255f, 1f);
        private static readonly Color ActiveHoverColor  = new Color(255f/255f, 180f/255f, 60f/255f, 1f);

        private static ScriptableObject _currentToolbar;
        private static bool _initialized;
        private static int _retryCount;
        private const int MaxRetries = 100;

        private static VisualElement _container;
        private static VisualElement _spinner;
        private static bool _isHovered;
        private static bool _isReimporting;
        private static double _lastAnimTime;
        private static float _spinAngle;

        static SyntyReimportButton()
        {
            EditorApplication.update -= OnUpdate;
            EditorApplication.update += OnUpdate;
        }

        // True if the developer debug-unlock marker type exists in the project. Mirrors the
        // check used by the main Passport tool. Kept here so the Reimport toolbar button is
        // only shown in the Synty dev project, never in customer installs.
        private static bool IsDebugUnlocked()
        {
            return Type.GetType("Synty.Passport.Dev.SyntyDebugUnlock, Assembly-CSharp-Editor") != null
                || Type.GetType("Synty.Passport.Dev.SyntyDebugUnlock") != null;
        }

        private static void ApplyButtonColor(int state)
        {
            if (_container == null) return;
            Color bg;
            if (_isReimporting)
                bg = ActiveColor;
            else
                bg = state == 2 ? DefaultPressColor : state == 1 ? DefaultHoverColor : DefaultColor;
            _container.style.backgroundColor = bg;
        }

        private static void OnUpdate()
        {
            if (_isReimporting && _spinner != null)
            {
                double now = EditorApplication.timeSinceStartup;
                float dt = (float)(now - _lastAnimTime);
                _lastAnimTime = now;
                _spinAngle += dt * 360f;
                if (_spinAngle >= 360f) _spinAngle -= 360f;
                _spinner.style.rotate = new Rotate(new Angle(_spinAngle, AngleUnit.Degree));
            }

            if (_initialized) return;

            // The Reimport button is a developer convenience. Only inject it when the
            // developer marker script (Synty.Passport.Dev.SyntyDebugUnlock) is present in
            // the project. That file is kept in the Synty dev project and excluded from the
            // customer .unitypackage, so customers never get this toolbar button. Located by
            // name via reflection so this compiles whether or not the marker exists.
            if (!IsDebugUnlocked())
            {
                // If a stale button exists from a previous session where the marker was
                // present, remove it so toggling the marker hides the button too.
                if (_container != null) { _container.RemoveFromHierarchy(); _container = null; }
                _initialized = true; // stop polling; nothing to inject
                return;
            }

            var toolbars = Resources.FindObjectsOfTypeAll(ToolbarType);
            if (toolbars == null || toolbars.Length == 0) return;
            _currentToolbar = (ScriptableObject)toolbars[0];

            if (TryUIToolkitApproach())
            {
                _initialized = true;
                _lastAnimTime = EditorApplication.timeSinceStartup;
            }
            else
            {
                _retryCount++;
                if (_retryCount >= MaxRetries) _initialized = true; // give up silently
            }
        }

        private static bool TryUIToolkitApproach()
        {
            try
            {
                var rootField = _currentToolbar.GetType()
                    .GetField("m_Root", BindingFlags.NonPublic | BindingFlags.Instance);
                if (rootField == null) return false;

                var root = rootField.GetValue(_currentToolbar) as VisualElement;
                if (root == null) return false;

                // Remove if already exists (domain reload)
                var existing = root.Q("synty-reimport-button");
                if (existing != null) existing.RemoveFromHierarchy();

                _container = new VisualElement();
                _container.name = "synty-reimport-button";
                _container.pickingMode = PickingMode.Position;
                _container.style.flexDirection = FlexDirection.Row;
                _container.style.alignItems = Align.Center;
                _container.style.justifyContent = Justify.Center;
                _container.style.marginLeft = 2;
                _container.style.marginRight = 2;
                _container.style.paddingLeft = 7;
                _container.style.paddingRight = 7;
                _container.style.paddingTop = 2;
                _container.style.paddingBottom = 2;
                _container.style.height = 20;
                _container.style.alignSelf = Align.Center;
                _container.style.borderTopLeftRadius = 3;
                _container.style.borderTopRightRadius = 3;
                _container.style.borderBottomLeftRadius = 3;
                _container.style.borderBottomRightRadius = 3;

                // Spinner ring (rotates during reimport)
                _spinner = new VisualElement();
                _spinner.pickingMode = PickingMode.Ignore;
                _spinner.style.width = 11;
                _spinner.style.height = 11;
                _spinner.style.borderTopWidth = 2;
                _spinner.style.borderBottomWidth = 2;
                _spinner.style.borderLeftWidth = 2;
                _spinner.style.borderRightWidth = 2;
                _spinner.style.borderTopColor = new Color(1f, 1f, 1f, 0.9f);
                _spinner.style.borderRightColor = new Color(1f, 1f, 1f, 0.3f);
                _spinner.style.borderBottomColor = new Color(1f, 1f, 1f, 0.3f);
                _spinner.style.borderLeftColor = new Color(1f, 1f, 1f, 0.3f);
                _spinner.style.borderTopLeftRadius = 6;
                _spinner.style.borderTopRightRadius = 6;
                _spinner.style.borderBottomLeftRadius = 6;
                _spinner.style.borderBottomRightRadius = 6;
                _spinner.style.marginRight = 5;
                _container.Add(_spinner);

                var label = new Label("Reimport");
                label.pickingMode = PickingMode.Ignore;
                label.style.fontSize = 11;
                label.style.color = Color.white;
                label.style.unityTextAlign = TextAnchor.MiddleCenter;
                label.style.marginTop = label.style.marginBottom = 0;
                label.style.marginLeft = label.style.marginRight = 0;
                label.style.paddingTop = label.style.paddingBottom = 0;
                label.style.paddingLeft = label.style.paddingRight = 0;
                _container.Add(label);

                ApplyButtonColor(0);

                _container.RegisterCallback<MouseDownEvent>(evt => {
                    if (evt.button == 0) ApplyButtonColor(2);
                });
                _container.RegisterCallback<MouseUpEvent>(evt => {
                    if (evt.button == 0)
                    {
                        _isHovered = true;
                        ApplyButtonColor(1);
                        RunReimport();
                        evt.StopPropagation();
                    }
                });
                _container.RegisterCallback<MouseEnterEvent>(evt => {
                    _isHovered = true;
                    ApplyButtonColor(1);
                });
                _container.RegisterCallback<MouseLeaveEvent>(evt => {
                    _isHovered = false;
                    ApplyButtonColor(0);
                });

                // Insert right after the Synty Assets button if found, otherwise near play controls
                var syntyBtn = root.Q("synty-toolbar-button");
                if (syntyBtn?.parent != null)
                {
                    var p = syntyBtn.parent;
                    p.Insert(p.IndexOf(syntyBtn) + 1, _container);
                    return true;
                }

                var stepButton = root.Q("Step");
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
                // Debug.LogWarning($"[SyntyReimportButton] Failed to inject toolbar button: {e.Message}");
                return false;
            }
        }

        private static void RunReimport()
        {
            if (_isReimporting) return;

            _isReimporting = true;
            _spinAngle = 0f;
            _lastAnimTime = EditorApplication.timeSinceStartup;
            ApplyButtonColor(0);

            try
            {
                var paths = CollectPaths();
                // Debug.Log($"[SyntyReimport] Reimporting {paths.Count} asset(s)...");

                AssetDatabase.StartAssetEditing();
                foreach (var path in paths)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();

                _isReimporting = false;
                ApplyButtonColor(_isHovered ? 1 : 0);
                // Debug.Log("[SyntyReimport] Done.");
            }
        }

        private static List<string> CollectPaths()
        {
            var paths = new List<string>();

            // Find the main C# script
            foreach (var guid in AssetDatabase.FindAssets("SyntyAssetDownloaderV2 t:Script"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("SyntyAssetDownloaderV2.cs"))
                    paths.Add(path);
            }

            // Find the USS
            foreach (var guid in AssetDatabase.FindAssets("SyntyAssetDownloaderV2 t:StyleSheet"))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            // Find all textures under the Passport folder
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets/Synty/Importer" }))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            // Also grab any fonts
            foreach (var guid in AssetDatabase.FindAssets("t:Font", new[] { "Assets/Synty/Importer" }))
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }

            paths.RemoveAll(string.IsNullOrEmpty);
            return paths;
        }
    }
}
