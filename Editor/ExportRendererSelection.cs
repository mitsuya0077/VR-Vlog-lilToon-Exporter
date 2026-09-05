using System;
using System.Collections.Generic;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class ExportRendererSelection
    {
        public static void RequireActiveRoot(GameObject avatar)
        {
            if (avatar == null) throw new ArgumentNullException(nameof(avatar));
            if (!avatar.activeInHierarchy)
                throw new InvalidOperationException("アバター本体と親オブジェクトを有効にしてから書き出してください。");
        }

        public static IEnumerable<Renderer> Enumerate(GameObject avatar)
        {
            RequireActiveRoot(avatar);
            // Match UniVRM 0.131 ModelExporter material collection exactly.
            // Inactive wardrobe objects and disabled renderers have no fallback
            // material to restore; do not validate or convert their materials.
            foreach (var renderer in avatar.GetComponentsInChildren<Renderer>())
                if (renderer.gameObject.activeInHierarchy && renderer.enabled)
                    yield return renderer;
        }
    }
}
