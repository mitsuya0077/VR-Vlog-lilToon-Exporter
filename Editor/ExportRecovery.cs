using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRVlog.LilToon;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    internal enum ExportRecoveryActionKind { DisableAudioLink, OmitSecondLayer, OmitThirdLayer, ExcludeHiddenRenderer, SkipVrChatMenus, ExcludeMenuBranch }

    internal sealed class ExportRecoveryAction
    {
        internal string Id;
        internal ExportRecoveryActionKind Kind;
        internal Material Material;
        internal Renderer Renderer;
        internal Object MenuRoot;
        internal GameObject MenuOwner;
        internal string MenuPath;
    }

    internal sealed class ExportRecoveryOptions
    {
        internal readonly List<ExportRecoveryAction> Actions = new List<ExportRecoveryAction>();
    }

    internal sealed class ExportRecoveryDiagnostic
    {
        internal string Id, Code, Stage, Target, Reason, Remedy, LostEffect;
        internal Object Source;
        internal ExportRecoveryAction Action;
    }

    // Adds context to the original exception rather than changing its type.
    internal static class ExportRecoveryFailure
    {
        internal const string CodeKey = "VRVlog.Recovery.Code", TargetKey = "VRVlog.Recovery.Target", LayerKey = "VRVlog.Recovery.Layer";
        internal static T At<T>(T error, string code, Object target) where T : Exception
        {
            error.Data[CodeKey] = code;
            error.Data[TargetKey] = target;
            return error;
        }
    }

    internal sealed class ExportRecoveryReport
    {
        internal GameObject DiagnosticSource;
        internal string Stage = "環境確認";
        internal bool Succeeded;
        internal readonly List<ExportRecoveryDiagnostic> Diagnostics = new List<ExportRecoveryDiagnostic>();
        readonly Dictionary<Material, Material> originals = new Dictionary<Material, Material>();
        string errorType;

        internal void Begin()
        {
            Stage = "環境確認";
            Succeeded = false;
            errorType = null;
            Diagnostics.Clear();
            originals.Clear();
        }

        internal void Track(Material copy, Material original)
        {
            originals[copy] = originals.TryGetValue(original, out var source) ? source : original;
        }

        internal Material SourceMaterial(Material material)
            => !ReferenceEquals(material, null) && originals.TryGetValue(material, out var original) ? original : material;

        internal static ExportRecoveryReport FromException(GameObject source, Exception error, string stage = null)
        {
            var report = new ExportRecoveryReport();
            if (stage != null) report.Stage = stage;
            try { report.Fail(source, error); }
            catch
            {
                // Unreadable plugin/component data must not replace the actual
                // export failure or invent an automatic remedy.
                if (report.Diagnostics.Count == 0)
                    report.Add("export-failure", source, source != null ? source.name : "アバター", error.Message,
                        "表示された設定とConsoleを確認してください。未知の設定を理由に自動で除外しません。", "対策は適用していません。", null);
            }
            return report;
        }

        internal void Fail(GameObject source, Exception error)
        {
            source = DiagnosticSource != null ? DiagnosticSource : source;
            Succeeded = false;
            Diagnostics.Clear();
            errorType = error is MaterialBakeException ? nameof(MaterialBakeException) :
                error.GetType().Namespace == "System" || error.GetType().Namespace == "System.IO" ? error.GetType().Name : "Exception";
            var rawCode = error.Data[ExportRecoveryFailure.CodeKey] as string;
            var code = rawCode == "audio-link" || rawCode == "material-layer" || rawCode == "outside-reference" || rawCode == "physbone" ? rawCode : null;
            var target = error.Data[ExportRecoveryFailure.TargetKey] as Object;
            if (target is Material material) target = SourceMaterial(material);
            if (error is VrChatMenuImportLimitException menuLimit)
            {
                Stage = "表情メニュー読込";
                AddMenuLimit(source, menuLimit.Summary);
            }
            else if (error is VrChatMenuImportPolicyException)
            {
                Stage = "表情メニュー読込";
                var root = source != null ? VrChatExpressionMenu.Root(source) as Object : null;
                Add("menu-import-changed", root != null ? root : source, ExporterLocalization.T("VRChatメニュー全体"), error.Message,
                    "メニュー由来の表情・ポーズの取り込みをすべて省略して試せます。ジェスチャー・FaceEmo・APL・手動追加・既存VRM表情は保持します。",
                    "VRChatメニュー由来の表情・ポーズが取り込まれなくなります。衣装・髪・骨格のオブジェクト自体は削除しません。",
                    source == null ? null : new ExportRecoveryAction { Kind = ExportRecoveryActionKind.SkipVrChatMenus,
                        Id = "menu:" + source.GetInstanceID() + ":skip", MenuOwner = source, MenuRoot = root });
            }
            else if (error is MaterialBakeException bake)
            {
                foreach (var issue in bake.Issues)
                {
                    var original = SourceMaterial(issue.Material);
                    var layer = issue.Layer;
                    var action = ContainsMaterial(source, original) && (layer == "2nd" || layer == "3rd")
                        ? MaterialAction(original, layer == "2nd" ? ExportRecoveryActionKind.OmitSecondLayer : ExportRecoveryActionKind.OmitThirdLayer) : null;
                    Add("material-layer", original, issue.RendererPath + " / " + issue.MaterialName + " / " + layer,
                        issue.Reason, action != null ? "変換用コピーで、このレイヤーを省略して試せます。" : issue.NextStep,
                        "このレイヤーの模様・文字・透過効果が失われます。", action);
                }
            }
            else if (code == "audio-link" && target is Material audio && ContainsMaterial(source, audio) && Official(audio) && Enabled(audio, "_UseAudioLink"))
                Add(code, audio, audio.name, "AudioLinkは外部連携のため今回の再現対象外です。", "変換用コピーで、この材質のAudioLinkをOFFにして試せます。",
                    "音に連動した発光・色・動きが失われます。", MaterialAction(audio, ExportRecoveryActionKind.DisableAudioLink));
            else if (code == "material-layer" && target is Material layerMaterial && ContainsMaterial(source, layerMaterial) && Official(layerMaterial) &&
                (error.Data[ExportRecoveryFailure.LayerKey] as string == "2nd" || error.Data[ExportRecoveryFailure.LayerKey] as string == "3rd"))
            {
                var layer = (string)error.Data[ExportRecoveryFailure.LayerKey];
                Add(code, layerMaterial, layerMaterial.name + " / " + layer,
                    "このレイヤーの動的テクスチャは保存できません。", "変換用コピーで、このレイヤーを省略して試せます。",
                    "このレイヤーの模様・文字・透過効果が失われます。", MaterialAction(layerMaterial,
                        layer == "2nd" ? ExportRecoveryActionKind.OmitSecondLayer : ExportRecoveryActionKind.OmitThirdLayer));
            }
            else
                Add(code ?? (Stage == "揺れ物変換" ? "physbone" : "export-failure"), target != null ? target : source,
                    target != null ? target.name : source != null ? source.name : "アバター", error.Message,
                    Stage == "揺れ物変換" ? "表示されたPhysBoneの参照先・数値・対象の重複を確認してください。自動では除外しません。" :
                    "表示された設定とConsoleを確認してください。未知の設定を理由に自動で除外しません。",
                    "対策は適用していません。", null);

            if (source == null) return;
            foreach (var finding in ExportGimmickDetection.Analyze(source))
            {
                if (finding.Renderer == null) continue;
                var safe = finding.Unit == GimmickExclusionUnit.Renderer;
                var action = safe ? new ExportRecoveryAction { Kind = ExportRecoveryActionKind.ExcludeHiddenRenderer,
                    Renderer = finding.Renderer, Id = "renderer:" + finding.Renderer.GetInstanceID() } : null;
                Add(safe ? "hidden-renderer" : "unsupported-renderer", finding.Renderer,
                    Label(source, finding.Renderer.transform), finding.Reason,
                    safe ? "シェーダー作者のHidden指定に従い、この補助Rendererだけをコピーで除外して試せます。失敗の原因とは未確定です。" :
                        "シェーダーの対応状況を確認してください。この表示物の除外は推奨しません。",
                    safe ? "このRendererの補助表示が失われます。子オブジェクトと骨格は保持します。" : "自動対策はありません。", action);
            }
            AddOutsideReferences(source);
        }

        void AddMenuLimit(GameObject source, VrChatMenuImportSummary summary)
        {
            var root = source != null ? VrChatExpressionMenu.Root(source) as Object : null;
            var count = summary.IsComplete ? string.Format(ExporterLocalization.T("{0}候補"), summary.CandidateCount) :
                string.Format(ExporterLocalization.T("少なくとも{0}候補（省略された参照があり、件数は未確定）"), summary.CandidateCount);
            var reason = string.Format(ExporterLocalization.T("VRChatメニュー全体の取り込み対象は{0}です。上限は256候補です。顔表情以外の衣装Toggle・Puppet・無効な操作も数え、共有サブメニューは参照経路ごとに数えます。メニューを分割するだけでは合計は減りません。"), count);
            if (!summary.IsComplete)
                reason += " " + ExporterLocalization.T("メニューの参照数・操作数・深さの検査上限、または循環参照により、全体の件数を確定できませんでした。表示件数は下限で、省略後にも再検査します。");
            var skip = source != null ? new ExportRecoveryAction { Kind = ExportRecoveryActionKind.SkipVrChatMenus,
                Id = "menu:" + source.GetInstanceID() + ":skip", MenuOwner = source, MenuRoot = root } : null;
            Add("menu-import-limit", root != null ? root : source, ExporterLocalization.T("VRChatメニュー全体"), reason,
                "メニュー由来の表情・ポーズの取り込みをすべて省略して試せます。ジェスチャー・FaceEmo・APL・手動追加・既存VRM表情は保持します。",
                "VRChatメニュー由来の表情・ポーズが取り込まれなくなります。衣装・髪・骨格のオブジェクト自体は削除しません。", skip);
            // A preparation pass may generate a different menu. Index routes
            // on that generated tree must never select a branch in the source.
            if (source == null || !ReferenceEquals(summary.Root, root)) return;
            foreach (var branch in summary.Branches.Where(b => b.CandidateCount > 0 || !b.IsComplete))
            {
                var branchCount = branch.IsComplete ? string.Format(ExporterLocalization.T("{0}候補"), branch.CandidateCount) :
                    string.Format(ExporterLocalization.T("少なくとも{0}候補（省略された参照があり、件数は未確定）"), branch.CandidateCount);
                var action = new ExportRecoveryAction { Kind = ExportRecoveryActionKind.ExcludeMenuBranch, MenuOwner = source,
                    MenuRoot = root, MenuPath = branch.Path, Id = "menu:" + source.GetInstanceID() + ":" + branch.Path };
                Add("menu-import-limit", root, branch.Label + " [" + branch.Path + "]",
                    string.Format(ExporterLocalization.T("この枝には{0}あります。同じ名前や共有アセットでも、この参照経路だけを対象にします。"), branchCount),
                    "このサブメニューの枝だけを取り込み対象から省略して試せます。残した範囲が上限内になるまで再検査します。",
                    "この枝のVRChatメニュー由来の表情・ポーズが取り込まれなくなります。衣装・髪・骨格のオブジェクト自体は削除しません。", action);
            }
        }

        internal static VrChatMenuImportPolicy MenuImportPolicy(GameObject source, ExportRecoveryOptions options)
        {
            var actions = (options?.Actions ?? new List<ExportRecoveryAction>()).Where(a => a != null &&
                (a.Kind == ExportRecoveryActionKind.SkipVrChatMenus || a.Kind == ExportRecoveryActionKind.ExcludeMenuBranch)).ToArray();
            if (actions.Length == 0) return null;
            if (source == null) throw new InvalidOperationException("対策のVRChatメニューが選択アバターの原本と一致しません。再検査してください。");
            var root = VrChatExpressionMenu.Root(source);
            var policy = new VrChatMenuImportPolicy { ExpectedRoot = root };
            foreach (var action in actions)
            {
                if (action.MenuOwner != source || !ReferenceEquals(action.MenuRoot, root))
                    throw new InvalidOperationException("対策のVRChatメニューが選択アバターの原本と一致しません。再検査してください。");
                if (action.Kind == ExportRecoveryActionKind.SkipVrChatMenus) policy.SkipAll = true;
            }
            // Full omission supersedes branch scope, while every action above
            // must still belong to this source/menu. Retain the user's branch
            // choices in the UI recipe so deselecting full omission restores them.
            if (!policy.SkipAll)
                foreach (var action in actions.Where(a => a.Kind == ExportRecoveryActionKind.ExcludeMenuBranch))
                {
                    if (!VrChatExpressionMenu.IsSubMenuPath(root, action.MenuPath))
                        throw new InvalidOperationException("選んだVRChatメニューの枝を確認できません。再検査してください。");
                    policy.ExcludedBranches.Add(action.MenuPath);
                }
            policy.CaptureBranchReferences();
            return policy;
        }

        void AddOutsideReferences(GameObject source)
        {
            foreach (var component in source.GetComponentsInChildren<Component>(true))
            {
                if (component == null || component is Transform) continue;
                using var serialized = new SerializedObject(component);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var value = property.objectReferenceValue;
                    var transform = value is GameObject go ? go.transform : (value as Component)?.transform;
                    if (transform == null || EditorUtility.IsPersistent(value) || transform == source.transform || transform.IsChildOf(source.transform)) continue;
                    Add("outside-reference", component, Label(source, component.transform) + " / " + property.propertyPath,
                        "アバター外のシーンオブジェクトを参照しています。変換用コピー内で完結しない可能性があります。",
                        "アバター全体を選ぶか、該当設定の参照先を同じアバター内へ設定してください。自動では除外しません。",
                        "自動対策はありません。", null);
                }
            }
        }

        void Add(string code, Object source, string target, string reason, string remedy, string effect, ExportRecoveryAction action)
        {
            Diagnostics.Add(new ExportRecoveryDiagnostic { Id = code + ":" + Diagnostics.Count, Code = code,
                Stage = Stage, Source = source, Target = target, Reason = reason, Remedy = remedy, LostEffect = effect, Action = action });
        }

        // Deliberately omit local labels, free-form exception text, paths, names,
        // instance IDs and screenshots from the shareable report.
        internal string BuildSupportText()
        {
            var text = new StringBuilder("VR Vlog exporter recovery report v1\n");
            text.AppendLine("Result: " + (Succeeded ? "success" : "failed"));
            text.AppendLine("Stage: " + SupportStage(Stage));
            text.AppendLine("Error: " + (errorType ?? "none"));
            foreach (var issue in Diagnostics)
                text.AppendLine("Issue: " + issue.Code + "; action: " + (issue.Action?.Kind.ToString() ?? "manual-review"));
            return text.ToString();
        }

        static string SupportStage(string stage)
        {
            switch (stage)
            {
                case "環境確認": return "environment";
                case "原本検査": return "source-validation";
                case "表情メニュー読込": return "expression-menu";
                case "コピー作成": return "copy";
                case "ビルド処理": return "preparation";
                case "状態確定": return "appearance";
                case "材質保存": return "material-snapshot";
                case "揺れ物変換": return "physbone";
                case "VRM変換": return "vrm";
                case "出力検査": return "output-validation";
                case "完了": return "complete";
                default: return "other";
            }
        }

        internal static bool Official(Material material) => material != null && material.shader != null && LilToon234Catalogue.Shaders.ContainsKey(material.shader.name);
        internal static bool Enabled(Material material, string property) => material != null && material.HasProperty(property) && material.GetFloat(property) != 0;
        internal static bool ContainsMaterial(GameObject source, Material material)
        {
            if (source == null || material == null) return false;
            // Materials in controllers and authoring setters are part of the
            // source too, even before a build pass assigns them to a renderer.
            var queue = new Queue<Object>(source.GetComponentsInChildren<Component>(true).Cast<Object>());
            var visited = new HashSet<Object>();
            while (queue.Count != 0)
            {
                var value = queue.Dequeue();
                if (value == null || !visited.Add(value)) continue;
                if (value == material) return true;
                if (value is Material || value is Texture || value is Mesh || value is Shader || value is MonoScript || value is Transform) continue;
                using var serialized = new SerializedObject(value);
                var property = serialized.GetIterator();
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    var reference = property.objectReferenceValue;
                    if (reference == null || reference is Component || reference is GameObject) continue;
                    queue.Enqueue(reference);
                }
            }
            return false;
        }
        internal static ExportRecoveryAction MaterialAction(Material material, ExportRecoveryActionKind kind)
            => new ExportRecoveryAction { Id = "material:" + material.GetInstanceID() + ":" + kind, Kind = kind, Material = material };
        static string Label(GameObject source, Transform transform) => AnimationUtility.CalculateTransformPath(transform, source.transform);
    }

    // Every attempt owns a fresh copy. Material equality/name never selects a
    // different source, and duplicate hierarchy names use sibling-index routes.
    internal sealed class ExportRecoveryCopySession
    {
        readonly GameObject source, copy;
        readonly List<Material> owned;
        readonly ExportRecoveryOptions options;
        readonly ExportRecoveryReport report;
        readonly MaterialBakeOptions bakeOptions;
        readonly Dictionary<Material, Material> originals = new Dictionary<Material, Material>();
        readonly Dictionary<Renderer, Renderer> renderers = new Dictionary<Renderer, Renderer>();

        internal ExportRecoveryCopySession(GameObject source, GameObject copy, List<Material> owned,
            ExportRecoveryOptions options, ExportRecoveryReport report = null, MaterialBakeOptions bakeOptions = null)
        {
            if (source == null || copy == null || source == copy || EditorUtility.IsPersistent(copy) ||
                copy.transform.IsChildOf(source.transform) || source.transform.IsChildOf(copy.transform))
                throw new ArgumentException("An independent export copy is required.");
            this.source = source; this.copy = copy; this.owned = owned; this.options = options; this.report = report; this.bakeOptions = bakeOptions;
            foreach (var renderer in source.GetComponentsInChildren<Renderer>(true))
            {
                var mapped = Resolve(renderer.transform, source.transform, copy.transform);
                var sameType = renderer.GetComponents<Renderer>().Where(r => r.GetType() == renderer.GetType()).ToArray();
                var index = Array.IndexOf(sameType, renderer);
                renderers.Add(renderer, mapped.GetComponents<Renderer>().Where(r => r.GetType() == renderer.GetType()).ElementAt(index));
            }
        }

        internal void Track(Material material, Material original)
        {
            originals[material] = originals.TryGetValue(original, out var sourceMaterial) ? sourceMaterial : original;
        }

        internal void Apply(ICollection<string> warnings = null)
        {
            foreach (var action in options?.Actions ?? new List<ExportRecoveryAction>()) Validate(action);
            var skipAllMenus = options?.Actions.Any(action => action.Kind == ExportRecoveryActionKind.SkipVrChatMenus) == true;
            // Copy every material in the copy, including inactive wardrobes.
            // Never change a persistent material or any shared source material.
            var replacements = new Dictionary<Material, Material>();
            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                for (var i = 0; i < slots.Length; i++)
                {
                    var current = slots[i];
                    if (current == null) continue;
                    if (!originals.ContainsKey(current))
                    {
                        if (!replacements.TryGetValue(current, out var material))
                        {
                            material = new Material(current) { name = current.name };
                            owned.Add(material); replacements.Add(current, material);
                            originals.Add(material, current); report?.Track(material, current);
                        }
                        slots[i] = material;
                    }
                    var original = originals[slots[i]];
                    foreach (var layer in new[] { "2nd", "3rd" })
                        if (bakeOptions?.Omits(original, layer) == true && slots[i].HasProperty("_UseMain" + layer + "Tex"))
                            slots[i].SetFloat("_UseMain" + layer + "Tex", 0);
                    foreach (var action in options?.Actions ?? new List<ExportRecoveryAction>())
                    {
                        if (action.Material != original && action.Material != current) continue;
                        var property = action.Kind == ExportRecoveryActionKind.DisableAudioLink ? "_UseAudioLink" :
                            action.Kind == ExportRecoveryActionKind.OmitSecondLayer ? "_UseMain2ndTex" :
                            action.Kind == ExportRecoveryActionKind.OmitThirdLayer ? "_UseMain3rdTex" : null;
                        if (property != null && slots[i].HasProperty(property)) slots[i].SetFloat(property, 0);
                        if (action.Kind == ExportRecoveryActionKind.OmitSecondLayer || action.Kind == ExportRecoveryActionKind.OmitThirdLayer)
                        {
                            // Full snapshots retain disabled texture slots too.
                            // An explicitly omitted layer must not keep a dynamic
                            // image alive and fail its serialization afterward.
                            var prefix = action.Kind == ExportRecoveryActionKind.OmitSecondLayer ? "_Main2nd" : "_Main3rd";
                            var shader = slots[i].shader;
                            for (var p = 0; p < shader.GetPropertyCount(); p++)
                                if (shader.GetPropertyType(p) == UnityEngine.Rendering.ShaderPropertyType.Texture && shader.GetPropertyName(p).StartsWith(prefix, StringComparison.Ordinal))
                                    slots[i].SetTexture(shader.GetPropertyName(p), null);
                        }
                    }
                }
                renderer.sharedMaterials = slots;
            }
            // Redirect authoring component material references on the copy too;
            // this prevents a material setter from restoring the source asset.
            if (replacements.Count != 0)
                foreach (var component in copy.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    using var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator(); var changed = false;
                    while (property.Next(true))
                        if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue is Material material && replacements.TryGetValue(material, out var replacement))
                        { property.objectReferenceValue = replacement; changed = true; }
                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            foreach (var action in options?.Actions ?? new List<ExportRecoveryAction>())
            {
                // Full omission supersedes branch scope. Keep branch choices
                // in the recipe, but never report them as additional changes.
                if (skipAllMenus && action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch) continue;
                if (action.Kind == ExportRecoveryActionKind.ExcludeHiddenRenderer && renderers.TryGetValue(action.Renderer, out var renderer) && renderer != null)
                    renderer.enabled = false;
                warnings?.Add(ExporterLocalization.T("変換用コピーに対策を適用: ") + action.Kind);
            }
        }

        void Validate(ExportRecoveryAction action)
        {
            if (action == null) throw new InvalidOperationException("対策の対象を確認できません。再検査してください。");
            if (action.Kind == ExportRecoveryActionKind.SkipVrChatMenus || action.Kind == ExportRecoveryActionKind.ExcludeMenuBranch)
            {
                ExportRecoveryReport.MenuImportPolicy(source, options);
                return;
            }
            if (action.Kind == ExportRecoveryActionKind.ExcludeHiddenRenderer)
            {
                if (action.Renderer == null || !renderers.ContainsKey(action.Renderer) ||
                    ExportGimmickDetection.Inspect(action.Renderer)?.Unit != GimmickExclusionUnit.Renderer)
                    throw new InvalidOperationException("このRendererを安全な補助表示として確認できません。再検査してください。");
                return;
            }
            if (!ExportRecoveryReport.ContainsMaterial(source, action.Material))
                throw new InvalidOperationException("対策の材質が選択アバターにありません。再検査してください。");
            if (action.Kind == ExportRecoveryActionKind.DisableAudioLink && !ExportRecoveryReport.Official(action.Material))
                throw new InvalidOperationException("公式lilToonと確認できない材質のAudioLinkは自動変更できません。");
            if ((action.Kind == ExportRecoveryActionKind.OmitSecondLayer || action.Kind == ExportRecoveryActionKind.OmitThirdLayer) && !LilToonMaterialReader.IsLilToon(action.Material))
                throw new InvalidOperationException("この材質のレイヤー省略は適用できません。");
            if (!Enum.IsDefined(typeof(ExportRecoveryActionKind), action.Kind)) throw new InvalidOperationException("未対応の対策です。");
        }

        internal static Transform Resolve(Transform target, Transform root, Transform copy)
        {
            var route = new Stack<int>();
            for (var current = target; current != root; current = current.parent)
            {
                if (current == null) throw new ArgumentException("The target is outside the avatar.");
                route.Push(current.GetSiblingIndex());
            }
            while (route.Count != 0) copy = copy.GetChild(route.Pop());
            return copy;
        }
    }

    internal sealed class ExportRecoveryPreview : IDisposable
    {
        internal GameObject Copy { get; private set; }
        GameObject inactiveHost;
        readonly List<Material> materials = new List<Material>();
        readonly List<Mesh> meshes = new List<Mesh>();

        internal static ExportRecoveryPreview Create(GameObject source, ExportRecoveryOptions options = null,
            IEnumerable<GameObject> excludedObjects = null, ExportGimmickOptions gimmickOptions = null, MaterialBakeOptions bakeOptions = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            ExportRendererSelection.RequireActiveRoot(source);
            var preview = new ExportRecoveryPreview();
            try
            {
                // Instantiate under an inactive parent so ExecuteAlways
                // Awake/OnEnable cannot run before the shared assets are split.
                preview.inactiveHost = new GameObject("VRVlog display-copy staging") { hideFlags = HideFlags.HideAndDontSave };
                preview.inactiveHost.SetActive(false);
                preview.Copy = Object.Instantiate(source, preview.inactiveHost.transform, false);
                preview.Copy.name = source.name;
                var session = new ExportRecoveryCopySession(source, preview.Copy, preview.materials, options, bakeOptions: bakeOptions);
                using var gimmicks = new ExportGimmickSession(source, preview.Copy, gimmickOptions ?? new ExportGimmickOptions { AutoExclude = false });
                using var manual = new ExportObjectExclusions(source, excludedObjects);
                var findings = gimmickOptions?.AutoExclude == true ? ExportGimmickDetection.Analyze(source, manual.Contains) : new List<ExportGimmickFinding>();
                using var exclusions = new ExportObjectExclusions(source, (excludedObjects ?? Array.Empty<GameObject>()).Concat(ExportGimmickDetection.AutomaticRoots(findings, gimmickOptions)));
                MaAppearanceSnapshot.Apply(source, preview.Copy, preview.meshes, exclusions);
                PoseExportSession.RemoveAplFromCopy(source, preview.Copy);
                // Disabling a MonoBehaviour alone does not suppress its Awake
                // when the GameObject is activated. Display-only copies have no
                // runtime/authoring scripts, while preserving their baked shape.
                foreach (var script in preview.Copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (script != null) Object.DestroyImmediate(script);
                // Split/redirect material references after script removal too,
                // so these Editor writes cannot invoke a copied OnValidate.
                session.Apply();
                foreach (var behaviour in preview.Copy.GetComponentsInChildren<Behaviour>(true)) behaviour.enabled = false;
                foreach (var transform in preview.Copy.GetComponentsInChildren<Transform>(true)) transform.gameObject.hideFlags = HideFlags.HideAndDontSave;
                preview.Copy.transform.SetParent(null, false);
                Object.DestroyImmediate(preview.inactiveHost);
                preview.inactiveHost = null;
                gimmicks.Apply(null, null, null);
                return preview;
            }
            catch { preview.Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Copy != null) Object.DestroyImmediate(Copy);
            Copy = null;
            if (inactiveHost != null) Object.DestroyImmediate(inactiveHost);
            inactiveHost = null;
            foreach (var material in materials) if (material != null) Object.DestroyImmediate(material);
            foreach (var mesh in meshes) if (mesh != null) Object.DestroyImmediate(mesh);
            materials.Clear(); meshes.Clear();
        }
    }

    internal sealed class ExportRecoverySourceStamp
    {
        readonly int sourceId;
        readonly Hash128 hash;
        readonly ExportSourceFingerprint.PartialRotation[] partialRotations;
        ExportRecoverySourceStamp(int sourceId, Hash128 hash, ExportSourceFingerprint.PartialRotation[] partialRotations)
        { this.sourceId = sourceId; this.hash = hash; this.partialRotations = partialRotations; }
        internal static ExportRecoverySourceStamp Capture(GameObject source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var hash = ExportSourceFingerprint.Capture(source, out var partialRotations);
            return new ExportRecoverySourceStamp(source.GetInstanceID(), hash, partialRotations);
        }
        internal bool Matches(GameObject source)
        {
            if (source == null || source.GetInstanceID() != sourceId || hash != ExportSourceFingerprint.Capture(source, out var current) || current.Length != partialRotations.Length) return false;
            for (var index = 0; index < current.Length; index++) if (!partialRotations[index].Matches(current[index])) return false;
            return true;
        }

    }
}
