using System;
using System.Collections.Generic;
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
        Console.WriteLine("Pose contract: " + count + " assertions passed.");
    }
}
