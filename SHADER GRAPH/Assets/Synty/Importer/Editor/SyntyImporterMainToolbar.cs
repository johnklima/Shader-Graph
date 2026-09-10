#if UNITY_6000_3_OR_NEWER
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Toolbars;
using UnityEngine;

namespace Synty.Passport.Editor
{
    // Unity 6.3+ replaced the internal-reflection main toolbar with an official extensibility API,
    // so the legacy injection in SyntyToolbarButton (m_Root / ToolbarZone* reflection) no longer
    // works on those versions. This registers the Synty Importer button through the official
    // MainToolbarElement API instead. Pre-6.3 keeps using the reflection path.
    //
    // NOTE: the official API only exposes basic elements (icon + text + click). The custom colours,
    // Newake font, and the first-run / notification badge from the legacy toolbar button are not
    // available here — on 6.3+ the button is a standard toolbar button.
    internal static class SyntyImporterMainToolbar
    {
        [MainToolbarElement("Synty/Synty Importer", defaultDockPosition = MainToolbarDockPosition.Middle)]
        public static IEnumerable<MainToolbarElement> CreateSyntyImporterButton()
        {
            // Respect the tool's "Off" toolbar setting (position 3). Yielding nothing hides it;
            // users can also show/hide/reorder it via Unity's own toolbar customization on 6.3+.
            if (SyntyToolbarButton.ToolbarPosition == 3)
                yield break;

            // Text-only content — no icon. (The previous project-texture lookup could match an
            // unrelated Synty asset texture and render a garbled icon.) A clean label reads better;
            // supply a real logo Texture2D here if you have one you want to use.
            var content = new MainToolbarContent("Synty Importer", "Open the Synty Importer");
            yield return new MainToolbarButton(content, () => SyntyToolbarButton.OpenFromMainToolbar());
        }
    }
}
#endif
