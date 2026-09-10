using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using Synty.Tools;
using Synty.Tools.V2;
using Synty.Passport.Compat;

namespace Synty.Passport.Editor
{
    /// <summary>
    /// Sale advertisement window that appears when a store sale is active.
    /// Shows a sale image with an "Open Synty Passport" button at the bottom.
    /// </summary>
    public class SyntySaleWindow : EditorWindow
    {
        private const string API_URL = "https://synty-downloads.syntystore.workers.dev";
        
        private string _saleTitle;
        private EditorApplication.CallbackFunction _pulseTick;
        private string _saleStage; // "start", "middle", "end", or "extended"
        private string _saleUrl;
        private bool _isNewRelease;
        private Texture2D _adTexture;
        private bool _imageLoaded;
        private bool _imageFailed;
        private UnityWebRequest _imageRequest;
        
        private static readonly float WindowWidth = 550f;
        private static readonly float MaxScreenPercent = 0.8f; // Never exceed 80% of screen
        
        public static void ShowSaleAd(string title, string stage, string saleUrl)
        {
            var window = GetWindow<SyntySaleWindow>(true, string.IsNullOrEmpty(title) ? "Synty Store Sale" : title);
            window._saleTitle = title;
            window._saleStage = string.IsNullOrEmpty(stage) ? "start" : stage;
            window._saleUrl = saleUrl;
            window._isNewRelease = false;
            
            // Start at a compact size, will resize when image loads
            var startSize = new Vector2(WindowWidth, 350);
            window.minSize = new Vector2(300, 150);
            window.maxSize = new Vector2(Screen.currentResolution.width * MaxScreenPercent, Screen.currentResolution.height * MaxScreenPercent);
            
            // Center on main editor window
            window.CenterOnEditor(startSize);
            
            window.LoadAdImage();
            window.Show();
        }
        
        public static void ShowNewRelease(string title, string url)
        {
            var window = GetWindow<SyntySaleWindow>(true, string.IsNullOrEmpty(title) ? "New Release" : title);
            window._saleTitle = title;
            window._saleStage = "new-release";
            window._saleUrl = url;
            window._isNewRelease = true;
            
            var startSize = new Vector2(WindowWidth, 350);
            window.minSize = new Vector2(300, 150);
            window.maxSize = new Vector2(Screen.currentResolution.width * MaxScreenPercent, Screen.currentResolution.height * MaxScreenPercent);
            window.CenterOnEditor(startSize);
            
            window.LoadAdImage();
            window.Show();
        }
        
