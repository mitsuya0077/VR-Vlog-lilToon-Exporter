# VR Vlog expression animation extension, version 1

`VRVLOG_expression_animations` is an optional GLB root extension. It is listed
in `extensionsUsed`, never `extensionsRequired`. Each animation refers to the
exact custom expression key in `VRMC_vrm.expressions.custom`, after duplicate
name suffixing. That standard expression stores the first composed pose and
blocks automatic blink, mouth and gaze. No animation helper is a custom
expression, so ordinary viewers keep a usable list of named first poses.

An animation stores `expression`, `duration` (seconds), `loop`, and `channels`.
Each channel contains `preWrap` / `postWrap` (`clamp`, `loop`, `pingPong`), `keys`,
and `points`. A key is `[time, value, inTangent, outTangent, inWeight, outWeight]`.
Null tangents represent a step; all numbers are finite. Unweighted Unity handles
use weight 1/3. Weighted handles are cubic Bezier control points in normalized
segment time. The player solves the time coordinate before evaluating value.
It does not resample the motion into a fixed frame rate.

Points contain an increasing source BlendShape `value` and a `target` name. Each
target is a unique generated mesh morph beginning with `__VRVlog_Anim_`. Its
geometry is the source shape evaluated at that value minus the shape at clip
time zero. `target: null` is allowed only at the time-zero value, where the
residual is zero. Point values cover the entire Bezier control-point range and
include every source morph frame knot in that range, including implicit zero.
Interpolating two adjacent points therefore retains multi-frame and negative
weight geometry. Identical basis targets may be reused by mutually exclusive
animations; channels within one animation cannot alias a target.

The player evaluates all channels at the same clip time and writes only these
dedicated morphs. The ordinary selected expression supplies the first pose.
Returning to default, changing expressions or disabling the driver clears the
animation writes. A non-looping clip holds its end pose. The component pauses
with its avatar and resumes its retained time when re-enabled. The app validates
the complete optional data and resolves every target before attaching a player;
rejected data retains the ordinary VRM faces. JSON is read without another copy
of the GLB's large binary chunk.

Bounds: at most 512 animations, 1,024 channels, 16,384 total curve keys, 4,096
point references, 600 seconds per clip, absolute key time at most 3,600 seconds,
and source weights/control-point range within -10,000 to 10,000. Added expanded
mesh deltas share the exporter's 128 MiB cap. Oversized output fails before the
destination file is replaced; it is not silently truncated.

The source is an authored facial AnimationClip reached from a registered FX
gesture condition. Its BlendShape curves are composed together over the
customized base face. This is not a VRChat runtime: controller behaviours,
retained layer state, gestures implemented only in generated controllers,
material/object changes and bone animations are not simulated. The existing
menu evaluator keeps its documented restrictions. Runtime tests cover data,
curve mathematics, GLB binding and playback state with API doubles; the Unity
Editor tests compare portable curves to AnimationCurve and exercise source
Animator/baking. Real avatar re-export and iPhone recording require device
acceptance testing.
