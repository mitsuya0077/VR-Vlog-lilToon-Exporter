using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter.Tests
{
    internal static class BlendShapeSettingsTestSupport
    {
        // The native setting can remain stale in an interactive EditMode runner.
        // Queue four updates, then prove the clamped policy on an independent,
        // fresh mesh before constructing a fixture. These native geometry tests
        // run through the GPU-backed batch compatibility profile. The false case
        // declares unlimited exporter math, not native unclamped equivalence.
        internal static async Task WaitForEditorUpdates(int count = 4)
        {
            if (count < 1 || count > 64) throw new ArgumentOutOfRangeException(nameof(count));
            var expectedClamp = PlayerSettings.legacyClampBlendShapeWeights;
            for (var update = 0; update < count; update++)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                await NextEditorUpdate();
                if (PlayerSettings.legacyClampBlendShapeWeights != expectedClamp)
                    throw new InvalidOperationException("Another Editor callback changed the declared blendshape policy while its native adoption was being verified.");
            }
            if (!expectedClamp) return;

            var scene = EditorSceneManager.NewPreviewScene();
            GameObject root = null;
            Mesh mesh = null, baked = null;
            try
            {
                root = new GameObject("Owned native blendshape policy sentinel") { hideFlags = HideFlags.HideAndDontSave };
                SceneManager.MoveGameObjectToScene(root, scene);
                mesh = new Mesh { name = "Owned100-frame policy sentinel", hideFlags = HideFlags.HideAndDontSave,
                    vertices = new[] { Vector3.zero, Vector3.right * .1f, Vector3.up * .1f }, triangles = new[] { 0, 2, 1 },
                    bindposes = new[] { Matrix4x4.identity },
                    boneWeights = new[] { new BoneWeight { boneIndex0 = 0, weight0 = 1 }, new BoneWeight { boneIndex0 = 0, weight0 = 1 },
                        new BoneWeight { boneIndex0 = 0, weight0 = 1 } } };
                mesh.AddBlendShapeFrame("Declared native range", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
                var skin = root.AddComponent<SkinnedMeshRenderer>(); skin.sharedMesh = mesh;
                skin.bones = new[] { root.transform }; skin.rootBone = root.transform;
                baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                skin.SetBlendShapeWeight(0, 150); skin.BakeMesh(baked, false); var positive = baked.vertices[0].y;
                skin.SetBlendShapeWeight(0, -50); skin.BakeMesh(baked, false); var negative = baked.vertices[0].y;
                if (!(Math.Abs(positive - 1f) < .00001f && Math.Abs(negative) < .00001f))
                    throw new InvalidOperationException("Native blendshape clamping has not adopted the declared true policy; BakeMesh at150=" + positive +
                        " and-50=" + negative + ", expected 1 and 0. Run the native geometry cases through the batch compatibility profile.");
            }
            finally
            {
                Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); Object.DestroyImmediate(baked);
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static async Task NextEditorUpdate()
        {
            var completed = new TaskCompletionSource<bool>();
            void Update() => completed.TrySetResult(true);
            EditorApplication.update += Update;
            try { await completed.Task; }
            finally { EditorApplication.update -= Update; }
        }
    }
}
