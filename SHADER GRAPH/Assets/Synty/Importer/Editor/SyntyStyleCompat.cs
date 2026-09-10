using UnityEngine;
using UnityEngine.UIElements;

namespace Synty.Passport.Compat
{
    /// <summary>
    /// Compatibility helpers that keep the tool compiling cleanly across every Unity
    /// version from 2021 through Unity 6 and later.
    ///
    /// Background image scaling moved API in Unity 2022.2: the old
    /// IStyle.unityBackgroundScaleMode was deprecated in favour of the background-* set
    /// (backgroundSize / backgroundPosition / backgroundRepeat). The old property still
    /// works on newer Unity but emits a CS0618 deprecation warning, and the new property
    /// does not exist on 2021 at all. Because this tool ships to customers on unknown
    /// Unity versions, we cannot pick one API unconditionally.
    ///
    /// These extension methods select the correct API at COMPILE time via Unity's
    /// version define symbols, so each Unity version only ever compiles the code that is
    /// valid for it: no deprecation warnings on new Unity, no missing-type errors on old
    /// Unity. Call sites read almost identically to a direct property set, e.g.
    ///     logo.style.SetBackgroundScaleToFit();
    ///     art.style.SetBackgroundStretchToFill();
    /// </summary>
    public static class SyntyStyleCompat
    {
        /// <summary>
        /// Scales the background image to fit inside the element while preserving aspect
        /// ratio (equivalent to the old ScaleMode.ScaleToFit).
        /// </summary>
        public static void SetBackgroundScaleToFit(this IStyle style)
        {
#if UNITY_2022_2_OR_NEWER
            style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
#else
            style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
#endif
        }

        /// <summary>
        /// Stretches the background image to fill the element exactly, allowing the image
        /// to distort (equivalent to the old ScaleMode.StretchToFill).
        /// </summary>
        public static void SetBackgroundStretchToFill(this IStyle style)
        {
#if UNITY_2022_2_OR_NEWER
            style.backgroundPositionX = new BackgroundPosition(BackgroundPositionKeyword.Center);
            style.backgroundPositionY = new BackgroundPosition(BackgroundPositionKeyword.Center);
            style.backgroundRepeat = new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat);
            style.backgroundSize = new BackgroundSize(Length.Percent(100), Length.Percent(100));
#else
            style.unityBackgroundScaleMode = ScaleMode.StretchToFill;
#endif
        }
    }
}
