using System;
using System.Collections.Generic;
using System.Linq;
using UniVRM10;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace VRVlog.LilToonExporter
{
    // A VRM bind/profile weight declares an absolute source pose, not a fraction
    // of the range left after neutral has been stored in the base mesh. Bake
    // each declared endpoint against the same prepared neutral before rebinding.
    internal sealed class AuthoredExpressionEndpoints : IDisposable
    {
        sealed class Endpoint
        {
            internal NeutralShapeSnapshot.RendererState State;
            internal float[] Pose;
            internal string Name;
        }

        sealed class ClipEndpoint
        {
            internal VRM10Expression Clip;
            internal readonly List<Endpoint> Endpoints = new List<Endpoint>();
            internal readonly List<MorphTargetBinding> Retained = new List<MorphTargetBinding>();
        }

        readonly GameObject avatar;
        readonly NeutralShapeSnapshot neutral;
        readonly VrmTrackingProfile sourceProfile;
        readonly List<ClipEndpoint> clips = new List<ClipEndpoint>();
        readonly Dictionary<VRM10Expression, MorphTargetBinding[]> inertBindings = new Dictionary<VRM10Expression, MorphTargetBinding[]>();
        readonly List<List<Endpoint>> tracking = new List<List<Endpoint>>();
        readonly List<Object> owned = new List<Object>();
        readonly string prefix = "__VRVlog_Endpoint_" + Guid.NewGuid().ToString("N") + "_";
        int serial;
        bool baked;
        long generatedBytes;
        internal VrmTrackingProfile TrackingProfile { get; private set; }

        AuthoredExpressionEndpoints(GameObject avatar, NeutralShapeSnapshot neutral, VrmTrackingProfile profile)
        {
            this.avatar = avatar;
            this.neutral = neutral;
            sourceProfile = profile;
        }

        internal static AuthoredExpressionEndpoints Capture(GameObject prepared, NeutralShapeSnapshot neutral,
            VrmTrackingProfile profile = null)
        {
            if (neutral == null || neutral.Root != prepared)
                throw new ArgumentException("The neutral snapshot must belong to the prepared avatar.");
            var session = new AuthoredExpressionEndpoints(prepared, neutral, profile);
            var expression = prepared.GetComponent<Vrm10Instance>()?.Vrm?.Expression;
            if (expression != null)
            {
                // Preset slots are authoritative, even if one asset is also used
                // as a custom non-blink expression. Do not rewrite blink's copy.
                foreach (var clip in expression.Clips.Where(item => item.Preset != ExpressionPreset.blink &&
                    item.Preset != ExpressionPreset.blinkLeft && item.Preset != ExpressionPreset.blinkRight)
                    .Select(item => item.Clip).Distinct())
                    session.CaptureClip(clip);
            }
            if (profile != null)
            {
                VrmTrackingExpressions.Validate(profile);
                foreach (var entry in profile.expressions)
                {
                    var endpoints = new Dictionary<NeutralShapeSnapshot.RendererState, Endpoint>();
                    var declared = new Dictionary<(NeutralShapeSnapshot.RendererState, int), float>();
                    foreach (var morph in entry.morphs)
                    {
                        VrmTrackingExpressions.ValidateMorph(morph, entry.name);
                        var found = false;
                        foreach (var state in neutral.Renderers)
                        {
                            var index = state.OriginalMesh.GetBlendShapeIndex(morph.shape);
                            if (index < 0) continue;
                            found = true;
                            session.SetEndpoint(endpoints, declared, state, index, morph.weight * 100f);
                        }
                        if (!found) throw new InvalidOperationException("追跡表情の元BlendShapeがありません: " + morph.shape + " (" + entry.name + ")");
                    }
                    session.tracking.Add(endpoints.Values.ToList());
                }
            }
            return session;
        }

        void CaptureClip(VRM10Expression clip)
        {
            var result = new ClipEndpoint { Clip = clip };
            var endpoints = new Dictionary<NeutralShapeSnapshot.RendererState, Endpoint>();
            var declared = new Dictionary<(NeutralShapeSnapshot.RendererState, int), float>();
            var bindings = clip.MorphTargetBindings ?? Array.Empty<MorphTargetBinding>();
            var protectedBindings = bindings.Select(binding => {
                var state = neutral.Get(binding.RelativePath);
                return binding.Index >= 0 && binding.Index != int.MaxValue &&
                    (state == null || binding.Index >= state.OriginalMesh.blendShapeCount)
                    ? new MorphTargetBinding(binding.RelativePath, int.MaxValue, binding.Weight) : binding;
            }).ToArray();
            if (protectedBindings.Where((binding, index) => binding.Index != bindings[index].Index).Any())
                inertBindings.Add(clip, protectedBindings);
            // Malformed declarations deliberately reserve authored coverage in
            // the existing export contract. Rewriting their positive portion
            // could accidentally turn a disabled route into usable evidence.
            if (bindings.Any(binding => !NeutralShapeSnapshot.Finite(binding.Weight) || binding.Weight < 0 || binding.Weight > 1)) return;
            var first = new HashSet<(NeutralShapeSnapshot.RendererState, int)>();
            foreach (var binding in bindings)
            {
                var state = neutral.Get(binding.RelativePath);
                // Leave omitted/malformed routes to the existing exporter and
                // exclusion policy, preserving their scope and fallback rules.
                if (state == null || binding.Index < 0 || binding.Index >= state.OriginalMesh.blendShapeCount)
                {
                    return;
                }
                // UniVRM keeps the first bind for each renderer/index. A first
                // weight of zero intentionally disables that authored channel.
                if (!first.Add((state, binding.Index))) continue;
                if (binding.Weight == 0)
                {
                    result.Retained.Add(binding);
                    continue;
                }
                SetEndpoint(endpoints, declared, state, binding.Index, binding.Weight * 100f);
            }
            result.Endpoints.AddRange(endpoints.Values);
            if (result.Endpoints.Count > 0) clips.Add(result);
        }

        void SetEndpoint(Dictionary<NeutralShapeSnapshot.RendererState, Endpoint> endpoints,
            Dictionary<(NeutralShapeSnapshot.RendererState, int), float> declared,
            NeutralShapeSnapshot.RendererState state, int shape, float weight)
        {
            var key = (state, shape);
            // VRM1 keeps the first binding to a renderer/channel, including in
            // profiles whose declared shape name resolves on several meshes.
            if (declared.ContainsKey(key)) return;
            if (!endpoints.TryGetValue(state, out var endpoint))
            {
                endpoint = new Endpoint { State = state, Pose = (float[])state.Weights.Clone(), Name = prefix + serial++ };
                endpoints.Add(state, endpoint);
            }
            if (!NeutralShapeSnapshot.Finite(weight)) throw new InvalidOperationException("表情のBlendShape値が不正です。");
            declared[key] = weight;
            endpoint.Pose[shape] = weight;
        }

        internal void Bake(ICollection<Mesh> meshes)
        {
            if (baked) throw new InvalidOperationException("Expression endpoints have already been baked.");
            if (meshes == null) throw new ArgumentNullException(nameof(meshes));
            baked = true;
            foreach (var endpoint in clips.SelectMany(clip => clip.Endpoints).Concat(tracking.SelectMany(entry => entry)))
            {
                var state = endpoint.State;
                var renderer = state.Renderer;
                if (renderer == null || renderer.sharedMesh == null || renderer.sharedMesh.vertexCount != state.OriginalMesh.vertexCount)
                    throw new InvalidOperationException("表情の対象Meshがneutral確定後に変更されました: " + state.Path);
                generatedBytes += state.OriginalMesh.vertexCount * 36L;
                if (generatedBytes > 128L * 1024 * 1024)
                    throw new InvalidOperationException("表情の追加データが上限の128 MiBを超えます。");
                if (!meshes.Contains(renderer.sharedMesh) || ReferenceEquals(renderer.sharedMesh, state.OriginalMesh))
                {
                    renderer.sharedMesh = Object.Instantiate(renderer.sharedMesh);
                    meshes.Add(renderer.sharedMesh);
                }
                AvatarBaseShape.AppendExpression(state.OriginalMesh, renderer.sharedMesh, endpoint.Name, state.Weights, endpoint.Pose);
            }
            if (clips.Count > 0 || inertBindings.Count > 0)
            {
                var instance = avatar.GetComponent<Vrm10Instance>();
                if (instance == null || instance.Vrm == null) throw new InvalidOperationException("元のVRM表情設定がありません。");
                var privateVrm = Object.Instantiate(instance.Vrm); owned.Add(privateVrm);
                var replacements = new Dictionary<VRM10Expression, VRM10Expression>();
                foreach (var pair in inertBindings)
                {
                    var copy = Object.Instantiate(pair.Key); owned.Add(copy);
                    copy.MorphTargetBindings = pair.Value; replacements.Add(pair.Key, copy);
                }
                foreach (var endpoint in clips)
                {
                    var copy = Object.Instantiate(endpoint.Clip); owned.Add(copy);
                    copy.MorphTargetBindings = endpoint.Retained.Concat(endpoint.Endpoints.Select(value => new MorphTargetBinding(
                        AnimationUtility.CalculateTransformPath(value.State.Renderer.transform, avatar.transform),
                        value.State.Renderer.sharedMesh.GetBlendShapeIndex(value.Name), 1f))).ToArray();
                    replacements.Add(endpoint.Clip, copy);
                }
                ReplaceNonBlink(privateVrm.Expression, replacements);
                instance.Vrm = privateVrm;
            }
            if (sourceProfile != null)
            {
                TrackingProfile = Object.Instantiate(sourceProfile); owned.Add(TrackingProfile);
                TrackingProfile.expressions = sourceProfile.expressions.Select((entry, index) => new TrackingExpression {
                    name = entry.name,
                    morphs = tracking[index].Select(endpoint => new TrackingMorph { shape = endpoint.Name, weight = 1f }).ToArray()
                }).ToArray();
            }
        }

        static void ReplaceNonBlink(VRM10ObjectExpression expression, IDictionary<VRM10Expression, VRM10Expression> replacements)
        {
            VRM10Expression Replace(VRM10Expression clip) => clip != null && replacements.TryGetValue(clip, out var copy) ? copy : clip;
            expression.Happy = Replace(expression.Happy); expression.Angry = Replace(expression.Angry);
            expression.Sad = Replace(expression.Sad); expression.Relaxed = Replace(expression.Relaxed); expression.Surprised = Replace(expression.Surprised);
            expression.Aa = Replace(expression.Aa); expression.Ih = Replace(expression.Ih); expression.Ou = Replace(expression.Ou);
            expression.Ee = Replace(expression.Ee); expression.Oh = Replace(expression.Oh);
            expression.LookUp = Replace(expression.LookUp); expression.LookDown = Replace(expression.LookDown);
            expression.LookLeft = Replace(expression.LookLeft); expression.LookRight = Replace(expression.LookRight);
            expression.Neutral = Replace(expression.Neutral);
            expression.CustomClips = expression.CustomClips?.Select(Replace).ToList() ?? new List<VRM10Expression>();
        }

        public void Dispose()
        {
            foreach (var asset in owned) if (asset != null) Object.DestroyImmediate(asset);
            owned.Clear();
        }
    }
}
