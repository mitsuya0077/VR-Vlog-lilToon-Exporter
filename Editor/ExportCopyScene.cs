using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Keep export edits and cleanup in an owned scene. Processing a temporary
    // root in the avatar's scene can dirty that scene even when the original
    // avatar's serialized contents are intact.
    internal sealed class ExportCopyScene : IDisposable
    {
        internal GameObject Copy { get; private set; }
        Scene scene;
        GameObject staging;

        internal ExportCopyScene(GameObject source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            try
            {
                scene = EditorSceneManager.NewPreviewScene();
                // Apply DontSave at native creation, before this staging root
                // can become a saved object in the user's active scene.
                staging = EditorUtility.CreateGameObjectWithHideFlags(
                    "VRVlog export-copy staging", HideFlags.HideAndDontSave);
                SceneManager.MoveGameObjectToScene(staging, scene);
                // Copy the native local transform under an identity
                // parent, rather than substitute the authored world
                // pose. The parentless native reference in tests
                // verifies position, rotation and scale together.
                Copy = Object.Instantiate(source, staging.transform, false);
                Copy.name = source.name;
                // Detach without changing the copied transform.
                Copy.transform.SetParent(null, true);
                Object.DestroyImmediate(staging);
                staging = null;
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (Copy != null) Object.DestroyImmediate(Copy);
                Copy = null;
                if (staging != null) Object.DestroyImmediate(staging);
                staging = null;
            }
            finally
            {
                if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
                scene = default;
            }
        }
    }
}
