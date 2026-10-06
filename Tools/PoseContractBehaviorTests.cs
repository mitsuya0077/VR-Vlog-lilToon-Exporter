using System;
using System.Collections.Generic;
using System.Linq;
using VRVlog.Poses;

public static class PoseContractBehaviorTests
{
    static int count;
    static void Check(bool value) { count++; if (!value) throw new Exception("Pose assertion " + count); }
    static void Reject(Action action)
    { bool rejected = false; try { action(); } catch (InvalidOperationException) { rejected = true; } Check(rejected); }
    static HumanoidPoseData Valid()
    {
        var p = new HumanoidPoseData { Id = "stable", Name = "同じ名前", Category = "座り", Source = "APL", SampleTime = 0 };
        p.Bones.Add(new HumanoidPoseData.Bone { Name = "hips", Node = 4, Rotation = new double[] { 0, 0, 0, 1 } });
        return p;
    }
    public static void Run()
    {
        var a = Valid(); var b = Valid(); b.Id = "other-condition";
        var roundtrip = HumanoidPoseData.Read(HumanoidPoseData.Write(new[] { a, b }));
        Check(roundtrip.Count == 2 && roundtrip[0].Name == roundtrip[1].Name);
        Check(roundtrip[0].Category == "座り" && roundtrip[0].Bones[0].Node == 4);
        foreach (var n in new[] { double.NaN, double.PositiveInfinity, -1, 601 })
        { a = Valid(); a.SampleTime = n; Reject(() => HumanoidPoseData.Write(new[] { a })); }
        a = Valid(); b = Valid(); Reject(() => HumanoidPoseData.Write(new[] { a, b }));
        a = Valid(); a.Bones.Add(a.Bones[0]); Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.Bones[0].Name = "head"; Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.Bones[0].Rotation = new double[4]; Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.Bones[0].Rotation[0] = double.NaN; Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.HipsOffset[1] = 10.1; Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.Name = new string('x', 257); Reject(() => HumanoidPoseData.Write(new[] { a }));
        a = Valid(); a.Name = "line\nbreak"; Reject(() => HumanoidPoseData.Write(new[] { a }));
        var data = HumanoidPoseData.Write(new[] { Valid() }); data["schemaVersion"] = 2L; Reject(() => HumanoidPoseData.Read(data));
        data["schemaVersion"] = 1L; data["space"] = "unityLocal"; Reject(() => HumanoidPoseData.Read(data));
        data["space"] = "vrm1AvatarRestDelta"; data["unknown"] = true; Reject(() => HumanoidPoseData.Read(data));
        data.Remove("unknown"); var pose = (Dictionary<string, object>)((List<object>)data["poses"])[0];
        pose["sampledMotion"] = 1L; Reject(() => HumanoidPoseData.Read(data)); pose["sampledMotion"] = false;
        var bone = (Dictionary<string, object>)((List<object>)pose["bones"])[0]; bone["node"] = 4.5; Reject(() => HumanoidPoseData.Read(data)); bone["node"] = 4L;
        var nodes = new List<object> { new object(), new object(), new object(), new object(), new object() };
        var refs = new Dictionary<string, object> { ["hips"] = new Dictionary<string, object> { ["node"] = 4L } };
        var json = new Dictionary<string, object> { ["nodes"] = nodes, ["extensions"] = new Dictionary<string, object> {
            ["VRMC_vrm"] = new Dictionary<string, object> { ["humanoid"] = new Dictionary<string, object> { ["humanBones"] = refs } } } };
        HumanoidPoseData.ValidateReferences(new[] { Valid() }, json); Check(true);
        ((Dictionary<string, object>)refs["hips"])["node"] = 3L; Reject(() => HumanoidPoseData.ValidateReferences(new[] { Valid() }, json));
        ((Dictionary<string, object>)refs["hips"])["node"] = 4L; nodes.RemoveAt(4); Reject(() => HumanoidPoseData.ValidateReferences(new[] { Valid() }, json));
        var tooMany = new List<HumanoidPoseData>(); for (var i = 0; i < 129; i++) { var p = Valid(); p.Id = i.ToString(); tooMany.Add(p); }
        Reject(() => HumanoidPoseData.Write(tooMany));
        AnimationContract();
        Console.WriteLine("Pose contract: " + count + " assertions passed.");
    }

