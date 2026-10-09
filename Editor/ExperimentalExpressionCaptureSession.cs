using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Experimental, opt-in window only. The ordinary exporter has no dependency on this session.
    internal sealed class ExperimentalExpressionCaptureSession : IDisposable
    {
        [Serializable]
        internal sealed class Row
        {
            public string Path;
            public string[] Shapes;
            public float[] Weights;
        }

        [Serializable]
        internal sealed class Pose
        {
            public string Name;
            public List<Row> Rows = new List<Row>();
        }

        [Serializable]
        internal sealed class Settings
        {
            public int Version = 1;
            public string[] MeshHashes;
            public Pose Baseline;
            public List<Pose> Expressions = new List<Pose>();
        }

        internal sealed class Channel
        {
            internal SkinnedMeshRenderer Renderer;
            internal Mesh Mesh;
            internal string Path;
            internal string[] Shapes;
            internal string MeshHash;
        }

        internal readonly GameObject Source;
        internal GameObject Copy { get; private set; }
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<Channel> Channels = new List<Channel>();
        internal readonly List<Pose> Expressions = new List<Pose>();
        internal VrChatExpressionMenu.Source Candidates { get; private set; }
        internal Pose Baseline { get; private set; }
        readonly List<Mesh> ownedMeshes = new List<Mesh>();
        NdmfExportPreparation preparation;
        Hash128 sourceStamp;
        Pose preparedRest;
        bool disposed;
        internal const int MaximumExpressions = 64;

        internal ExperimentalExpressionCaptureSession(GameObject source)
        {
            Source = source;
            try
            {
                ExportRendererSelection.RequireActiveRoot(source);
                Copy = Object.Instantiate(source);
                Copy.name = source.name;
                Copy.hideFlags = HideFlags.HideAndDontSave;
                using (var exclusions = new ExportObjectExclusions(source, Array.Empty<GameObject>()))
                    MaAppearanceSnapshot.Apply(source, Copy, ownedMeshes, exclusions, Warnings);
                PoseExportSession.RemoveAplFromCopy(source, Copy);
                // As with the blink preview, keep Transforming's committed meshes and FX.
                // Optimizing is deliberately deferred until generated expression targets exist.
                preparation = NdmfExportPreparation.Prepare(source, Copy, Warnings);
                foreach (var behaviour in Copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var renderer in ExportRendererSelection.Enumerate(Copy).OfType<SkinnedMeshRenderer>())
                {
                    var mesh = renderer.sharedMesh;
                    if (mesh == null || mesh.blendShapeCount == 0) continue;
                    var path = AnimationUtility.CalculateTransformPath(renderer.transform, Copy.transform);
                    if (VrChatExpressionSampler.FindRenderer(Copy, path) != renderer)
                        throw new InvalidOperationException("表情のメッシュを一意に特定できません。");
                    renderer.forceMatrixRecalculationPerRender = true;
                    Channels.Add(new Channel { Renderer = renderer, Mesh = mesh, Path = path,
                        Shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray(),
                        MeshHash = MeshStamp(mesh) });
                }
                if (Channels.Count == 0) throw new InvalidOperationException("表示中のメッシュにBlendShapeがありません。");
                preparedRest = Snapshot("準備後の顔");
                Baseline = Snapshot("基準の顔");
                // Settle source read caches before guarding subsequent edits.
                sourceStamp = ExportSourceFingerprint.Compute(source);
            }
            catch { Dispose(); throw; }
        }

        void RequireValid()
        {
            if (disposed || Copy == null) throw new InvalidOperationException("表情記録のコピーがありません。");
            if (Source == null || ExportSourceFingerprint.Compute(Source) != sourceStamp)
                throw new InvalidOperationException("元のアバターが変更されました。設定を保存し、コピーを作り直してください。");
            foreach (var channel in Channels)
                if (channel.Renderer == null || !channel.Renderer.enabled || !channel.Renderer.gameObject.activeInHierarchy ||
                    channel.Renderer.sharedMesh != channel.Mesh || MeshStamp(channel.Mesh) != channel.MeshHash)
                    throw new InvalidOperationException("記録用メッシュが変更されました。コピーを作り直してください。");
        }

        internal void DiscoverCandidates()
        {
            RequireValid();
            var current = Snapshot("現在の顔");
            try
            {
                ApplyUnchecked(preparedRest);
                var result = VrChatExpressionSampler.Analyze(Copy);
                Candidates = result;
            }
            finally { ApplyUnchecked(current); }
        }

        internal void PreviewCandidate(VrChatExpressionMenu.Entry entry, double time = 0)
        {
            RequireValid();
            if (Candidates == null || !Candidates.Entries.Contains(entry))
                throw new InvalidOperationException("このコピーから取得した表情候補を選んでください。");
            if (entry.Error != null) throw new InvalidOperationException(entry.Error);
            if (entry.Unevaluated.Count != 0) throw new InvalidOperationException("未解決の変形がある候補は記録できません。");
            if (double.IsNaN(time) || double.IsInfinity(time) || time < 0 || time > entry.Duration)
                throw new InvalidOperationException("表情の時刻が範囲外です。");
            // Build and validate first. A missing channel must not partially change the preview.
            var pose = ClonePose(preparedRest);
            foreach (var value in entry.Values) Set(pose, value.Path, value.Shape, value.Weight);
            foreach (var value in entry.Animation) Set(pose, value.Path, value.Shape, (float)value.Curve.Evaluate(time));
            ValidatePose(pose);
            ApplyUnchecked(pose);
        }

        static void Set(Pose pose, string path, string shape, float weight)
        {
            var row = pose.Rows.SingleOrDefault(value => value.Path == path);
            var index = row == null ? -1 : Array.IndexOf(row.Shapes, shape);
            if (index < 0) throw new InvalidOperationException("候補のBlendShapeが表示中のメッシュにありません: " + path + " / " + shape);
            row.Weights[index] = weight;
        }

        internal void SetWeight(int channel, int shape, float value)
        {
            if (!Finite(value)) throw new InvalidOperationException("変形量に有限の数値を入力してください。");
            var target = Channels[channel];
            if (disposed || target.Renderer == null || target.Renderer.sharedMesh != target.Mesh)
                throw new InvalidOperationException("記録用メッシュがありません。");
            target.Renderer.SetBlendShapeWeight(shape, value);
        }

        internal void CaptureBaseline()
        {
            RequireValid();
            Baseline = Snapshot("基準の顔");
        }

        internal void RestoreBaseline()
        {
            RequireValid();
            ApplyUnchecked(Baseline);
        }

        internal Pose Capture(string name)
        {
            RequireValid();
            name = ValidateName(name);
            if (Expressions.Count >= MaximumExpressions) throw new InvalidOperationException("記録できる表情は64件までです。");
            if (Expressions.Any(pose => pose.Name == name)) throw new InvalidOperationException("同じ名前の表情があります。別の名前を入力してください。");
            var pose = Snapshot(name);
            if (!HasGeometryChange(pose)) throw new InvalidOperationException("基準の顔と形状が同じです。変形を調整してから記録してください。");
            Expressions.Add(pose);
            return pose;
        }

        internal void PreviewRecorded(Pose pose)
        {
            RequireValid();
            if (!Expressions.Contains(pose)) throw new InvalidOperationException("記録した表情を選んでください。");
            ValidatePose(pose);
            ApplyUnchecked(pose);
        }

        Pose Snapshot(string name)
        {
            var pose = new Pose { Name = name };
            foreach (var channel in Channels)
            {
                var weights = Enumerable.Range(0, channel.Shapes.Length).Select(channel.Renderer.GetBlendShapeWeight).ToArray();
                if (weights.Any(weight => !Finite(weight))) throw new InvalidOperationException("BlendShapeに不正な変形量があります。");
                pose.Rows.Add(new Row { Path = channel.Path, Shapes = (string[])channel.Shapes.Clone(), Weights = weights });
            }
            return pose;
        }

        void ValidatePose(Pose pose)
        {
            if (pose == null || pose.Rows == null || pose.Rows.Count != Channels.Count)
                throw new InvalidOperationException("記録設定のメッシュ数が一致しません。");
            for (var i = 0; i < Channels.Count; i++)
            {
                var row = pose.Rows[i]; var channel = Channels[i];
                if (row == null || row.Path != channel.Path || row.Shapes == null || !row.Shapes.SequenceEqual(channel.Shapes) ||
                    row.Weights == null || row.Weights.Length != channel.Shapes.Length || row.Weights.Any(weight => !Finite(weight)))
                    throw new InvalidOperationException("記録設定の参照・変形量が一致しません。");
            }
        }

        void ApplyUnchecked(Pose pose)
        {
            for (var i = 0; i < Channels.Count; i++)
                for (var shape = 0; shape < pose.Rows[i].Weights.Length; shape++)
                    Channels[i].Renderer.SetBlendShapeWeight(shape, pose.Rows[i].Weights[shape]);
        }

        bool HasGeometryChange(Pose pose)
        {
            var clamp = PlayerSettings.legacyClampBlendShapeWeights;
            for (var i = 0; i < Channels.Count; i++)
            {
                if (Baseline.Rows[i].Weights.SequenceEqual(pose.Rows[i].Weights)) continue;
                var mesh = Object.Instantiate(Channels[i].Mesh);
                try
                {
                    AvatarBaseShape.AppendExpression(Channels[i].Mesh, mesh, "__CaptureProbe_" + Guid.NewGuid().ToString("N"),
                        Baseline.Rows[i].Weights, pose.Rows[i].Weights, clamp);
                    if (AvatarBaseShape.HasUsableMorphEndpoint(mesh, mesh.blendShapeCount - 1, 0, 100)) return true;
                }
                finally { Object.DestroyImmediate(mesh); }
            }
            return false;
        }

        internal string SaveSettings()
        {
            // Recorded arrays remain useful recovery data after source edits.
            // Applying or exporting them still requires all identity checks.
            if (disposed) throw new InvalidOperationException("表情記録のコピーがありません。");
            return JsonUtility.ToJson(new Settings { MeshHashes = Channels.Select(channel => channel.MeshHash).ToArray(),
                Baseline = ClonePose(Baseline), Expressions = Expressions.Select(ClonePose).ToList() }, true);
        }

        internal void LoadSettings(string json)
        {
            RequireValid();
            if (json == null || json.Length > 8 * 1024 * 1024) throw new InvalidOperationException("記録設定のサイズが不正です。");
            Settings settings;
            try { settings = JsonUtility.FromJson<Settings>(json); }
            catch (Exception error) { throw new InvalidOperationException("記録設定を読み込めません。", error); }
            if (settings == null || settings.Version != 1 || settings.MeshHashes == null ||
                !settings.MeshHashes.SequenceEqual(Channels.Select(channel => channel.MeshHash)) || settings.Expressions == null ||
                settings.Expressions.Count > MaximumExpressions)
                throw new InvalidOperationException("記録設定の形式またはメッシュが一致しません。");
            ValidatePose(settings.Baseline);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in settings.Expressions)
            {
                ValidatePose(pose);
                if (ValidateName(pose.Name) != pose.Name || !names.Add(pose.Name))
                    throw new InvalidOperationException("記録設定の表情名が重複または不正です。");
            }
            // Publish only after validating every row and expression.
            Baseline = ClonePose(settings.Baseline);
            Expressions.Clear(); Expressions.AddRange(settings.Expressions.Select(ClonePose));
            ApplyUnchecked(Baseline);
        }

        internal byte[] Export(string avatarName, string author, ICollection<string> warnings = null, BlinkExportOptions blinkOptions = null,
            AvatarLicenseOptions licenseOptions = null)
        {
            RequireValid();
            if (Expressions.Count == 0) throw new InvalidOperationException("表情を一つ以上記録してください。");
            ValidatePose(Baseline);
            foreach (var pose in Expressions)
            {
                ValidatePose(pose);
                if (!HasGeometryChange(pose)) throw new InvalidOperationException(pose.Name + ": 基準の顔と形状が同じです。");
            }
            var exportCopy = Object.Instantiate(Copy);
            exportCopy.name = Copy.name;
            exportCopy.hideFlags = HideFlags.HideAndDontSave;
            var meshes = new List<Mesh>();
            var assets = new List<Object>();
            try
            {
                // Source controllers remain on the preview for candidate discovery.
                // This private export copy already contains the selected completed faces.
                // Prevent a second Animator evaluation from replacing the explicit baseline.
                foreach (var descriptor in exportCopy.GetComponents<Component>().Where(component => component != null &&
                    component.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor").ToArray())
                    Object.DestroyImmediate(descriptor);
                foreach (var animator in exportCopy.GetComponentsInChildren<Animator>(true)) animator.runtimeAnimatorController = null;
                // Declare bindings before the normal exporter runs: its authored
                // endpoint and optimization mapping then protect and relocate them.
                // Late registration by morph name cannot survive AAO renaming.
                var instance = exportCopy.GetComponent<Vrm10Instance>() ?? exportCopy.AddComponent<Vrm10Instance>();
                var settings = instance.Vrm != null ? Object.Instantiate(instance.Vrm) : ScriptableObject.CreateInstance<VRM10Object>();
                assets.Add(settings);
                settings.Prefab = null;
                settings.Expression = new VRM10ObjectExpression();
                instance.Vrm = settings;
                var targetPrefix = "__VRVlog_Capture_" + Guid.NewGuid().ToString("N") + "_";
                var serial = 0;
                long addedBytes = 0;
                for (var i = 0; i < Channels.Count; i++)
                {
                    var renderer = VrChatExpressionSampler.FindRenderer(exportCopy, Channels[i].Path);
                    var mesh = Object.Instantiate(Channels[i].Mesh); meshes.Add(mesh); renderer.sharedMesh = mesh;
                    for (var shape = 0; shape < Baseline.Rows[i].Weights.Length; shape++)
                        renderer.SetBlendShapeWeight(shape, Baseline.Rows[i].Weights[shape]);
                }
                foreach (var pose in Expressions)
                {
                    var expression = ScriptableObject.CreateInstance<VRM10Expression>(); assets.Add(expression);
                    expression.name = "VRChat / 記録 / " + pose.Name;
                    expression.IsBinary = true;
                    expression.OverrideBlink = expression.OverrideMouth = expression.OverrideLookAt = UniGLTF.Extensions.VRMC_vrm.ExpressionOverrideType.block;
                    var bindings = new List<MorphTargetBinding>();
                    for (var i = 0; i < Channels.Count; i++)
                    {
                        if (Baseline.Rows[i].Weights.SequenceEqual(pose.Rows[i].Weights)) continue;
                        var renderer = VrChatExpressionSampler.FindRenderer(exportCopy, Channels[i].Path);
                        var target = targetPrefix + serial++;
                        addedBytes += Channels[i].Mesh.vertexCount * 36L;
                        if (addedBytes > 128L * 1024 * 1024) throw new InvalidOperationException("追加表情の形状データが128 MiBを超えます。");
                        AvatarBaseShape.AppendExpression(Channels[i].Mesh, renderer.sharedMesh, target,
                            Baseline.Rows[i].Weights, pose.Rows[i].Weights, PlayerSettings.legacyClampBlendShapeWeights);
                        renderer.SetBlendShapeWeight(renderer.sharedMesh.blendShapeCount - 1, 0);
                        bindings.Add(new MorphTargetBinding(Channels[i].Path, renderer.sharedMesh.blendShapeCount - 1, 1f));
                    }
                    expression.MorphTargetBindings = bindings.ToArray();
                    settings.Expression.CustomClips.Add(expression);
                }
                return UniVrmOneClickExporter.Export(exportCopy, avatarName, author, warnings,
                    exporterVersion: UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UniVrmOneClickExporter).Assembly)?.version ?? "0.11.14",
                    lilToonVersion: "2.3.4", gimmickOptions: new ExportGimmickOptions { AutoExclude = false }, blinkOptions: blinkOptions,
                    licenseOptions: licenseOptions ?? new AvatarLicenseOptions());
            }
            finally
            {
                Object.DestroyImmediate(exportCopy);
                foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
                foreach (var asset in assets) Object.DestroyImmediate(asset);
            }
        }

        static string ValidateName(string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 128 || name.Any(char.IsControl))
                throw new InvalidOperationException("表情名は制御文字を含まない1〜128文字で入力してください。");
            return name;
        }

        static Pose ClonePose(Pose pose) => new Pose { Name = pose.Name, Rows = pose.Rows.Select(row => new Row {
            Path = row.Path, Shapes = (string[])row.Shapes.Clone(), Weights = (float[])row.Weights.Clone() }).ToList() };

        static string MeshStamp(Mesh mesh)
        {
            using var hash = new ExportSourceFingerprint.Digest();
            hash.Integer(mesh.vertexCount); hash.Integer(mesh.blendShapeCount);
            hash.Integer(mesh.subMeshCount);
            for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                hash.Integer((int)mesh.GetTopology(submesh));
                var indices = mesh.GetIndices(submesh); hash.Integer(indices.Length);
                foreach (var index in indices) hash.Integer(index);
            }
            foreach (var vertex in mesh.vertices) hash.Vector(vertex);
            foreach (var normal in mesh.normals) hash.Vector(normal);
            foreach (var tangent in mesh.tangents) hash.Vector(tangent);
            var vertices = new Vector3[mesh.vertexCount]; var normals = new Vector3[mesh.vertexCount]; var tangents = new Vector3[mesh.vertexCount];
            for (var shape = 0; shape < mesh.blendShapeCount; shape++)
            {
                hash.Text(mesh.GetBlendShapeName(shape)); hash.Integer(mesh.GetBlendShapeFrameCount(shape));
                for (var frame = 0; frame < mesh.GetBlendShapeFrameCount(shape); frame++)
                {
                    hash.Float(mesh.GetBlendShapeFrameWeight(shape, frame));
                    mesh.GetBlendShapeFrameVertices(shape, frame, vertices, normals, tangents);
                    for (var vertex = 0; vertex < vertices.Length; vertex++)
                    { hash.Vector(vertices[vertex]); hash.Vector(normals[vertex]); hash.Vector(tangents[vertex]); }
                }
            }
            return hash.Finish().ToString();
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Copy != null) Object.DestroyImmediate(Copy);
            Copy = null;
            preparation?.Dispose(); preparation = null;
            foreach (var mesh in ownedMeshes) if (mesh != null) Object.DestroyImmediate(mesh);
            ownedMeshes.Clear();
        }
    }
}
