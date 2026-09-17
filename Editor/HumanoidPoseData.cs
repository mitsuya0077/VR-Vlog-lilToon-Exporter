using System;
using System.Collections.Generic;
using System.Linq;

// Identical source in the exporter and application; no Unity dependency.
namespace VRVlog.Poses
{
    internal sealed class HumanoidPoseData
    {
        internal const string Extension = "VRVLOG_humanoid_poses";
        internal const int MaximumPoses = 128, MaximumBytes = 2097152;
        internal static readonly string[] BoneNames = ("hips spine chest upperChest leftShoulder rightShoulder " +
            "leftUpperArm leftLowerArm leftHand rightUpperArm rightLowerArm rightHand " +
            "leftUpperLeg leftLowerLeg leftFoot leftToes rightUpperLeg rightLowerLeg rightFoot rightToes " +
            "leftThumbMetacarpal leftThumbProximal leftThumbDistal leftIndexProximal leftIndexIntermediate leftIndexDistal " +
            "leftMiddleProximal leftMiddleIntermediate leftMiddleDistal leftRingProximal leftRingIntermediate leftRingDistal " +
            "leftLittleProximal leftLittleIntermediate leftLittleDistal rightThumbMetacarpal rightThumbProximal rightThumbDistal " +
            "rightIndexProximal rightIndexIntermediate rightIndexDistal rightMiddleProximal rightMiddleIntermediate rightMiddleDistal " +
            "rightRingProximal rightRingIntermediate rightRingDistal rightLittleProximal rightLittleIntermediate rightLittleDistal").Split(' ');
        internal string Id, Name, Category, Source;
        internal double SampleTime;
        internal bool SampledMotion;
        internal double[] HipsOffset = new double[3];
        internal readonly List<Bone> Bones = new List<Bone>();
        internal sealed class Bone { internal string Name; internal int Node; internal double[] Rotation; }

        internal static Dictionary<string, object> Write(IList<HumanoidPoseData> poses)
        {
            var result = new Dictionary<string, object> {
                ["schemaVersion"] = 1L, ["space"] = "vrm1AvatarRestDelta",
                ["poses"] = poses.Select(p => (object)new Dictionary<string, object> {
                    ["id"] = p.Id, ["name"] = p.Name, ["category"] = p.Category, ["source"] = p.Source,
                    ["sampleTime"] = p.SampleTime, ["sampledMotion"] = p.SampledMotion,
                    ["hipsOffset"] = p.HipsOffset.Cast<object>().ToList(),
                    ["bones"] = p.Bones.Select(b => (object)new Dictionary<string, object> {
                        ["bone"] = b.Name, ["node"] = (long)b.Node, ["rotation"] = b.Rotation.Cast<object>().ToList()
                    }).ToList()
                }).ToList()
            };
            Read(result);
            return result;
        }

