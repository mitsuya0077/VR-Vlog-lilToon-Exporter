using System;
using System.Collections.Generic;
using System.Linq;

// Identical source in the exporter and application; no Unity dependency.
namespace VRVlog.Poses
{
    internal sealed class HumanoidAnimationData
    {
        internal const string Extension = "VRVLOG_humanoid_animations";
        internal const int MaximumAnimations = 128, MaximumFramesPerAnimation = 8192,
            MaximumTotalFrames = 32768, MaximumBytes = 16777216;
        internal static readonly string[] BoneNames = HumanoidPoseData.BoneNames.Concat(new[] { "neck", "head" }).ToArray();
        internal string Id, Name, Category, Source;
        internal double SourceStartTime, Duration;
        internal bool Loop;
        internal readonly List<Bone> Bones = new List<Bone>();
        internal readonly List<Frame> Frames = new List<Frame>();
        internal sealed class Bone { internal string Name; internal int Node; }
        internal sealed class Frame
        {
            internal double Time;
            internal double[] HipsOffset = new double[3];
            internal double[][] Rotations;
        }

        internal static Dictionary<string, object> Write(IList<HumanoidAnimationData> animations)
        {
            Validate(animations);
            var rows = new List<object>();
            var result = new Dictionary<string, object> { ["schemaVersion"] = 1L,
                ["space"] = "vrm1AvatarRestDelta", ["animations"] = rows };
            var budget = MaximumBytes; Measure(result, ref budget, 0);
            foreach (var animation in animations)
            {
                var frames = new List<object>();
                var row = new Dictionary<string, object> {
                    ["id"] = animation.Id, ["name"] = animation.Name, ["category"] = animation.Category, ["source"] = animation.Source,
                    ["sourceStartTime"] = animation.SourceStartTime, ["duration"] = animation.Duration, ["loop"] = animation.Loop,
                    ["bones"] = animation.Bones.Select(bone => (object)new Dictionary<string, object> { ["bone"] = bone.Name, ["node"] = (long)bone.Node }).ToList(),
                    ["frames"] = frames
                };
                Measure(row, ref budget, 2); rows.Add(row);
                foreach (var frame in animation.Frames)
                {
                    var entry = new Dictionary<string, object> { ["time"] = frame.Time,
                        ["hipsOffset"] = frame.HipsOffset.Cast<object>().ToList(),
                        ["rotations"] = frame.Rotations.Select(rotation => (object)rotation.Cast<object>().ToList()).ToList() };
                    // Bound allocation as entries are built, before producing
                    // an oversized DOM or a second serialized payload.
                    Measure(entry, ref budget, 4); frames.Add(entry);
                }
            }
            return result;
        }

        internal static List<HumanoidAnimationData> Read(object value)
        {
            var budget = MaximumBytes; Measure(value, ref budget, 0);
            var root = Obj(value, "schemaVersion", "space", "animations");
            if (Number(root["schemaVersion"]) != 1 || Text(root["space"]) != "vrm1AvatarRestDelta") Bad();
            var rows = Arr(root["animations"]);
            if (rows.Count == 0 || rows.Count > MaximumAnimations) Bad();
            var result = new List<HumanoidAnimationData>(); var totalFrames = 0;
            foreach (var raw in rows)
            {
                var row = Obj(raw, "id", "name", "category", "source", "sourceStartTime", "duration", "loop", "bones", "frames");
                if (!(row["loop"] is bool)) Bad();
                var animation = new HumanoidAnimationData { Id = Text(row["id"]), Name = Text(row["name"]), Category = Text(row["category"], true),
                    Source = Text(row["source"]), SourceStartTime = Number(row["sourceStartTime"]), Duration = Number(row["duration"]), Loop = (bool)row["loop"] };
                var bones = Arr(row["bones"]);
                if (bones.Count == 0 || bones.Count > BoneNames.Length) Bad();
                foreach (var rawBone in bones)
                {
                    var bone = Obj(rawBone, "bone", "node"); var node = Number(bone["node"]);
                    if (node < 0 || node > 65535 || node != Math.Floor(node)) Bad();
                    animation.Bones.Add(new Bone { Name = Text(bone["bone"]), Node = (int)node });
                }
                var frames = Arr(row["frames"]); totalFrames += frames.Count;
                if (frames.Count < 2 || frames.Count > MaximumFramesPerAnimation || totalFrames > MaximumTotalFrames) Bad();
                foreach (var rawFrame in frames)
                {
                    var frame = Obj(rawFrame, "time", "hipsOffset", "rotations"); var rotations = Arr(frame["rotations"]);
                    if (rotations.Count != bones.Count) Bad();
                    animation.Frames.Add(new Frame { Time = Number(frame["time"]), HipsOffset = Vector(frame["hipsOffset"], 3, 10),
                        Rotations = rotations.Select(rotation => Vector(rotation, 4, 1.001)).ToArray() });
                }
                result.Add(animation);
            }
            Validate(result); return result;
        }

        internal static void ValidateReferences(IList<HumanoidAnimationData> animations, Dictionary<string, object> json)
        {
            Validate(animations);
            var nodes = Arr(Get(json, "nodes"));
            var extensions = Object(Get(json, "extensions"));
            var vrm = Object(Get(extensions, "VRMC_vrm"));
            var human = Object(Get(Object(Get(vrm, "humanoid")), "humanBones"));
            foreach (var bone in animations.SelectMany(animation => animation.Bones))
                if (bone.Node >= nodes.Count || !human.TryGetValue(bone.Name, out var reference) ||
                    !(reference is Dictionary<string, object> entry) || !entry.TryGetValue("node", out var node) || Number(node) != bone.Node) Bad();
        }