        private void CenterOnEditor(Vector2 size)
        {
            try
            {
                var typ = typeof(EditorWindow).Assembly.GetType("UnityEditor.ContainerWindow");
                var winsProp = typ?.GetProperty("windows", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                var posProp = typ?.GetProperty("position", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                var winsVal = winsProp?.GetValue(null);
                var winsArr = winsVal as System.Collections.IEnumerable;
                Rect best = new Rect(0, 0, 1920, 1080);
                float bestArea = 0;
                if (winsArr != null && posProp != null)
                {
                    foreach (var w in winsArr)
                    {
                        var r = (Rect)posProp.GetValue(w);
                        float area = r.width * r.height;
                        if (area > bestArea) { bestArea = area; best = r; }
                    }
                }
                position = new Rect(
                    best.x + (best.width - size.x) / 2f,
                    best.y + (best.height - size.y) / 2f,
                    size.x, size.y);
            }
            catch
            {
                position = new Rect(
                    (Screen.currentResolution.width - size.x) / 2f,
                    (Screen.currentResolution.height - size.y) / 2f,
                    size.x, size.y);
            }
        }
        
        private void LoadAdImage()
        {
            // Fetch image from server based on type
            string url = _isNewRelease 
                ? $"{API_URL}/new-release-image" 
                : $"{API_URL}/sale-image-{_saleStage}";
            
            _imageRequest = UnityWebRequestTexture.GetTexture(url);
            var op = _imageRequest.SendWebRequest();
            op.completed += _ =>
            {
                try
                {
                    if (_imageRequest.result == UnityWebRequest.Result.Success)
                    {
                        _adTexture = DownloadHandlerTexture.GetContent(_imageRequest);
                        _imageLoaded = true;
                        
                        // Resize window to match image aspect ratio
                        if (_adTexture.width > 0 && _adTexture.height > 0)
                        {
                            float aspect = (float)_adTexture.height / _adTexture.width;
                            float imageHeight = WindowWidth * aspect;
                            float totalHeight = imageHeight;
                            
                            // Cap to screen size
                            float maxW = Screen.currentResolution.width * MaxScreenPercent;
                            float maxH = Screen.currentResolution.height * MaxScreenPercent;
                            float finalWidth = Mathf.Min(WindowWidth, maxW);
                            float finalHeight = Mathf.Min(totalHeight, maxH);
                            
                            // If height was capped, scale width to maintain image ratio
                            if (totalHeight > maxH)
                            {
                                finalWidth = Mathf.Min(maxH / aspect, maxW);
                                finalHeight = maxH;
                            }
                            
                            var newSize = new Vector2(finalWidth, finalHeight);
                            minSize = new Vector2(300, 150);
                            maxSize = new Vector2(maxW, maxH);
                            CenterOnEditor(newSize);
                        }
                    }
                    else
                    {
                        _imageFailed = true;
                        // Debug.LogWarning($"[SyntySaleWindow] Failed to load ad image: {_imageRequest.error}");
                    }
                }
                catch (Exception)
                {
                    _imageFailed = true;
                    // Debug.LogWarning($"[SyntySaleWindow] Image error: {e.Message}");
                }
                finally
                {
                    _imageRequest.Dispose();
                    _imageRequest = null;
                    Repaint();
                }
            };
        }
        
        private void CreateGUI()
        {
            var root = rootVisualElement;
            // Load the package stylesheet so its ".link-cursor" rule is available (finger cursor on hover).
            var linkSheet = LoadLinkCursorSheet();
            if (linkSheet != null && !root.styleSheets.Contains(linkSheet)) root.styleSheets.Add(linkSheet);
            root.style.backgroundColor = new Color(0.09f, 0.08f, 0.14f, 1f);
            root.style.flexGrow = 1;
            root.style.paddingTop = 0;
            root.style.paddingBottom = 0;
            root.style.paddingLeft = 0;
            root.style.paddingRight = 0;
            
            // Image container (fills entire window)
            var imageContainer = new VisualElement();
            imageContainer.name = "ad-image-container";
            imageContainer.style.flexGrow = 1;
            imageContainer.style.alignItems = Align.Center;
            imageContainer.style.justifyContent = Justify.Center;
            imageContainer.style.overflow = Overflow.Hidden;
            imageContainer.style.position = Position.Relative;
            root.Add(imageContainer);
            
            // Loading label
            var loadingLabel = new Label("Loading...");
            loadingLabel.name = "loading-label";
            loadingLabel.style.color = new Color(1f, 1f, 1f, 0.5f);
            loadingLabel.style.fontSize = 14;
            loadingLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            imageContainer.Add(loadingLabel);
            
            // Clickable area covers top 90% — opens sale URL
            var clickArea = new VisualElement();
            clickArea.style.position = Position.Absolute;
            clickArea.style.top = 0;
            clickArea.style.left = 0;
            clickArea.style.right = 0;
            clickArea.style.bottom = Length.Percent(10);
            clickArea.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (!string.IsNullOrEmpty(_saleUrl))
                    Application.OpenURL(_saleUrl);
            });
            clickArea.AddToClassList("link-cursor");
            imageContainer.Add(clickArea);
            
            // Buttons overlay (absolute positioned at bottom of image)
            var buttonOverlay = new VisualElement();
            buttonOverlay.style.position = Position.Absolute;
            buttonOverlay.style.left = 0;
            buttonOverlay.style.right = 0;
            buttonOverlay.style.bottom = 0;
            buttonOverlay.style.flexDirection = FlexDirection.Column;
            buttonOverlay.style.alignItems = Align.Center;
            buttonOverlay.style.paddingBottom = 16;
            buttonOverlay.style.paddingTop = 24;
            imageContainer.Add(buttonOverlay);
            
            // Poll for image load
            imageContainer.schedule.Execute(() =>
            {
                if (_imageLoaded && _adTexture != null)
                {
                    loadingLabel.style.display = DisplayStyle.None;
                    imageContainer.style.backgroundImage = new StyleBackground(_adTexture);
                    imageContainer.style.SetBackgroundScaleToFit();
                }
                else if (_imageFailed)
                {
                    loadingLabel.text = !string.IsNullOrEmpty(_saleTitle) ? _saleTitle : "Sale";
                    loadingLabel.style.fontSize = 24;
                    loadingLabel.style.color = Color.white;
                    loadingLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                }
            }).Every(100);
            
            // Open Synty Passport button
            var openBtn = new Button();
            openBtn.text = "Open Synty Importer";
            openBtn.style.height = 36;
            openBtn.style.paddingLeft = 32;
            openBtn.style.paddingRight = 32;
            // Button + text colors: use the admin-configured sale colors when set, else default orange/black.
            openBtn.style.backgroundColor = ParseHexColor(SyntyNotificationService.SaleButtonColor, new Color(0.957f, 0.471f, 0.247f, 1f)); // #F4783F
            openBtn.style.color = ParseHexColor(SyntyNotificationService.SaleButtonTextColor, Color.black);
            openBtn.style.fontSize = 14;
            openBtn.style.unityFontStyleAndWeight = FontStyle.Normal;
            openBtn.style.borderTopLeftRadius = 8;
            openBtn.style.borderTopRightRadius = 8;
            openBtn.style.borderBottomLeftRadius = 8;
            openBtn.style.borderBottomRightRadius = 8;
            openBtn.style.borderTopWidth = 0;
            openBtn.style.borderBottomWidth = 0;
            openBtn.style.borderLeftWidth = 0;
            openBtn.style.borderRightWidth = 0;
            openBtn.style.marginBottom = 6;
            
            // Hover effect
            openBtn.style.transitionProperty = new System.Collections.Generic.List<StylePropertyName> {
                new StylePropertyName("scale"),
                new StylePropertyName("border-top-width"),
                new StylePropertyName("border-bottom-width"),
                new StylePropertyName("border-left-width"),
                new StylePropertyName("border-right-width")
            };
            openBtn.style.transitionDuration = new System.Collections.Generic.List<TimeValue> {
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond)
            };
            openBtn.style.borderTopColor = Color.white;
            openBtn.style.borderBottomColor = Color.white;
            openBtn.style.borderLeftColor = Color.white;
            openBtn.style.borderRightColor = Color.white;
            