        internal static List<HumanoidPoseData> Read(object value)
        {
            var budget = MaximumBytes;
            Measure(value, ref budget, 0);
            var root = Obj(value, "schemaVersion", "space", "poses");
            if (Number(root["schemaVersion"]) != 1 || Text(root["space"]) != "vrm1AvatarRestDelta") Bad();
            var rows = Arr(root["poses"]);
            if (rows.Count == 0 || rows.Count > MaximumPoses) Bad();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<HumanoidPoseData>();
            foreach (var raw in rows)
            {
                var row = Obj(raw, "id", "name", "category", "source", "sampleTime", "sampledMotion", "hipsOffset", "bones");
                var pose = new HumanoidPoseData { Id = Text(row["id"]), Name = Text(row["name"]),
                    Category = Text(row["category"], true), Source = Text(row["source"]), SampleTime = Number(row["sampleTime"]),
                    HipsOffset = Vector(row["hipsOffset"], 3, 10) };
                if (!ids.Add(pose.Id) || pose.SampleTime < 0 || pose.SampleTime > 600 || !(row["sampledMotion"] is bool)) Bad();
                pose.SampledMotion = (bool)row["sampledMotion"];
                var bones = Arr(row["bones"]);
                if (bones.Count == 0 || bones.Count > BoneNames.Length) Bad();
                var names = new HashSet<string>(); var nodes = new HashSet<int>();
                foreach (var rawBone in bones)
                {
                    var b = Obj(rawBone, "bone", "node", "rotation");
                    var name = Text(b["bone"]); var number = Number(b["node"]);
                    var rotation = Vector(b["rotation"], 4, 1.001);
                    if (!BoneNames.Contains(name) || !names.Add(name) || number < 0 || number > 65535 || number != Math.Floor(number) ||
                        !nodes.Add((int)number) || Math.Abs(rotation.Sum(v => v * v) - 1) > .002) Bad();
                    pose.Bones.Add(new Bone { Name = name, Node = (int)number, Rotation = rotation });
                }
                if (!names.Contains("hips") && pose.HipsOffset.Any(v => v != 0)) Bad();
                result.Add(pose);
            }
            return result;
        }

        // Check exact exported node identities, never node names or hierarchy guesses.
        internal static void ValidateReferences(IList<HumanoidPoseData> poses, Dictionary<string, object> json)
        {
            var nodes = Arr(json["nodes"]);
            var extensions = (Dictionary<string, object>)json["extensions"];
            var vrm = (Dictionary<string, object>)extensions["VRMC_vrm"];
            var human = (Dictionary<string, object>)((Dictionary<string, object>)vrm["humanoid"])["humanBones"];
            foreach (var bone in poses.SelectMany(p => p.Bones))
                if (bone.Node >= nodes.Count || !human.TryGetValue(bone.Name, out var reference) ||
                    !(reference is Dictionary<string, object> entry) || !entry.TryGetValue("node", out var node) || Number(node) != bone.Node) Bad();
        }

        static Dictionary<string, object> Obj(object value, params string[] keys)
        {
            if (!(value is Dictionary<string, object> obj) || obj.Count != keys.Length || keys.Any(k => !obj.ContainsKey(k)))
                throw new InvalidOperationException("Invalid pose object.");
            return obj;
        }
        static List<object> Arr(object value) => value as List<object> ?? throw new InvalidOperationException("Invalid pose array.");
        static string Text(object value, bool empty = false) => value is string s && (empty || !string.IsNullOrWhiteSpace(s)) && s.Length <= 256 && !s.Any(char.IsControl)
            ? s : throw new InvalidOperationException("Invalid pose text.");
        static double[] Vector(object value, int length, double maximum)
        {
            var list = Arr(value); if (list.Count != length) Bad();
            var result = list.Select(Number).ToArray(); if (result.Any(v => Math.Abs(v) > maximum)) Bad(); return result;
        }
        static double Number(object value)
        {
            if (!(value is double) && !(value is float) && !(value is long) && !(value is int)) Bad();
            var n = Convert.ToDouble(value); if (double.IsNaN(n) || double.IsInfinity(n)) Bad(); return n;
        }
        static void Bad() => throw new InvalidOperationException("Pose extension is invalid, unsupported or exceeds its limits.");
        // Conservative JSON byte bound without serializing/allocating a second
        // hostile payload. String escapes cost at most six bytes per UTF-16 unit.
        static void Measure(object value, ref int budget, int depth)
        {
            if (depth > 8) Bad();
            budget -= 32;
            if (value is string s) budget -= checked(s.Length * 6);
            else if (value is Dictionary<string, object> obj)
                foreach (var pair in obj) { Measure(pair.Key, ref budget, depth + 1); Measure(pair.Value, ref budget, depth + 1); }
            else if (value is List<object> list)
                foreach (var item in list) Measure(item, ref budget, depth + 1);
            if (budget < 0) Bad();
        }
    }
}
