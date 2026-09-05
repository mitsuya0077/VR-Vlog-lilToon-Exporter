using System;
using System.Collections.Generic;
using System.Linq;

// Kept identical in the exporter and player. No Unity/Editor dependency.
namespace VRVlog.Expressions
{
    internal sealed class ExpressionAnimationData
    {
        internal const string Extension = "VRVLOG_expression_animations";
        internal const string TargetPrefix = "__VRVlog_Anim_";
        internal string Expression;
        internal double Duration;
        internal bool Loop;
        internal readonly List<Channel> Channels = new List<Channel>();

        internal sealed class Point { internal double Value; internal string Target; }
        internal sealed class Channel
        {
            internal Curve Curve;
            internal readonly List<Point> Points = new List<Point>();

            internal void Weights(double time, out int left, out int right, out float blend)
            {
                var value = Curve.Evaluate(time);
                right = 1;
                while (right < Points.Count - 1 && value > Points[right].Value) right++;
                left = right - 1;
                blend = (float)Math.Max(0, Math.Min(1,
                    (value - Points[left].Value) / (Points[right].Value - Points[left].Value)));
            }
        }

        internal double TimeAt(double elapsed)
        {
            elapsed = Math.Max(0, elapsed);
            return Loop ? elapsed % Duration : Math.Min(elapsed, Duration);
        }

        internal sealed class Key
        {
            internal double Time, Value, InWeight = 1.0 / 3, OutWeight = 1.0 / 3;
            // null denotes a stepped tangent; JSON never contains Infinity.
            internal double? InTangent, OutTangent;
        }

        internal sealed class Curve
        {
            internal string PreWrap = "clamp", PostWrap = "clamp";
            internal readonly List<Key> Keys = new List<Key>();

            internal double Evaluate(double time)
            {
                var first = Keys[0];
                var last = Keys[Keys.Count - 1];
                if (Keys.Count == 1) return first.Value;
                if (time < first.Time || time > last.Time)
                {
                    var wrap = time < first.Time ? PreWrap : PostWrap;
                    if (wrap == "clamp") return time < first.Time ? first.Value : last.Value;
                    var length = last.Time - first.Time;
                    var period = wrap == "pingPong" ? length * 2 : length;
                    var phase = (time - first.Time) % period;
                    if (phase < 0) phase += period;
                    if (phase > length) phase = period - phase;
                    time = first.Time + phase;
                }
                if (time <= first.Time) return first.Value;
                if (time >= last.Time) return last.Value;
                var lo = 0;
                var hi = Keys.Count - 1;
                while (hi - lo > 1)
                {
                    var mid = (lo + hi) / 2;
                    if (Keys[mid].Time <= time) lo = mid; else hi = mid;
                }
                var a = Keys[lo]; var b = Keys[hi];
                if (time == a.Time || !a.OutTangent.HasValue || !b.InTangent.HasValue) return a.Value;
                var duration = b.Time - a.Time;
                var x = (time - a.Time) / duration;
                // Weighted Unity tangents are cubic Bezier handles in normalized
                // time. Solve x first, then evaluate y with the same parameter.
                double lower = 0, upper = 1;
                for (var iteration = 0; iteration < 40; iteration++)
                {
                    var t = (lower + upper) * .5;
                    if (Bezier(0, a.OutWeight, 1 - b.InWeight, 1, t) < x) lower = t;
                    else upper = t;
                }
                return Bezier(a.Value, a.Value + duration * a.OutWeight * a.OutTangent.Value,
                    b.Value - duration * b.InWeight * b.InTangent.Value, b.Value, (lower + upper) * .5);
            }

            static double Bezier(double a, double b, double c, double d, double t)
            {
                var u = 1 - t;
                return u * u * u * a + 3 * u * u * t * b + 3 * u * t * t * c + t * t * t * d;
            }

            internal void Range(out double minimum, out double maximum)
            {
                minimum = Keys.Min(k => k.Value); maximum = Keys.Max(k => k.Value);
                for (var i = 1; i < Keys.Count; i++)
                {
                    var a = Keys[i - 1]; var b = Keys[i]; var dt = b.Time - a.Time;
                    if (!a.OutTangent.HasValue || !b.InTangent.HasValue) continue;
                    var y1 = a.Value + dt * a.OutWeight * a.OutTangent.Value;
                    var y2 = b.Value - dt * b.InWeight * b.InTangent.Value;
                    minimum = Math.Min(minimum, Math.Min(y1, y2));
                    maximum = Math.Max(maximum, Math.Max(y1, y2));
                }
            }

            internal void Validate()
            {
                if (!Wrap(PreWrap) || !Wrap(PostWrap) || Keys.Count == 0 || Keys.Count > 16384) Bad();
                var previous = double.NegativeInfinity;
                foreach (var k in Keys)
                {
                    if (!Finite(k.Time) || Math.Abs(k.Time) > 3600 || k.Time <= previous ||
                        !Finite(k.Value) || !Finite(k.InWeight) || !Finite(k.OutWeight) ||
                        k.InWeight < 0 || k.InWeight > 1 || k.OutWeight < 0 || k.OutWeight > 1 ||
                        k.InTangent.HasValue && !Finite(k.InTangent.Value) ||
                        k.OutTangent.HasValue && !Finite(k.OutTangent.Value)) Bad();
                    previous = k.Time;
                }
                Range(out var minimum, out var maximum);
                if (!Finite(minimum) || !Finite(maximum) || minimum < -10000 || maximum > 10000) Bad();
            }