            bool openBtnHovering = false;
            openBtn.RegisterCallback<MouseEnterEvent>(evt => {
                openBtnHovering = true;
                openBtn.style.scale = new Scale(new Vector3(1.04f, 1.04f, 1f));
                openBtn.style.borderTopWidth = 2;
                openBtn.style.borderBottomWidth = 2;
                openBtn.style.borderLeftWidth = 2;
                openBtn.style.borderRightWidth = 2;
            });
            openBtn.RegisterCallback<MouseLeaveEvent>(evt => {
                openBtnHovering = false;
                openBtn.style.scale = new Scale(Vector3.one);
                openBtn.style.borderTopWidth = 0;
                openBtn.style.borderBottomWidth = 0;
                openBtn.style.borderLeftWidth = 0;
                openBtn.style.borderRightWidth = 0;
            });
            // Slow, subtle "breathing" scale pulse to draw the eye. Pauses while hovered so the hover
            // scale/border take over cleanly, and resumes on leave. Driven off EditorApplication.update
            // (with explicit cleanup in OnDisable/OnDestroy) — the UI Toolkit scheduler throws on unschedule
            // when the window closes.
            double openPulseStart = EditorApplication.timeSinceStartup;
            EditorApplication.update -= _pulseTick;
            _pulseTick = () => {
                if (openBtn == null || openBtn.panel == null || openBtnHovering) return;
                float s = 1f + 0.025f * Mathf.Sin((float)(EditorApplication.timeSinceStartup - openPulseStart) * 1.6f);
                openBtn.style.scale = new Scale(new Vector3(s, s, 1f));
            };
            EditorApplication.update += _pulseTick;
            
            openBtn.clicked += () =>
            {
                SyntyAnalytics.TrackSaleOpenTool(_saleStage, _saleTitle);
                EditorWindow.GetWindow<SyntyAssetDownloaderV2>(false, "Synty Importer");
                SyntyNotificationService.DismissSale();
                Close();
            };
            openBtn.AddToClassList("link-cursor");
            buttonOverlay.Add(openBtn);
            