    static HumanoidAnimationData.Frame Frame(double time, double y = 0)
    {
        return new HumanoidAnimationData.Frame { Time = time, HipsOffset = new[] { 0d, y, 0d },
            Rotations = new[] { new[] { 0d, 0d, 0d, 1d } } };
    }
    static HumanoidAnimationData Animation()
    {
        var animation = new HumanoidAnimationData { Id = "motion", Name = "Moving pose", Category = "", Source = "Manual",
            SourceStartTime = .25, Duration = 1, Loop = true };
        animation.Bones.Add(new HumanoidAnimationData.Bone { Name = "hips", Node = 4 });
        animation.Frames.Add(Frame(0)); animation.Frames.Add(Frame(1, .4)); return animation;
    }
    static Dictionary<string, object> MotionRow(Dictionary<string, object> data) => (Dictionary<string, object>)((List<object>)data["animations"])[0];
    static Dictionary<string, object> MotionFrame(Dictionary<string, object> data) => (Dictionary<string, object>)((List<object>)MotionRow(data)["frames"])[0];
    static void AnimationContract()
    {
        var a = Animation(); var b = Animation(); b.Id = "other"; b.Loop = false;
        var decoded = HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a, b }));
        Check(decoded.Count == 2 && decoded[0].SourceStartTime == .25 && decoded[0].Loop && !decoded[1].Loop);
        Check(decoded[0].Bones[0].Node == 4 && decoded[0].Frames[1].HipsOffset[1] == .4);
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1, 601 })
        { a = Animation(); a.SourceStartTime = value; Reject(() => HumanoidAnimationData.Write(new[] { a })); }
        foreach (var value in new[] { double.NaN, double.NegativeInfinity, 0, -1, 601 })
        { a = Animation(); a.Duration = value; Reject(() => HumanoidAnimationData.Write(new[] { a })); }
        a = Animation(); b = Animation(); Reject(() => HumanoidAnimationData.Write(new[] { a, b }));
        a = Animation(); a.Name = new string('x', 257); Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Source = "line\nbreak"; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Category = null; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Bones.Add(a.Bones[0]); Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Bones[0].Name = "leftEye"; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Bones[0].Node = 65536; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Bones[0].Node = -1; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Bones[0].Name = "leftUpperArm"; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[0].Time = .1; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Time = .9; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1, 2 })
        { a = Animation(); a.Frames.Insert(1, Frame(value)); Reject(() => HumanoidAnimationData.Write(new[] { a })); }
        a = Animation(); a.Frames.Insert(1, Frame(.7)); a.Frames.Insert(2, Frame(.6)); Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames.Insert(1, Frame(0)); Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames.InsertRange(1, new[] { Frame(.5), Frame(.5), Frame(.5) }); Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].HipsOffset[0] = 10.1; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].HipsOffset[0] = double.NaN; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].HipsOffset = new double[2]; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Rotations = new double[0][]; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Rotations[0] = new double[3]; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Rotations[0] = new double[4]; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Rotations[0][0] = double.PositiveInfinity; Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames[1].Rotations[0][3] = -1;
        Check(HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a }))[0].Frames[1].Rotations[0][3] == -1);

        a = Animation(); a.Frames.InsertRange(1, new[] { Frame(.5, .1), Frame(.5, .3) });
        a = HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a }))[0];
        a.Locate(.25, out var from, out var to, out var amount);
        Check(ReferenceEquals(from, a.Frames[0]) && ReferenceEquals(to, a.Frames[1]) && amount == .5);
        Check(from.HipsOffset[1] + (to.HipsOffset[1] - from.HipsOffset[1]) * amount == .05);
        a.Locate(.5, out from, out to, out amount);
        Check(ReferenceEquals(from, a.Frames[2]) && ReferenceEquals(to, from) && amount == 0);
        a.Locate(.75, out from, out to, out amount);
        Check(ReferenceEquals(from, a.Frames[2]) && ReferenceEquals(to, a.Frames[3]) && amount == .5);
        a.Locate(-1, out from, out to, out amount); Check(ReferenceEquals(from, a.Frames[0]) && ReferenceEquals(to, from) && amount == 0);
        a.Locate(2, out from, out to, out amount); Check(ReferenceEquals(from, a.Frames[3]) && ReferenceEquals(to, from) && amount == 0);
        Reject(() => a.Locate(double.NaN, out from, out to, out amount));
        Reject(() => a.Locate(double.PositiveInfinity, out from, out to, out amount));
        a = Animation(); a.Frames.Insert(1, Frame(1, .2));
        a = HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a }))[0];
        a.Locate(1, out from, out to, out amount); Check(ReferenceEquals(from, a.Frames[2]) && ReferenceEquals(to, from));

        var data = HumanoidAnimationData.Write(new[] { Animation() }); data["schemaVersion"] = 2L; Reject(() => HumanoidAnimationData.Read(data));
        data["schemaVersion"] = 1L; data["space"] = "unityLocal"; Reject(() => HumanoidAnimationData.Read(data));
        data["space"] = "vrm1AvatarRestDelta"; data["unknown"] = true; Reject(() => HumanoidAnimationData.Read(data)); data.Remove("unknown");
        MotionRow(data)["loop"] = 1L; Reject(() => HumanoidAnimationData.Read(data)); MotionRow(data)["loop"] = true;
        MotionRow(data)["unknown"] = true; Reject(() => HumanoidAnimationData.Read(data)); MotionRow(data).Remove("unknown");
        MotionFrame(data)["unknown"] = true; Reject(() => HumanoidAnimationData.Read(data)); MotionFrame(data).Remove("unknown");
        var bone = (Dictionary<string, object>)((List<object>)MotionRow(data)["bones"])[0];
        bone["node"] = 4.5; Reject(() => HumanoidAnimationData.Read(data)); bone["node"] = 4L;
        MotionFrame(data)["time"] = "0"; Reject(() => HumanoidAnimationData.Read(data)); MotionFrame(data)["time"] = 0d;
        MotionFrame(data)["rotations"] = new List<object>(); Reject(() => HumanoidAnimationData.Read(data));
        data = HumanoidAnimationData.Write(new[] { Animation() }); data["oversize"] = new string('x', HumanoidAnimationData.MaximumBytes / 6 + 1);
        Reject(() => HumanoidAnimationData.Read(data));

        a = Animation(); a.Bones.Add(new HumanoidAnimationData.Bone { Name = "neck", Node = 3 });
        a.Bones.Add(new HumanoidAnimationData.Bone { Name = "head", Node = 2 });
        foreach (var frame in a.Frames) frame.Rotations = new[] { new[] { 0d, 0d, 0d, 1d }, new[] { 0d, 0d, 0d, 1d }, new[] { 0d, 0d, 0d, 1d } };
        Check(HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a }))[0].Bones.Count == 3);
        var nodes = new List<object> { new object(), new object(), new object(), new object(), new object() };
        var refs = new Dictionary<string, object> { ["hips"] = new Dictionary<string, object> { ["node"] = 4L },
            ["neck"] = new Dictionary<string, object> { ["node"] = 3L }, ["head"] = new Dictionary<string, object> { ["node"] = 2L } };
        var json = new Dictionary<string, object> { ["nodes"] = nodes, ["extensions"] = new Dictionary<string, object> {
            ["VRMC_vrm"] = new Dictionary<string, object> { ["humanoid"] = new Dictionary<string, object> { ["humanBones"] = refs } } } };
        HumanoidAnimationData.ValidateReferences(new[] { a }, json); Check(true);
        ((Dictionary<string, object>)refs["head"])["node"] = 1L; Reject(() => HumanoidAnimationData.ValidateReferences(new[] { a }, json));
        ((Dictionary<string, object>)refs["head"])["node"] = 2L; nodes.RemoveAt(4); Reject(() => HumanoidAnimationData.ValidateReferences(new[] { a }, json));
        Reject(() => HumanoidAnimationData.ValidateReferences(new[] { a }, new Dictionary<string, object>()));

        var animations = new List<HumanoidAnimationData>();
        for (var index = 0; index < HumanoidAnimationData.MaximumAnimations + 1; index++) { var entry = Animation(); entry.Id = index.ToString(); animations.Add(entry); }
        Reject(() => HumanoidAnimationData.Write(animations));
        a = Animation(); a.Frames.Clear();
        for (var index = 0; index <= HumanoidAnimationData.MaximumFramesPerAnimation; index++) a.Frames.Add(Frame((double)index / HumanoidAnimationData.MaximumFramesPerAnimation));
        Reject(() => HumanoidAnimationData.Write(new[] { a }));
        a = Animation(); a.Frames.Clear();
        for (var index = 0; index < HumanoidAnimationData.MaximumFramesPerAnimation; index++) a.Frames.Add(Frame((double)index / (HumanoidAnimationData.MaximumFramesPerAnimation - 1)));
        Check(HumanoidAnimationData.Read(HumanoidAnimationData.Write(new[] { a }))[0].Frames.Count == HumanoidAnimationData.MaximumFramesPerAnimation);
        animations.Clear();
        for (var index = 0; index < 4; index++) { var entry = Animation(); entry.Id = index.ToString(); entry.Frames.Clear(); entry.Frames.AddRange(a.Frames); animations.Add(entry); }
        var last = Animation(); last.Id = "last"; animations.Add(last); Reject(() => HumanoidAnimationData.Write(animations));
        a.Bones.Clear(); foreach (var name in HumanoidAnimationData.BoneNames) a.Bones.Add(new HumanoidAnimationData.Bone { Name = name, Node = a.Bones.Count });
        var rotations = HumanoidAnimationData.BoneNames.Select(name => new[] { 0d, 0d, 0d, 1d }).ToArray();
        foreach (var frame in a.Frames) frame.Rotations = rotations;
        Reject(() => HumanoidAnimationData.Write(new[] { a }));
        // Motion-only metadata cannot change or enlarge the static v1 shape.
        var staticData = HumanoidPoseData.Write(new[] { Valid() }); staticData["animations"] = new List<object>();
        Reject(() => HumanoidPoseData.Read(staticData)); Check(HumanoidPoseData.BoneNames.Length == 50 && HumanoidAnimationData.BoneNames.Length == 52);
    }
}
