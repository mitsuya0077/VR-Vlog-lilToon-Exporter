using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Explicit expression capture used by the primary VRM export window.
    internal sealed partial class ExperimentalExpressionCaptureSession : IDisposable
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
            public string InstalledSourceId;
            public List<Row> Rows = new List<Row>();
        }

        [Serializable]
        internal sealed class Settings
        {
            public int Version = 1;
            public string[] MeshHashes;
            public Pose Baseline;
            public List<Pose> Expressions = new List<Pose>();
            public List<PoseReference> ManualPoses = new List<PoseReference>();
            public string[] ExcludedPoses;
            public List<PoseNameReference> PoseNames;
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
        internal readonly PoseExportOptions PoseOptions = new PoseExportOptions();
        readonly Dictionary<string, SkinnedMeshRenderer> authoringRenderers = new Dictionary<string, SkinnedMeshRenderer>(StringComparer.Ordinal);
        internal VrChatExpressionMenu.Source Candidates { get; private set; }
        internal Pose Baseline { get; private set; }
        readonly List<Mesh> ownedMeshes = new List<Mesh>();
        NdmfExportPreparation preparation;
        ExperimentalExpressionControllerScope controllers;
        FaceEmoExpressions.BindingSnapshot faceEmoBindings;
        Hash128 sourceStamp;
        Pose preparedRest;
        bool disposed;
        internal const int MaximumExpressions = 64;

        internal ExperimentalExpressionCaptureSession(GameObject source) : this(source, true) { }

        internal ExperimentalExpressionCaptureSession(GameObject source, bool replayInstalledDefaults)
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
                faceEmoBindings = FaceEmoExpressions.Capture(source, Copy, deferPermanentOverrides: true);
                faceEmoBindings.CompletedFacePlayer = (avatar, metadata, state, layer) =>
                    ExperimentalInstalledExpressionPlayer.Sample(avatar, metadata, new Dictionary<string, float>(), state, layer);
                foreach (var group in Copy.GetComponentsInChildren<SkinnedMeshRenderer>(true).GroupBy(renderer =>
                    AnimationUtility.CalculateTransformPath(renderer.transform, Copy.transform), StringComparer.Ordinal))
                    if (group.Count() == 1) authoringRenderers.Add(group.Key, group.Single());
                preparation = NdmfExportPreparation.Prepare(source, Copy, Warnings);
                faceEmoBindings.RebindPrepared(preparation.PreparedRendererFor, preparation.ObjectRegistry, preparation.IsolatedCopyOf);
                if (replayInstalledDefaults) controllers = new ExperimentalExpressionControllerScope(Copy, Warnings);
                foreach (var behaviour in Copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var skin in Copy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                { skin.updateWhenOffscreen = true; skin.forceMatrixRecalculationPerRender = true; }
                foreach (var renderer in ExportRendererSelection.Enumerate(Copy).OfType<SkinnedMeshRenderer>())
                {
                    var mesh = renderer.sharedMesh;
                    if (mesh == null || mesh.blendShapeCount == 0) continue;
                    var path = AnimationUtility.CalculateTransformPath(renderer.transform, Copy.transform);
                    if (VrChatExpressionSampler.FindRenderer(Copy, path) != renderer)
                        throw new InvalidOperationException(ExporterLocalization.T("表情のメッシュを一意に特定できません。"));
                    renderer.forceMatrixRecalculationPerRender = true;
                    Channels.Add(new Channel { Renderer = renderer, Mesh = mesh, Path = path,
                        Shapes = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray(),
                        MeshHash = MeshStamp(mesh) });
                }
                var installed = VrChatExpressionMenu.Read(Copy);
                if (replayInstalledDefaults && installed.Controller != null)
                {
                    NeutralInputProof.Read(Copy, installed);
                    try
                    {
                        var neutral = Snapshot("FXの基準表情");
                        foreach (var value in ExperimentalInstalledExpressionPlayer.Sample(Copy, installed, new Dictionary<string, float>()))
                            Set(neutral, value.Path, value.Shape, value.Weight);
                        ValidatePose(neutral); ApplyUnchecked(neutral);
                    }
                    catch (InvalidOperationException error) { Warnings.Add(ExporterLocalization.T("FXの基準表情を収録できませんでした: ") + error.Message); }
                }
                preparedRest = Snapshot("準備後の顔");
                Baseline = Snapshot("基準の顔");
                // Settle source read caches before guarding subsequent edits.
                sourceStamp = ExportSourceFingerprint.Compute(source);
            }
            catch { Dispose(); throw; }
        }

        void RequireValid()
        {
            if (disposed || Copy == null) throw new InvalidOperationException(ExporterLocalization.T("表情記録のコピーがありません。"));
            if (Source == null || ExportSourceFingerprint.Compute(Source) != sourceStamp)
                throw new InvalidOperationException(ExporterLocalization.T("元のアバターが変更されました。「変更」からアバターを指定し直してください。"));
            foreach (var channel in Channels)
                if (channel.Renderer == null || !channel.Renderer.enabled || !channel.Renderer.gameObject.activeInHierarchy ||
                    channel.Renderer.sharedMesh != channel.Mesh || MeshStamp(channel.Mesh) != channel.MeshHash)
                    throw new InvalidOperationException(ExporterLocalization.T("書き出し用メッシュが変更されました。「変更」からアバターを指定し直してください。"));
        }

        internal void DiscoverCandidates()
        {
            RequireValid();
            if (controllers == null) controllers = new ExperimentalExpressionControllerScope(Copy, Warnings);
            var current = Snapshot("現在の顔");
            try
            {
                ApplyUnchecked(preparedRest);
                var result = VrChatExpressionMenu.Read(Copy);
                NeutralInputProof.Read(Copy, result);
                foreach (var entry in result.Entries)
                {
                    if (entry.Error != null) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("導入済みの表情を収録中", entry.Name,
                        (float)result.Entries.IndexOf(entry) / Mathf.Max(1, result.Entries.Count))) throw new OperationCanceledException();
                    try { entry.Values.AddRange(ExperimentalInstalledExpressionPlayer.Sample(Copy, result, entry.Parameters)); }
                    catch (InvalidOperationException error) { entry.Error = error.Message; }
                }
                // Registered FaceEmo branches can exist outside the avatar root,
                // including gesture variants not exposed by a generated menu.
                FaceEmoExpressions.Add(Copy, result, authoringSource: Source, bindings: faceEmoBindings);
                using (var bindings = new PreparedExpressionBindings(Copy, result))
                    FaceEmoExpressions.ApplyPreparedDefaultFace(Copy, result, bindings, registeredBindings: faceEmoBindings);
                Candidates = result;
            }
            finally { ApplyUnchecked(current); EditorUtility.ClearProgressBar(); }
        }

        internal void PreviewCandidate(VrChatExpressionMenu.Entry entry, double time = 0)
        {
            RequireValid();
            if (Candidates == null || !Candidates.Entries.Contains(entry))
                throw new InvalidOperationException(ExporterLocalization.T("このコピーから取得した表情候補を選んでください。"));
            if (entry.Error != null) throw new InvalidOperationException(entry.Error);
            if (entry.Unevaluated.Count != 0) throw new InvalidOperationException(ExporterLocalization.T("未解決の変形がある候補は記録できません。"));
            if (double.IsNaN(time) || double.IsInfinity(time) || time < 0 || time > entry.Duration)
                throw new InvalidOperationException(ExporterLocalization.T("表情の時刻が範囲外です。"));
            // Build and validate first. A missing channel must not partially change the preview.
            var pose = ClonePose(preparedRest);
            foreach (var value in entry.Values) Set(pose, value.Path, value.Shape, value.Weight);
            foreach (var value in entry.Animation) Set(pose, value.Path, value.Shape, (float)value.Curve.Evaluate(time));
            ValidatePose(pose);
            ApplyUnchecked(pose);
        }

        internal sealed class ImportResult
        {
            internal readonly List<string> Imported = new List<string>();
            internal readonly List<string> Skipped = new List<string>();
        }

        internal bool SelectCandidateByDefault(VrChatExpressionMenu.Entry entry)
        {
            if (entry.Error != null || entry.Unevaluated.Count != 0 || entry.Animation.Count != 0) return false;
            foreach (var value in entry.Values)
            {
                var row = Baseline.Rows.SingleOrDefault(item => item.Path == value.Path);
                var shape = row == null ? -1 : Array.IndexOf(row.Shapes, value.Shape);
                if (shape >= 0 && Mathf.Abs(row.Weights[shape] - value.Weight) > .001f) return true;
            }
            // A named authored neutral face still belongs in the expression
            // catalog; unchanged settings toggles remain unchecked by default.
            var label = entry.Name.Split('/').Last().Trim();
            return entry.Id?.StartsWith("faceemo/", StringComparison.Ordinal) == true ||
                Candidates.Controller != null && Candidates.Controller.animationClips.Any(clip => clip.name == label &&
                    AnimationUtility.GetCurveBindings(clip).Any(binding => binding.type == typeof(SkinnedMeshRenderer) &&
                        binding.propertyName.StartsWith("blendShape.", StringComparison.Ordinal)));
        }

        internal ImportResult ImportCandidates(IEnumerable<VrChatExpressionMenu.Entry> entries)
        {
            RequireValid();
            if (Candidates == null) throw new InvalidOperationException(ExporterLocalization.T("既存の表情を先に読み込んでください。"));
            var result = new ImportResult();
            var current = Snapshot("現在の顔");
            var staged = new List<Pose>();
            var names = new HashSet<string>(Expressions.Select(pose => pose.Name), StringComparer.Ordinal);
            try
            {
                foreach (var entry in entries)
                {
                    if (!Candidates.Entries.Contains(entry)) throw new InvalidOperationException(ExporterLocalization.T("別のコピーの表情が選択されています。"));
                    if (entry.Error != null || entry.Unevaluated.Count > 0)
                    { result.Skipped.Add(entry.Name + ": " + (entry.Error ?? "未解決の変形")); continue; }
                    if (entry.Animation.Count != 0)
                    { result.Skipped.Add(entry.Name + ": 動く表情はプレビューで収録時刻を指定してください。"); continue; }
                    PreviewCandidate(entry);
                    var name = ValidateName(entry.Name);
                    var pose = Snapshot(name);
                    pose.InstalledSourceId = entry.Id;
                    if (names.Contains(name))
                    { result.Skipped.Add(name + ": 同名の表情を登録済みです。"); continue; }
                    if (Expressions.Count + staged.Count >= MaximumExpressions)
                        throw new InvalidOperationException(ExporterLocalization.T("記録できる表情は64件までです。取り込む候補を絞ってください。"));
                    names.Add(name); staged.Add(pose); result.Imported.Add(name);
                }
                // A failure never publishes half an import or changes the preview.
                Expressions.AddRange(staged);
                return result;
            }
            finally { ApplyUnchecked(current); }
        }

        static void Set(Pose pose, string path, string shape, float weight)
        {
            var row = pose.Rows.SingleOrDefault(value => value.Path == path);
            var index = row == null ? -1 : Array.IndexOf(row.Shapes, shape);
            if (index < 0) throw new InvalidOperationException(ExporterLocalization.T("候補のBlendShapeが表示中のメッシュにありません: ") + path + " / " + shape);
            row.Weights[index] = weight;
        }

        internal void SetWeight(int channel, int shape, float value)
        {
            if (!Finite(value)) throw new InvalidOperationException(ExporterLocalization.T("変形量に有限の数値を入力してください。"));
            var target = Channels[channel];
            if (disposed || target.Renderer == null || target.Renderer.sharedMesh != target.Mesh)
                throw new InvalidOperationException(ExporterLocalization.T("記録用メッシュがありません。"));
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
            if (Expressions.Count >= MaximumExpressions) throw new InvalidOperationException(ExporterLocalization.T("記録できる表情は64件までです。"));
            if (Expressions.Any(pose => pose.Name == name)) throw new InvalidOperationException(ExporterLocalization.T("同じ名前の表情があります。別の名前を入力してください。"));
            var pose = Snapshot(name);
            if (!HasGeometryChange(pose)) throw new InvalidOperationException(ExporterLocalization.T("基準の顔と形状が同じです。変形を調整してから記録してください。"));
            Expressions.Add(pose);
            return pose;
        }

        internal void PreviewRecorded(Pose pose)
        {
            RequireValid();
            if (!Expressions.Contains(pose)) throw new InvalidOperationException(ExporterLocalization.T("記録した表情を選んでください。"));
            ValidatePose(pose);
            ApplyUnchecked(pose);
        }

        Pose Snapshot(string name)
        {
            var pose = new Pose { Name = name };
            foreach (var channel in Channels)
            {
                var weights = Enumerable.Range(0, channel.Shapes.Length).Select(channel.Renderer.GetBlendShapeWeight).ToArray();
                if (weights.Any(weight => !Finite(weight))) throw new InvalidOperationException(ExporterLocalization.T("BlendShapeに不正な変形量があります。"));
                pose.Rows.Add(new Row { Path = channel.Path, Shapes = (string[])channel.Shapes.Clone(), Weights = weights });
            }
            return pose;
        }

        void ValidatePose(Pose pose)
        {
            if (pose == null || pose.Rows == null || pose.Rows.Count != Channels.Count)
                throw new InvalidOperationException(ExporterLocalization.T("記録設定のメッシュ数が一致しません。"));
            for (var i = 0; i < Channels.Count; i++)
            {
                var row = pose.Rows[i]; var channel = Channels[i];
                if (row == null || row.Path != channel.Path || row.Shapes == null || !row.Shapes.SequenceEqual(channel.Shapes) ||
                    row.Weights == null || row.Weights.Length != channel.Shapes.Length || row.Weights.Any(weight => !Finite(weight)))
                    throw new InvalidOperationException(ExporterLocalization.T("記録設定の参照・変形量が一致しません。"));
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
            if (disposed) throw new InvalidOperationException(ExporterLocalization.T("表情記録のコピーがありません。"));
            return JsonUtility.ToJson(new Settings { MeshHashes = Channels.Select(channel => channel.MeshHash).ToArray(),
                Baseline = ClonePose(Baseline), Expressions = Expressions.Select(ClonePose).ToList(),
                ManualPoses = PoseOptions.Manual.Select(SavePoseReference).ToList(), ExcludedPoses = PoseOptions.Excluded.ToArray(),
                PoseNames = PoseOptions.Names.Select(pair => new PoseNameReference { Id = pair.Key, Name = pair.Value }).ToList() }, true);
        }

        internal void LoadSettings(string json)
        {
            RequireValid();
            if (json == null || json.Length > 8 * 1024 * 1024) throw new InvalidOperationException(ExporterLocalization.T("記録設定のサイズが不正です。"));
            Settings settings;
            try { settings = JsonUtility.FromJson<Settings>(json); }
            catch (Exception error) { throw new InvalidOperationException(ExporterLocalization.T("記録設定を読み込めません。"), error); }
            if (settings == null || settings.Version != 1 || settings.MeshHashes == null ||
                !settings.MeshHashes.SequenceEqual(Channels.Select(channel => channel.MeshHash)) || settings.Expressions == null ||
                settings.Expressions.Count > MaximumExpressions)
                throw new InvalidOperationException(ExporterLocalization.T("記録設定の形式またはメッシュが一致しません。"));
            ValidatePose(settings.Baseline);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in settings.Expressions)
            {
                ValidatePose(pose);
                if (ValidateName(pose.Name) != pose.Name || !names.Add(pose.Name))
                    throw new InvalidOperationException(ExporterLocalization.T("記録設定の表情名が重複または不正です。"));
            }
            var manualPoses = (settings.ManualPoses ?? new List<PoseReference>()).Select(LoadPoseReference).ToList();
            if (manualPoses.Count > 128) throw new InvalidOperationException(ExporterLocalization.T("同梱ポーズは128件までです。"));
            var poseNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var reference in settings.PoseNames ?? new List<PoseNameReference>())
                if (reference == null || string.IsNullOrEmpty(reference.Id) || poseNames.ContainsKey(reference.Id))
                    throw new InvalidOperationException(ExporterLocalization.T("ポーズの表示名設定が不正です。"));
                else poseNames.Add(reference.Id, ValidateName(reference.Name));
            // Publish only after validating every row and expression.
            Baseline = ClonePose(settings.Baseline);
            Expressions.Clear(); Expressions.AddRange(settings.Expressions.Select(ClonePose));
            PoseOptions.Manual.Clear(); PoseOptions.Manual.AddRange(manualPoses);
            PoseOptions.Excluded.Clear(); PoseOptions.Names.Clear();
            PoseOptions.Excluded.UnionWith(settings.ExcludedPoses ?? Array.Empty<string>());
            foreach (var pair in poseNames) PoseOptions.Names.Add(pair.Key, pair.Value);
            ApplyUnchecked(Baseline);
        }

        internal byte[] Export(string avatarName, string author, ICollection<string> warnings = null, BlinkExportOptions blinkOptions = null,
            AvatarLicenseOptions licenseOptions = null)
        {
            RequireValid();
            ValidatePose(Baseline);
            foreach (var pose in Expressions)
            {
                ValidatePose(pose);
                if (!HasGeometryChange(pose) && string.IsNullOrEmpty(pose.InstalledSourceId))
                    throw new InvalidOperationException(pose.Name + ExporterLocalization.T(": 基準の顔と形状が同じです。"));
            }
            var exportCopy = Object.Instantiate(Copy);
            exportCopy.name = Copy.name;
            exportCopy.hideFlags = HideFlags.HideAndDontSave;
            var meshes = new List<Mesh>();
            var assets = new List<Object>();
            try
            {
                // Resolve configured eyelids while the disposable copy still has
                // its descriptor. The normal exporter remaps these renderer
                // references to its own copy before any preparation pass.
                var exportBlinkOptions = blinkOptions;
                if (blinkOptions == null || blinkOptions.Mode == BlinkExportMode.Auto)
                {
                    using var resolvedBlink = BlinkExportSession.CaptureForExport(exportCopy, blinkOptions);
                    if (resolvedBlink.ConfiguredByDescriptor)
                    {
                        exportBlinkOptions = new BlinkExportOptions();
                        exportBlinkOptions.SelectMode(BlinkExportMode.Manual, resolvedBlink);
                    }
                }
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
                // A model-only export retains existing authored VRM expressions.
                // Explicit faces replace the catalog with the selected records.
                if (Expressions.Count > 0 || settings.Expression == null)
                    settings.Expression = new VRM10ObjectExpression {
                        Blink = settings.Expression?.Blink, BlinkLeft = settings.Expression?.BlinkLeft,
                        BlinkRight = settings.Expression?.BlinkRight
                    };
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
                        if (addedBytes > 128L * 1024 * 1024) throw new InvalidOperationException(ExporterLocalization.T("追加表情の形状データが128 MiBを超えます。"));
                        AvatarBaseShape.AppendExpression(Channels[i].Mesh, renderer.sharedMesh, target,
                            Baseline.Rows[i].Weights, pose.Rows[i].Weights, PlayerSettings.legacyClampBlendShapeWeights);
                        renderer.SetBlendShapeWeight(renderer.sharedMesh.blendShapeCount - 1, 0);
                        bindings.Add(new MorphTargetBinding(Channels[i].Path, renderer.sharedMesh.blendShapeCount - 1, 1f));
                    }
                    if (bindings.Count == 0)
                    {
                        // Preserve a named installed neutral face. A zero-delta
                        // target also keeps existing VRM/app morph-based catalogs
                        // aware of the entry and its override/reset semantics.
                        var renderer = VrChatExpressionSampler.FindRenderer(exportCopy, Channels[0].Path);
                        var target = targetPrefix + serial++;
                        addedBytes += Channels[0].Mesh.vertexCount * 36L;
                        if (addedBytes > 128L * 1024 * 1024) throw new InvalidOperationException(ExporterLocalization.T("追加表情の形状データが128 MiBを超えます。"));
                        AvatarBaseShape.AppendExpression(Channels[0].Mesh, renderer.sharedMesh, target,
                            Baseline.Rows[0].Weights, Baseline.Rows[0].Weights, PlayerSettings.legacyClampBlendShapeWeights);
                        renderer.SetBlendShapeWeight(renderer.sharedMesh.blendShapeCount - 1, 0);
                        bindings.Add(new MorphTargetBinding(Channels[0].Path, renderer.sharedMesh.blendShapeCount - 1, 1f));
                    }
                    expression.MorphTargetBindings = bindings.ToArray();
                    settings.Expression.CustomClips.Add(expression);
                }
                var bytes = UniVrmOneClickExporter.Export(exportCopy, avatarName, author, warnings,
                    exporterVersion: UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(UniVrmOneClickExporter).Assembly)?.version ?? "0.11.14",
                    lilToonVersion: "2.3.4", gimmickOptions: new ExportGimmickOptions { AutoExclude = false }, blinkOptions: exportBlinkOptions,
                    licenseOptions: licenseOptions ?? new AvatarLicenseOptions(), disableAudioLink: true);
                return InjectManualPoses(bytes);
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
                throw new InvalidOperationException(ExporterLocalization.T("表情名は制御文字を含まない1〜128文字で入力してください。"));
            return name;
        }

        static Pose ClonePose(Pose pose) => new Pose { Name = pose.Name, InstalledSourceId = pose.InstalledSourceId, Rows = pose.Rows.Select(row => new Row {
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
            controllers?.Dispose(); controllers = null;
            preparation?.Dispose(); preparation = null;
            foreach (var mesh in ownedMeshes) if (mesh != null) Object.DestroyImmediate(mesh);
            ownedMeshes.Clear();
        }
    }
}