            // Close button
            var closeBtn = new Button();
            closeBtn.text = "Close";
            closeBtn.AddToClassList("link-cursor");
            closeBtn.style.height = 28;
            closeBtn.style.paddingLeft = 20;
            closeBtn.style.paddingRight = 20;
            closeBtn.style.backgroundColor = Color.black;
            closeBtn.style.color = new Color(1f, 1f, 1f, 0.7f);
            closeBtn.style.fontSize = 11;
            closeBtn.style.borderTopLeftRadius = 6;
            closeBtn.style.borderTopRightRadius = 6;
            closeBtn.style.borderBottomLeftRadius = 6;
            closeBtn.style.borderBottomRightRadius = 6;
            closeBtn.style.borderTopWidth = 0;
            closeBtn.style.borderBottomWidth = 0;
            closeBtn.style.borderLeftWidth = 0;
            closeBtn.style.borderRightWidth = 0;
            
            closeBtn.style.transitionProperty = new System.Collections.Generic.List<StylePropertyName> {
                new StylePropertyName("scale"),
                new StylePropertyName("border-top-width"),
                new StylePropertyName("border-bottom-width"),
                new StylePropertyName("border-left-width"),
                new StylePropertyName("border-right-width")
            };
            closeBtn.style.transitionDuration = new System.Collections.Generic.List<TimeValue> {
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond),
                new TimeValue(120, TimeUnit.Millisecond)
            };
            closeBtn.style.borderTopColor = Color.white;
            closeBtn.style.borderBottomColor = Color.white;
            closeBtn.style.borderLeftColor = Color.white;
            closeBtn.style.borderRightColor = Color.white;
            
            closeBtn.RegisterCallback<MouseEnterEvent>(evt => {
                closeBtn.style.scale = new Scale(new Vector3(1.04f, 1.04f, 1f));
                closeBtn.style.borderTopWidth = 2;
                closeBtn.style.borderBottomWidth = 2;
                closeBtn.style.borderLeftWidth = 2;
                closeBtn.style.borderRightWidth = 2;
            });
            closeBtn.RegisterCallback<MouseLeaveEvent>(evt => {
                closeBtn.style.scale = new Scale(Vector3.one);
                closeBtn.style.borderTopWidth = 0;
                closeBtn.style.borderBottomWidth = 0;
                closeBtn.style.borderLeftWidth = 0;
                closeBtn.style.borderRightWidth = 0;
            });
            
            closeBtn.clicked += () =>
            {
                SyntyNotificationService.DismissSale();
                Close();
            };
            buttonOverlay.Add(closeBtn);
            
            // Load font
            var font = LoadFont("Newake");
            if (font != null)
            {
                openBtn.style.unityFontDefinition = FontDefinition.FromFont(font);
                closeBtn.style.unityFontDefinition = FontDefinition.FromFont(font);
            }
        }
        
        // Parse a #RRGGBB / #RRGGBBAA hex string to a Color; returns the fallback if blank/invalid.
        private static Color ParseHexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string s = hex.Trim();
            if (!s.StartsWith("#")) s = "#" + s;
            return ColorUtility.TryParseHtmlString(s, out var c) ? c : fallback;
        }

        // Loads the package stylesheet (which defines ".link-cursor { cursor: link; }") so the sale
        // window can show the finger cursor on its buttons + image without duplicating the rule.
        private static StyleSheet LoadLinkCursorSheet()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:StyleSheet SyntyAssetDownloaderV2"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.EndsWith("SyntyAssetDownloaderV2.uss"))
                    return AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            }
            return null;
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
        
        private void OnDisable()
        {
            if (_pulseTick != null) { EditorApplication.update -= _pulseTick; _pulseTick = null; }
            // Abort any in-flight image request so its native GC handle isn't released
            // across a domain reload ("Release of invalid GC handle" warning)
            if (_imageRequest != null)
            {
                try { _imageRequest.Abort(); _imageRequest.Dispose(); } catch { }
                _imageRequest = null;
            }
        }
        
        private void OnDestroy()
        {
            if (_pulseTick != null) { EditorApplication.update -= _pulseTick; _pulseTick = null; }
            if (_imageRequest != null)
            {
                try { _imageRequest.Dispose(); } catch { }
                _imageRequest = null;
            }
            
            if (_adTexture != null)
            {
                DestroyImmediate(_adTexture);
                _adTexture = null;
            }
        }
    }
}
