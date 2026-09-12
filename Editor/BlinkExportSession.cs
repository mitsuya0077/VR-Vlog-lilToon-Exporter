using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;
using UniVRM10;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // Capture renderer identity before authoring passes; resolve shape names again
    // afterwards. Only the disposable export copy receives generated geometry.
    internal sealed class BlinkExportSession : IDisposable
    {
        internal readonly List<BlinkShapeBinding>[] Slots = {
            new List<BlinkShapeBinding>(), new List<BlinkShapeBinding>(), new List<BlinkShapeBinding>()
        };
        internal string Description;
        internal bool PreserveAuthored;
        internal bool Disabled;
        readonly Dictionary<SkinnedMeshRenderer, int> nodes = new Dictionary<SkinnedMeshRenderer, int>();
        readonly bool[] authored = new bool[3];
        readonly List<Object> expressionCopies = new List<Object>();

        internal static BlinkExportSession Resolve(GameObject source, BlinkExportOptions options = null,
            Func<Transform, bool> excluded = null)
        {
            if (source == null) throw new InvalidOperationException("アバターを指定してください。");
            options = options ?? new BlinkExportOptions();
            var result = new BlinkExportSession();
            if (options.Mode == BlinkExportMode.None)
            {
                result.Disabled = true;
                result.Description = "瞬きなし";
                return result;
            }
            if (options.Mode == BlinkExportMode.Manual)
            {
                var selected = new[] { options.Both, options.Left, options.Right };
                for (var slot = 0; slot < selected.Length; slot++)
                    result.Slots[slot].AddRange(selected[slot].Select(b => b.Copy()));
                result.Description = "手動設定";
            }
            else
            {
                var expressions = source.GetComponent<Vrm10Instance>()?.Vrm?.Expression;
                var clips = new[] { expressions?.Blink, expressions?.BlinkLeft, expressions?.BlinkRight };
                if (clips.Any(c => c != null))
                {
                    result.PreserveAuthored = true;
                    result.Description = "元のVRM設定";
                    for (var slot = 0; slot < clips.Length; slot++)
                    {
                        var clip = clips[slot];
                        result.authored[slot] = clip != null;
                        if (clip == null) continue;
                        foreach (var binding in clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>())
                        {
                            var renderer = VrChatExpressionSampler.FindRenderer(source, binding.RelativePath);
                            if (renderer.sharedMesh == null || binding.Index < 0 || binding.Index >= renderer.sharedMesh.blendShapeCount)
                                throw new InvalidOperationException("元のVRMの瞬き設定が存在しない変形を参照しています。");
                            result.Slots[slot].Add(new BlinkShapeBinding { Renderer = renderer,
                                Shape = renderer.sharedMesh.GetBlendShapeName(binding.Index), Weight = binding.Weight * 100f });
                        }
                    }
                }
                else if (TryDescriptor(source, excluded, out var descriptorBinding))
                {
                    result.Slots[0].Add(descriptorBinding);
                    result.Description = "VRChatの閉眼設定";
                }
                else
                {
                    var completePairs = true;
                    foreach (var renderer in ExportRendererSelection.Enumerate(source).OfType<SkinnedMeshRenderer>())
                    {
                        if (excluded?.Invoke(renderer.transform) == true || renderer.sharedMesh == null) continue;
                        var mesh = renderer.sharedMesh;
                        var names = Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
                        var resolved = BlinkShapeNames.Resolve(names);
                        if (resolved[0] == -2)
                            throw new InvalidOperationException("閉眼用の名前が重複しています。「確認・調整」で設定してください。");
                        if (resolved[0] >= 0 && resolved[1] < 0) completePairs = false;
                        for (var slot = 0; slot < resolved.Length; slot++)
                            if (resolved[slot] >= 0)
                                result.Slots[slot].Add(new BlinkShapeBinding { Renderer = renderer, Shape = names[resolved[slot]] });
                        // A renderer with only a complete left/right pair still
                        // contributes to the bilateral fallback used by viewers.
                        if (resolved[0] < 0 && resolved[1] >= 0 && resolved[2] >= 0)
                            for (var slot = 1; slot < 3; slot++)
                                result.Slots[0].Add(new BlinkShapeBinding { Renderer = renderer, Shape = names[resolved[slot]] });
                    }
                    if (!completePairs) { result.Slots[1].Clear(); result.Slots[2].Clear(); }
                    result.Description = "自動設定";
                }
            }
            result.Validate(source, excluded);
            return result;
        }

        internal static bool TryDescriptor(GameObject source, Func<Transform, bool> excluded, out BlinkShapeBinding binding)
        {
            binding = null;
            var descriptor = source.GetComponents<Component>().FirstOrDefault(c => c != null &&
                c.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (!(VrChatExpressionMenu.Member(descriptor, "enableEyeLook") is bool enabled) || !enabled) return false;
            var settings = VrChatExpressionMenu.Member(descriptor, "customEyeLookSettings");
            // The public SDK enum is Blendshapes. Never use LookingUp/Down slots.
            if (VrChatExpressionMenu.Member(settings, "eyelidType")?.ToString() != "Blendshapes") return false;
            var renderer = VrChatExpressionMenu.Member(settings, "eyelidsSkinnedMesh") as SkinnedMeshRenderer;
            var indices = VrChatExpressionMenu.Member(settings, "eyelidsBlendshapes") as int[];
            if (renderer == null || renderer.sharedMesh == null || indices == null || indices.Length == 0 ||
                !renderer.enabled || !renderer.gameObject.activeInHierarchy ||
                !renderer.transform.IsChildOf(source.transform) || excluded?.Invoke(renderer.transform) == true) return false;
            var index = indices[0];
            if (index < 0 || index >= renderer.sharedMesh.blendShapeCount) return false;
            binding = new BlinkShapeBinding { Renderer = renderer, Shape = renderer.sharedMesh.GetBlendShapeName(index) };
            return true;
        }

        internal void Validate(GameObject root, Func<Transform, bool> excluded = null)
        {
            if (Disabled) return;
            foreach (var slot in Slots)
            {
                var seen = new HashSet<(SkinnedMeshRenderer, string)>();
                foreach (var binding in slot)
                {
                    var renderer = binding.Renderer;
                    if (renderer == null || !renderer.transform.IsChildOf(root.transform) || !renderer.enabled ||
                        !renderer.gameObject.activeInHierarchy || renderer.sharedMesh == null || excluded?.Invoke(renderer.transform) == true)
                        throw new InvalidOperationException("瞬きの対象が書き出しに含まれていません。「確認・調整」で指定し直してください。");
                    var names = Enumerable.Range(0, renderer.sharedMesh.blendShapeCount).Select(renderer.sharedMesh.GetBlendShapeName).ToArray();
                    if (string.IsNullOrEmpty(binding.Shape) || BlinkShapeNames.Unique(names, binding.Shape) < 0)
                        throw new InvalidOperationException("瞬きの変形が見つからないか重複しています: " + binding.Shape);
                    if (float.IsNaN(binding.Weight) || float.IsInfinity(binding.Weight) || binding.Weight < 0 || binding.Weight > 100)
                        throw new InvalidOperationException("瞬きの適用量は0〜100%で指定してください。");
                    if (!seen.Add((renderer, binding.Shape.ToLowerInvariant())))
                        throw new InvalidOperationException("同じ瞬きの変形を重複して指定できません。");
                }
            }
            if (PreserveAuthored) return; // An intentionally empty clip is authoritative.
            if (Slots[1].Count == 0 != (Slots[2].Count == 0))
                throw new InvalidOperationException("左右別の瞬きは左目・右目を両方指定してください。");
            if (Slots[0].Count == 0 && Slots[1].Count == 0)
                throw new InvalidOperationException("閉眼用の変形を特定できません。「確認・調整」で選択するか「瞬きなし」を指定してください。");
            if (Slots[1].Any(left => Slots[2].Any(right => left.Renderer == right.Renderer &&
                string.Equals(left.Shape, right.Shape, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("左右別には異なる閉眼用の変形を指定してください。");
        }

        internal BlinkExportSession ForClone(GameObject source, GameObject clone)
        {
            if (source == clone) throw new ArgumentException("An independent export copy is required.");
            var copy = new BlinkExportSession { Description = Description, PreserveAuthored = PreserveAuthored, Disabled = Disabled };
            Array.Copy(authored, copy.authored, authored.Length);
            for (var slot = 0; slot < Slots.Length; slot++)
                foreach (var binding in Slots[slot])
                {
                    var route = new Stack<int>();
                    for (var t = binding.Renderer.transform; t != source.transform; t = t.parent) route.Push(t.GetSiblingIndex());
                    var target = clone.transform;
                    while (route.Count != 0) target = target.GetChild(route.Pop());
                    var index = Array.IndexOf(binding.Renderer.GetComponents<SkinnedMeshRenderer>(), binding.Renderer);
                    var row = binding.Copy(); row.Renderer = target.GetComponents<SkinnedMeshRenderer>()[index];
                    copy.Slots[slot].Add(row);
                }
            return copy;
        }

        internal void Bake(GameObject clone, ICollection<Mesh> owned)
        {
            Validate(clone);
            if (Disabled)
            {
                // A no-op standard binding prevents a viewer's raw-name blink
                // fallback from driving the original morphs when blinking is off.
                var renderer = ExportRendererSelection.Enumerate(clone).OfType<SkinnedMeshRenderer>()
                    .FirstOrDefault(r => r.sharedMesh != null && r.sharedMesh.vertexCount > 0);
                if (renderer == null) return;
                var mesh = Object.Instantiate(renderer.sharedMesh); owned.Add(mesh); renderer.sharedMesh = mesh;
                var name = "__VRVlog_BlinkNone_" + Guid.NewGuid().ToString("N");
                var zeros = new Vector3[mesh.vertexCount]; mesh.AddBlendShapeFrame(name, 100f, zeros, zeros, zeros);
                foreach (var slot in Slots) slot.Add(new BlinkShapeBinding { Renderer = renderer, Shape = name });
                return;
            }
            foreach (var group in Slots.SelectMany(s => s).GroupBy(b => b.Renderer))
            {
                var renderer = group.Key;
                var original = renderer.sharedMesh;
                var generated = Object.Instantiate(original); owned.Add(generated);
                foreach (var binding in group)
                {
                    var index = BlinkShapeNames.Unique(Enumerable.Range(0, original.blendShapeCount)
                        .Select(original.GetBlendShapeName).ToArray(), binding.Shape);
                    var name = "__VRVlog_Blink_" + Guid.NewGuid().ToString("N");
                    AvatarBaseShape.AppendAnimatedShape(original, generated, name, index,
                        renderer.GetBlendShapeWeight(index), binding.Weight);
                    binding.Shape = name;
                    binding.Weight = 100f;
                }
                renderer.sharedMesh = generated;
            }
            if (PreserveAuthored)
            {
                // UniVRM reads the prepared hierarchy too. Give it private clips
                // with current paths/indices; never mutate the source VRM assets.
                var instance = clone.GetComponent<Vrm10Instance>();
                instance.Vrm = Object.Instantiate(instance.Vrm); expressionCopies.Add(instance.Vrm);
                var expression = instance.Vrm.Expression;
                var clips = new[] { expression.Blink, expression.BlinkLeft, expression.BlinkRight };
                for (var slot = 0; slot < clips.Length; slot++)
                {
                    if (clips[slot] == null) continue;
                    clips[slot] = Object.Instantiate(clips[slot]); expressionCopies.Add(clips[slot]);
                    clips[slot].MorphTargetBindings = Slots[slot].Select(b => new MorphTargetBinding(
                        UnityEditor.AnimationUtility.CalculateTransformPath(b.Renderer.transform, clone.transform),
                        b.Renderer.sharedMesh.GetBlendShapeIndex(b.Shape), b.Weight / 100f)).ToArray();
                }
                expression.Blink = clips[0]; expression.BlinkLeft = clips[1]; expression.BlinkRight = clips[2];
            }
        }

        public void Dispose()
        {
            foreach (var asset in expressionCopies) if (asset != null) Object.DestroyImmediate(asset);
            expressionCopies.Clear();
        }

        internal void Bind(ModelExporter converter, VrmLib.Model model, ExportingGltfData storage)
        {
            foreach (var renderer in Slots.SelectMany(s => s).Select(b => b.Renderer).Distinct())
            {
                if (!converter.Nodes.TryGetValue(renderer.gameObject, out var node))
                    throw new InvalidOperationException("瞬きの対象が最終VRMにありません。");
                var index = model.Nodes.IndexOf(node);
                if (index < 0) throw new InvalidOperationException("瞬きの出力nodeを解決できません。");
                nodes.Add(renderer, index);
            }
        }

        internal byte[] Apply(byte[] bytes)
        {
            var glb = GlbDocument.Read(bytes);
            var vrm = (Dictionary<string, object>)((Dictionary<string, object>)glb.Json["extensions"])["VRMC_vrm"];
            var expressions = ObjectMap(vrm, "expressions");
            var presets = ObjectMap(expressions, "preset");
            if (PreserveAuthored)
            {
                for (var slot = 0; slot < authored.Length; slot++)
                    if (authored[slot] && !presets.ContainsKey(BlinkShapeNames.Presets[slot]))
                        throw new InvalidOperationException("元のVRMの瞬き設定が出力時に失われました。");
            }
            else foreach (var preset in BlinkShapeNames.Presets) presets.Remove(preset);
            var outputNodes = (List<object>)glb.Json["nodes"];
            var meshes = (List<object>)glb.Json["meshes"];
            for (var slot = 0; slot < Slots.Length; slot++)
            {
                if (PreserveAuthored && (!authored[slot] || Slots[slot].Count == 0)) continue;
                if (Slots[slot].Count == 0 && !Disabled) continue;
                var binds = new List<object>();
                foreach (var binding in Slots[slot])
                {
                    var node = nodes[binding.Renderer];
                    var meshIndex = Convert.ToInt32(((Dictionary<string, object>)outputNodes[node])["mesh"]);
                    var mesh = (Dictionary<string, object>)meshes[meshIndex];
                    var names = ((List<object>)((Dictionary<string, object>)mesh["extras"])["targetNames"]).Cast<string>().ToArray();
                    var target = BlinkShapeNames.Unique(names, binding.Shape);
                    if (target < 0) throw new InvalidOperationException("最終VRMで瞬きの変形を特定できません。");
                    foreach (Dictionary<string, object> primitive in (List<object>)mesh["primitives"])
                        if (!(primitive.TryGetValue("targets", out var rawTargets) && rawTargets is List<object> targets) || target >= targets.Count)
                            throw new InvalidOperationException("瞬きのmorph target番号が不正です。");
                    binds.Add(new Dictionary<string, object> { { "node", (long)node }, { "index", (long)target },
                        { "weight", PreserveAuthored ? (double)binding.Weight / 100.0 : 1.0 } });
                }
                if (PreserveAuthored)
                    ((Dictionary<string, object>)presets[BlinkShapeNames.Presets[slot]])["morphTargetBinds"] = binds;
                else presets[BlinkShapeNames.Presets[slot]] = new Dictionary<string, object> { { "morphTargetBinds", binds }, { "isBinary", false } };
            }
            // Perfect Sync custom keys must agree with an explicit override too.
            if (!PreserveAuthored && expressions.TryGetValue("custom", out var rawCustom) && rawCustom is Dictionary<string, object> custom)
                foreach (var key in custom.Keys.ToArray())
                    if (string.Equals(key, "EyeBlinkLeft", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(key, "EyeBlinkRight", StringComparison.OrdinalIgnoreCase))
                    {
                        var slot = key.EndsWith("Left", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
                        custom[key] = presets.TryGetValue(BlinkShapeNames.Presets[slot], out var value) ? value : presets["blink"];
                    }
            return glb.Write();
        }

        static Dictionary<string, object> ObjectMap(Dictionary<string, object> parent, string key)
        {
            if (!parent.TryGetValue(key, out var value)) parent[key] = value = new Dictionary<string, object>();
            return (Dictionary<string, object>)value;
        }
    }
}
