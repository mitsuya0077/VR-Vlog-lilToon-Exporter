using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    internal static class SkinnedMeshFallbackWeights
    {
        // Unity uses rootBone to render vertices with no bone weights. glTF has
        // no equivalent implicit influence. Measure that Unity transform on the
        // export copy and encode it as an ordinary, explicit joint influence.
        internal static void Preserve(GameObject clone, ICollection<Mesh> ownedMeshes, ICollection<string> warnings,
            ICollection<Transform> fixedRootJoints = null)
        {
            if (ownedMeshes == null) throw new ArgumentNullException(nameof(ownedMeshes));
            foreach (var renderer in ExportRendererSelection.Enumerate(clone))
                if (renderer is SkinnedMeshRenderer skin && skin.sharedMesh != null)
                {
                    RemapOmittedRootJoint(clone.transform, skin, warnings, fixedRootJoints);
                    Preserve(clone.transform, skin, ownedMeshes, warnings, fixedRootJoints);
                }
        }

        private static void RemapOmittedRootJoint(Transform root, SkinnedMeshRenderer skin, ICollection<string> warnings,
            ICollection<Transform> fixedRootJoints)
        {
            var bones = skin.bones;
            if (skin.sharedMesh.vertexCount == 0 || bones == null || Array.IndexOf(bones, root) < 0) return;
            if (skin.sharedMesh.bindposes.Length != bones.Length)
                throw Failure(skin, "ボーンとバインドポーズの数が一致しません。メッシュのスキニング設定を確認してください。");
            RequireInvertible(skin, root.localToWorldMatrix);
            var joint = new GameObject("VR Vlog exported root joint");
            var before = new Mesh();
            var after = new Mesh();
            var bounds = skin.localBounds;
            var retained = false;
            try
            {
                joint.transform.SetParent(root, false);
                skin.BakeMesh(before, false);
                var remapped = (Transform[])bones.Clone();
                for (var i = 0; i < remapped.Length; i++) if (remapped[i] == root) remapped[i] = joint.transform;
                skin.bones = remapped;
                skin.BakeMesh(after, false);
                RequireSameGeometry(skin, before, after);
                fixedRootJoints?.Add(joint.transform);
                retained = true;
                warnings?.Add($"{skin.name}: アバターのルートを参照するボーンを、同じ変換の出力用ジョイントに置き換えました。");
            }
            finally
            {
                if (!retained)
                {
                    skin.bones = bones;
                    UnityEngine.Object.DestroyImmediate(joint);
                }
                skin.localBounds = bounds;
                UnityEngine.Object.DestroyImmediate(before);
                UnityEngine.Object.DestroyImmediate(after);
            }
        }

        private static void Preserve(Transform root, SkinnedMeshRenderer skin, ICollection<Mesh> ownedMeshes, ICollection<string> warnings,
            ICollection<Transform> fixedRootJoints)
        {
            var source = skin.sharedMesh;
            if (source.vertexCount == 0) return;
            // These NativeArrays borrow mesh storage. Copy before assigning a
            // replacement mesh; only arrays allocated below are ours to dispose.
            var sourceCounts = source.GetBonesPerVertex();
            if (sourceCounts.Length != 0 && sourceCounts.Length != source.vertexCount)
                throw Failure(skin, "頂点のボーンウェイト数が一致しません。");
            var counts = new byte[source.vertexCount];
            var zeroCount = 0;
            for (var i = 0; i < counts.Length; i++)
            {
                counts[i] = sourceCounts.Length == 0 ? (byte)0 : sourceCounts[i];
                if (counts[i] == 0) zeroCount++;
            }
            if (zeroCount == 0) return;
            var sourceWeights = source.GetAllBoneWeights().ToArray();
            var weights = new List<BoneWeight1>(sourceWeights.Length + zeroCount);
            var bones = skin.bones ?? Array.Empty<Transform>();
            var bindposes = source.bindposes;
            if (bindposes.Length != bones.Length)
                throw Failure(skin, "ボーンとバインドポーズの数が一致しません。メッシュのスキニング設定を確認してください。");
            var anchor = skin.rootBone != null ? skin.rootBone : skin.transform;
            if (anchor != root && !anchor.IsChildOf(root))
                throw Failure(skin, "Root Bone がアバターの外部を参照しています。アバター内の追従先を設定してください。");
            if (skin.GetComponent<Cloth>() != null)
                throw Failure(skin, "ウェイトのない頂点と Cloth の組み合わせは、位置を固定したスキニングに変換できません。");
            RequireInvertible(skin, skin.transform.localToWorldMatrix);
            RequireInvertible(skin, anchor.localToWorldMatrix);
            // UniVRM excludes the selected avatar root from its exported nodes.
            // Give that anchor an equivalent child joint that survives export.
            GameObject rootJoint = null;
            if (anchor == root)
            {
                rootJoint = new GameObject("VR Vlog fallback anchor");
                rootJoint.transform.SetParent(root, false);
                anchor = rootJoint.transform;
            }
            var jointRetained = false;
            try
            {
                var newBones = new Transform[bones.Length + 1];
                Array.Copy(bones, newBones, bones.Length);
                newBones[bones.Length] = anchor;
                var newBindposes = new Matrix4x4[bindposes.Length + 1];
                Array.Copy(bindposes, newBindposes, bindposes.Length);
                newBindposes[bindposes.Length] = MeasureBindpose(skin, source, bones, newBones, bindposes);
                var readIndex = 0;
                for (var i = 0; i < counts.Length; i++)
                {
                    if (counts[i] == 0)
                    {
                        counts[i] = 1;
                        weights.Add(new BoneWeight1 { boneIndex = bones.Length, weight = 1f });
                    }
                    else for (var j = 0; j < counts[i]; j++)
                    {
                        if (readIndex >= sourceWeights.Length) throw Failure(skin, "ボーンウェイトのデータが不足しています。");
                        weights.Add(sourceWeights[readIndex++]);
                    }
                }
                if (readIndex != sourceWeights.Length) throw Failure(skin, "ボーンウェイトのデータ数が一致しません。");

                Mesh replacement = null;
                var before = new Mesh();
                var after = new Mesh();
                var blendWeights = ReadBlendWeights(skin, source);
                var bounds = skin.localBounds;
                var completed = false;
                try
                {
                    skin.BakeMesh(before, false);
                    replacement = UnityEngine.Object.Instantiate(source);
                    replacement.name = source.name + " (explicit fallback joint)";
                    replacement.bindposes = newBindposes;
                    using (var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp))
                    using (var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp))
                        replacement.SetBoneWeights(nativeCounts, nativeWeights);
                    skin.bones = newBones;
                    skin.sharedMesh = replacement;
                    RestoreBlendWeights(skin, blendWeights);
                    skin.BakeMesh(after, false);
                    RequireSameGeometry(skin, before, after);
                    if (rootJoint != null) fixedRootJoints?.Add(rootJoint.transform);
                    ownedMeshes.Add(replacement);
                    completed = true;
                    jointRetained = true;
                    warnings?.Add($"{skin.name}: ウェイトのない {zeroCount} 頂点の配置を、Root Bone に追従するウェイトとして保持しました。");
                }
                finally
                {
                    if (!completed)
                    {
                        skin.bones = bones;
                        skin.sharedMesh = source;
                        RestoreBlendWeights(skin, blendWeights);
                        UnityEngine.Object.DestroyImmediate(replacement);
                    }
                    skin.localBounds = bounds;
                    UnityEngine.Object.DestroyImmediate(before);
                    UnityEngine.Object.DestroyImmediate(after);
                }
            }
            finally
            {
                if (!jointRetained) UnityEngine.Object.DestroyImmediate(rootJoint);
            }
        }

        private static Matrix4x4 MeasureBindpose(SkinnedMeshRenderer skin, Mesh source, Transform[] bones,
            Transform[] newBones, Matrix4x4[] bindposes)
        {
            var probe = new Mesh { name = "VR Vlog temporary skin calibration", hideFlags = HideFlags.HideAndDontSave };
            var zeroPose = new Mesh();
            var identityPose = new Mesh();
            var explicitPose = new Mesh();
            var blendWeights = ReadBlendWeights(skin, source);
            var bounds = skin.localBounds;
            try
            {
                probe.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
                probe.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                probe.normals = new[] { new Vector3(1, 2, 3).normalized, Vector3.right, Vector3.up, Vector3.forward };
                probe.tangents = new[] { new Vector4(0, 0.8320503f, -0.5547002f, 1), new Vector4(0, 1, 0, -1), new Vector4(0, 0, 1, 1), new Vector4(1, 0, 0, -1) };
                probe.bindposes = bindposes;
                probe.boneWeights = new BoneWeight[4];
                if (source.blendShapeCount > 0)
                    probe.AddBlendShapeFrame("calibration", 100f, new Vector3[4], new Vector3[4], new Vector3[4]);
                skin.sharedMesh = probe;
                if (probe.blendShapeCount > 0) skin.SetBlendShapeWeight(0, 0f);
                skin.BakeMesh(zeroPose, false);

                var probeBinds = new Matrix4x4[newBones.Length];
                Array.Copy(bindposes, probeBinds, bindposes.Length);
                probeBinds[bones.Length] = Matrix4x4.identity;
                probe.bindposes = probeBinds;
                var probeWeights = new BoneWeight[4];
                for (var i = 0; i < probeWeights.Length; i++)
                    probeWeights[i] = new BoneWeight { boneIndex0 = bones.Length, weight0 = 1f };
                probe.boneWeights = probeWeights;
                skin.bones = newBones;
                skin.BakeMesh(identityPose, false);
                var implicitTransform = Basis(skin, zeroPose);
                var explicitTransform = Basis(skin, identityPose);
                RequireInvertible(skin, implicitTransform);
                RequireInvertible(skin, explicitTransform);
                var bindpose = explicitTransform.inverse * implicitTransform;
                RequireInvertible(skin, bindpose);
                probeBinds[bones.Length] = bindpose;
                probe.bindposes = probeBinds;
                skin.BakeMesh(explicitPose, false);
                RequireSameGeometry(skin, zeroPose, explicitPose);
                return bindpose;
            }
            finally
            {
                skin.bones = bones;
                skin.sharedMesh = source;
                RestoreBlendWeights(skin, blendWeights);
                skin.localBounds = bounds;
                UnityEngine.Object.DestroyImmediate(probe);
                UnityEngine.Object.DestroyImmediate(zeroPose);
                UnityEngine.Object.DestroyImmediate(identityPose);
                UnityEngine.Object.DestroyImmediate(explicitPose);
            }
        }

        private static Matrix4x4 Basis(SkinnedMeshRenderer skin, Mesh baked)
        {
            var p = baked.vertices;
            if (p.Length != 4) throw Failure(skin, "Unity から位置の検証用メッシュを取得できませんでした。");
            var result = Matrix4x4.identity;
            result.SetColumn(0, p[1] - p[0]);
            result.SetColumn(1, p[2] - p[0]);
            result.SetColumn(2, p[3] - p[0]);
            result.SetColumn(3, new Vector4(p[0].x, p[0].y, p[0].z, 1f));
            return result;
        }

        private static void RequireInvertible(SkinnedMeshRenderer skin, Matrix4x4 matrix)
        {
            for (var i = 0; i < 16; i++) if (!Finite(matrix[i])) throw Failure(skin, "追従先の変換行列に無効な値があります。");
            var inverse = matrix.inverse;
            for (var i = 0; i < 16; i++) if (!Finite(inverse[i])) throw Failure(skin, "追従先の変換を反転できません。");
            var identity = matrix * inverse;
            for (var i = 0; i < 16; i++)
                if (Mathf.Abs(identity[i] - Matrix4x4.identity[i]) > 0.002f)
                    throw Failure(skin, "追従先のスケールがゼロ、または位置を保持できない変換です。");
        }

        private static void RequireSameGeometry(SkinnedMeshRenderer skin, Mesh before, Mesh after)
        {
            if (before.vertexCount == 0) throw Failure(skin, "Unity から検証用の頂点を取得できませんでした。");
            SameVectors(skin, before.vertices, after.vertices, "位置");
            SameVectors(skin, before.normals, after.normals, "法線");
            var a = before.tangents;
            var b = after.tangents;
            if (a.Length != b.Length) throw Failure(skin, "接線の検証データ数が一致しません。");
            for (var i = 0; i < a.Length; i++)
                for (var j = 0; j < 4; j++)
                    if (!Close(a[i][j], b[i][j])) throw Failure(skin, "接線を保持できないスキニングです。");
        }

        private static void SameVectors(SkinnedMeshRenderer skin, Vector3[] a, Vector3[] b, string label)
        {
            if (a.Length != b.Length) throw Failure(skin, label + "の検証データ数が一致しません。");
            for (var i = 0; i < a.Length; i++)
                for (var j = 0; j < 3; j++)
                    if (!Close(a[i][j], b[i][j])) throw Failure(skin, label + "を保持できないスキニングです。");
        }

        private static bool Close(float a, float b) => Finite(a) && Finite(b) && Mathf.Abs(a - b) <= 0.0002f * Mathf.Max(1f, Mathf.Abs(a));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static float[] ReadBlendWeights(SkinnedMeshRenderer skin, Mesh mesh)
        {
            var values = new float[mesh.blendShapeCount];
            for (var i = 0; i < values.Length; i++) values[i] = skin.GetBlendShapeWeight(i);
            return values;
        }
        private static void RestoreBlendWeights(SkinnedMeshRenderer skin, float[] values)
        {
            for (var i = 0; i < values.Length; i++) skin.SetBlendShapeWeight(i, values[i]);
        }
        private static InvalidOperationException Failure(SkinnedMeshRenderer skin, string reason) =>
            new InvalidOperationException($"{skin.name}: スキンメッシュの配置を保持できません。{reason}");
    }
}