            static bool Wrap(string value) => value == "clamp" || value == "loop" || value == "pingPong";
        }

        internal static Dictionary<string, object> Write(IList<ExpressionAnimationData> animations)
        {
            var result = new Dictionary<string, object>
            {
                ["schemaVersion"] = 1L,
                ["animations"] = animations.Select(a => (object)new Dictionary<string, object>
                {
                    ["expression"] = a.Expression, ["duration"] = a.Duration, ["loop"] = a.Loop,
                    ["channels"] = a.Channels.Select(c => (object)new Dictionary<string, object>
                    {
                        ["preWrap"] = c.Curve.PreWrap, ["postWrap"] = c.Curve.PostWrap,
                        ["keys"] = c.Curve.Keys.Select(k => (object)new List<object>
                            { k.Time, k.Value, k.InTangent.HasValue ? (object)k.InTangent.Value : null,
                                k.OutTangent.HasValue ? (object)k.OutTangent.Value : null, k.InWeight, k.OutWeight }).ToList(),
                        ["points"] = c.Points.Select(p => (object)new Dictionary<string, object>
                            { ["value"] = p.Value, ["target"] = p.Target }).ToList()
                    }).ToList()
                }).ToList()
            };
            Read(result); // The exporter and runtime enforce the same contract.
            return result;
        }

        internal static List<ExpressionAnimationData> Read(object value)
        {
            var root = Obj(value);
            if (Number(root["schemaVersion"]) != 1) Bad();
            var array = Arr(root["animations"]);
            if (array.Count == 0 || array.Count > 512) Bad();
            var result = new List<ExpressionAnimationData>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var channelCount = 0; var keyCount = 0; var pointCount = 0;
            foreach (var raw in array)
            {
                var obj = Obj(raw);
                var animation = new ExpressionAnimationData { Expression = Text(obj["expression"]), Duration = Number(obj["duration"]) };
                var targets = new HashSet<string>(StringComparer.Ordinal);
                if (!(obj["loop"] is bool)) Bad();
                animation.Loop = (bool)obj["loop"];
                if (!names.Add(animation.Expression) || animation.Duration <= 0 || animation.Duration > 600) Bad();
                foreach (var rawChannel in Arr(obj["channels"]))
                {
                    if (++channelCount > 1024) Bad();
                    var c = Obj(rawChannel);
                    var channel = new Channel { Curve = new Curve { PreWrap = Text(c["preWrap"]), PostWrap = Text(c["postWrap"]) } };
                    foreach (var rawKey in Arr(c["keys"]))
                    {
                        if (++keyCount > 16384) Bad();
                        var k = Arr(rawKey);
                        if (k.Count != 6) Bad();
                        channel.Curve.Keys.Add(new Key { Time = Number(k[0]), Value = Number(k[1]),
                            InTangent = k[2] == null ? (double?)null : Number(k[2]), OutTangent = k[3] == null ? (double?)null : Number(k[3]),
                            InWeight = Number(k[4]), OutWeight = Number(k[5]) });
                    }
                    channel.Curve.Validate();
                    foreach (var rawPoint in Arr(c["points"]))
                    {
                        if (++pointCount > 4096) Bad();
                        var p = Obj(rawPoint);
                        var point = new Point { Value = Number(p["value"]), Target = p["target"] == null ? null : Text(p["target"]) };
                        // Null is the zero residual at the animation's first
                        // pose. Identical basis morphs may be shared between
                        // mutually exclusive animations, never within one face.
                        if (point.Target == null ? point.Value != channel.Curve.Evaluate(0) :
                            !point.Target.StartsWith(TargetPrefix, StringComparison.Ordinal) || !targets.Add(point.Target)) Bad();
                        if (point.Value < -10000 || point.Value > 10000 ||
                            channel.Points.Count > 0 && point.Value <= channel.Points[channel.Points.Count - 1].Value) Bad();
                        channel.Points.Add(point);
                    }
                    channel.Curve.Range(out var minimum, out var maximum);
                    if (channel.Points.Count < 2 || channel.Points[0].Value > minimum ||
                        channel.Points[channel.Points.Count - 1].Value < maximum) Bad();
                    animation.Channels.Add(channel);
                }
                if (animation.Channels.Count == 0) Bad();
                result.Add(animation);
            }
            return result;
        }

        static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static Dictionary<string, object> Obj(object value) => value as Dictionary<string, object> ?? throw new InvalidOperationException("Invalid expression animation object.");
        static List<object> Arr(object value) => value as List<object> ?? throw new InvalidOperationException("Invalid expression animation array.");
        static string Text(object value) => value is string s && s.Length > 0 && s.Length <= 1024 ? s : throw new InvalidOperationException("Invalid expression animation name.");
        static double Number(object value)
        {
            if (!(value is double) && !(value is float) && !(value is long) && !(value is int)) Bad();
            var number = Convert.ToDouble(value);
            if (!Finite(number)) Bad();
            return number;
        }
        static void Bad() => throw new InvalidOperationException("Expression animation data is invalid or exceeds the supported limits.");
    }
}