        // The caller owns elapsed time and once/loop policy. Clamp its local
        // timeline time; no arrays, lists or iterators are allocated here.
        internal void Locate(double time, out Frame from, out Frame to, out double amount)
        {
            if (!Finite(time) || !Finite(Duration) || Duration <= 0 || Frames.Count < 2) Bad();
            amount = 0;
            if (time <= 0) { from = to = Frames[0]; return; }
            if (time >= Duration) { from = to = Frames[Frames.Count - 1]; return; }
            var low = 0; var high = Frames.Count;
            // Upper bound selects the right-hand frame of a STEP pair at an
            // exact timestamp, while the preceding interval ends on the left.
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (Frames[middle].Time <= time) low = middle + 1;
                else high = middle;
            }
            from = Frames[low - 1]; to = Frames[low];
            if (from.Time == time) { to = from; return; }
            amount = (time - from.Time) / (to.Time - from.Time);
        }

        static void Validate(IList<HumanoidAnimationData> animations)
        {
            if (animations == null || animations.Count == 0 || animations.Count > MaximumAnimations) Bad();
            var ids = new HashSet<string>(StringComparer.Ordinal); var totalFrames = 0;
            foreach (var animation in animations)
            {
                if (animation == null) Bad();
                Text(animation.Id); Text(animation.Name); Text(animation.Category, true); Text(animation.Source);
                if (!ids.Add(animation.Id) || !Finite(animation.SourceStartTime) || animation.SourceStartTime < 0 || animation.SourceStartTime > 600 ||
                    !Finite(animation.Duration) || animation.Duration <= 0 || animation.Duration > 600) Bad();
                if (animation.Bones.Count == 0 || animation.Bones.Count > BoneNames.Length) Bad();
                var names = new HashSet<string>(StringComparer.Ordinal); var nodes = new HashSet<int>();
                foreach (var bone in animation.Bones)
                    if (bone == null || !BoneNames.Contains(bone.Name) || !names.Add(bone.Name) || bone.Node < 0 || bone.Node > 65535 || !nodes.Add(bone.Node)) Bad();
                var frames = animation.Frames; totalFrames += frames.Count;
                if (frames.Count < 2 || frames.Count > MaximumFramesPerAnimation || totalFrames > MaximumTotalFrames) Bad();
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames[index];
                    if (frame == null || !Finite(frame.Time) || frame.Time < 0 || frame.Time > animation.Duration ||
                        index == 0 && frame.Time != 0 || index == frames.Count - 1 && frame.Time != animation.Duration) Bad();
                    if (index > 0 && (frame.Time < frames[index - 1].Time || frame.Time == frames[index - 1].Time &&
                        (frame.Time == 0 || index > 1 && frame.Time == frames[index - 2].Time))) Bad();
                    ValidateVector(frame.HipsOffset, 3, 10);
                    if (!names.Contains("hips") && frame.HipsOffset.Any(value => value != 0)) Bad();
                    if (frame.Rotations == null || frame.Rotations.Length != animation.Bones.Count) Bad();
                    foreach (var rotation in frame.Rotations)
                    {
                        ValidateVector(rotation, 4, 1.001);
                        if (Math.Abs(rotation.Sum(value => value * value) - 1) > .002) Bad();
                    }
                }
            }
        }

        static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ??
            throw new InvalidOperationException("Invalid animation object.");
        static object Get(Dictionary<string, object> value, string key)
        { if (value == null || !value.TryGetValue(key, out var result)) throw new InvalidOperationException("Invalid animation reference."); return result; }
        static Dictionary<string, object> Obj(object value, params string[] keys)
        {
            var obj = Object(value);
            if (obj.Count != keys.Length || keys.Any(key => !obj.ContainsKey(key))) Bad();
            return obj;
        }
        static List<object> Arr(object value) => value as List<object> ?? throw new InvalidOperationException("Invalid animation array.");
        static string Text(object value, bool empty = false) => value is string text && (empty || !string.IsNullOrWhiteSpace(text)) &&
            text.Length <= 256 && !text.Any(char.IsControl) ? text : throw new InvalidOperationException("Invalid animation text.");
        static double[] Vector(object value, int length, double maximum)
        {
            var list = Arr(value); if (list.Count != length) Bad();
            var result = list.Select(Number).ToArray(); ValidateVector(result, length, maximum); return result;
        }
        static void ValidateVector(double[] value, int length, double maximum)
        { if (value == null || value.Length != length || value.Any(number => !Finite(number) || Math.Abs(number) > maximum)) Bad(); }
        static double Number(object value)
        {
            if (!(value is double) && !(value is float) && !(value is long) && !(value is int)) Bad();
            var number = Convert.ToDouble(value); if (!Finite(number)) Bad(); return number;
        }
        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static void Bad() => throw new InvalidOperationException("Animation extension is invalid, unsupported or exceeds its limits.");
        // Same conservative JSON bound as the static contract: account for
        // scalar/container overhead and worst-case UTF-16 escaping, not only
        // the visible character count. Avoid a second hostile serialization.
        static void Measure(object value, ref int budget, int depth)
        {
            if (depth > 8) Bad();
            budget -= 32;
            if (value is string text)
            {
                if (text.Length > budget / 6) Bad();
                budget -= text.Length * 6;
            }
            else if (value is Dictionary<string, object> obj)
                foreach (var pair in obj) { Measure(pair.Key, ref budget, depth + 1); Measure(pair.Value, ref budget, depth + 1); }
            else if (value is List<object> list)
                foreach (var item in list) Measure(item, ref budget, depth + 1);
            if (budget < 0) Bad();
        }
    }
}
