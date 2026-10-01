using System;
using System.Collections.Generic;
using System.Text;

namespace VRVlog.FaceTracking
{
    /// <summary>
    /// The bounded phone-compatible Unified Expressions vocabulary. This pure
    /// registry is shared with the exporter; it does not interpret VRCFT Animator
    /// parameters, corrective shapes or expressions the input cannot measure.
    /// </summary>
    internal static class UnifiedExpressionRegistry
    {
        public const string Prefix = "UE/";
        public const string RestPrefix = "__VRVlog_UE_Rest";

        sealed class Definition
        {
            public string Name;
            public int Priority;
            public Func<Func<string, float>, float> Evaluate;
            public string[] Coverage;
        }

        static readonly Dictionary<string, Definition> Definitions = new Dictionary<string, Definition>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> NormalizedNames = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly HashSet<string> SharedNames = new HashSet<string>(StringComparer.Ordinal);
        static readonly IReadOnlyList<string> RegisteredNames;

        public static IReadOnlyList<string> AllNames { get { return RegisteredNames; } }

        static UnifiedExpressionRegistry()
        {
            foreach (var side in new[] { "Left", "Right" })
            {
                var s = side;
                foreach (var direction in new[] { "Up", "Down", "In", "Out" })
                {
                    Direct("EyeLook" + direction + s, "EyeLook" + direction + s);
                }
                Direct("EyeClosed" + s, "EyeBlink" + s);
                Direct("EyeSquint" + s, "EyeSquint" + s);
                Direct("EyeWide" + s, "EyeWide" + s);
                Direct("BrowDown" + s, "BrowDown" + s, 10, "BrowLowerer" + s, "BrowPinch" + s);
                Direct("BrowOuterUp" + s, "BrowOuterUp" + s);
                Direct("NoseSneer" + s, "NoseSneer" + s);
                Direct("CheekSquint" + s, "CheekSquint" + s);
                Direct("Jaw" + s, "Jaw" + s);
                Direct("Mouth" + s, "Mouth" + s, 10, "MouthUpper" + s, "MouthLower" + s);
                Direct("MouthSmile" + s, "MouthSmile" + s, 10, "MouthCornerPull" + s, "MouthCornerSlant" + s);
                foreach (var action in new[] { "UpperUp", "LowerDown", "Frown", "Stretch", "Dimple", "Press" })
                {
                    Direct("Mouth" + action + s, "Mouth" + action + s);
                }

                Direct("BrowLowerer" + s, "BrowDown" + s, 20);
                Direct("BrowPinch" + s, "BrowDown" + s, 20);
                Direct("BrowInnerUp" + s, "BrowInnerUp", 20);
                Direct("CheekPuff" + s, "CheekPuff", 20);
                Direct("MouthCornerPull" + s, "MouthSmile" + s, 20);
                Direct("MouthCornerSlant" + s, "MouthSmile" + s, 20);
                Direct("MouthUpper" + s, "Mouth" + s, 20);
                Direct("MouthLower" + s, "Mouth" + s, 20);
                Add("BrowUp" + s, 25, read => BrowUp(read, s), "BrowInnerUp" + s, "BrowOuterUp" + s);
                Add("MouthSad" + s, 25, read => Sad(read, s), "MouthFrown" + s, "MouthStretch" + s);
            }

            Direct("BrowInnerUp", "BrowInnerUp", 10, "BrowInnerUpLeft", "BrowInnerUpRight");
            Direct("CheekPuff", "CheekPuff", 10, "CheekPuffLeft", "CheekPuffRight");
            Direct("JawOpen", "JawOpen");
            Direct("JawForward", "JawForward");
            Direct("MouthClosed", "MouthClose");
            Direct("TongueOut", "TongueOut");

            foreach (var half in new[] { "Upper", "Lower" })
            {
                var h = half;
                Direct("MouthRaiser" + h, "MouthShrug" + h);
                Direct("LipSuck" + h, "MouthRoll" + h, 10, "LipSuck" + h + "Left", "LipSuck" + h + "Right");
                foreach (var side in new[] { "Left", "Right" })
                {
                    Direct("LipSuck" + h + side, "MouthRoll" + h, 20);
                }
            }

            foreach (var action in new[] { "Funnel", "Pucker" })
            {
                var a = action;
                Direct("Lip" + a, "Mouth" + a, 10, LipCoverage(a));
                foreach (var half in new[] { "Upper", "Lower" })
                {
                    Direct("Lip" + a + half, "Mouth" + a, 20, "Lip" + a + half + "Left", "Lip" + a + half + "Right");
                    foreach (var side in new[] { "Left", "Right" })
                    {
                        Direct("Lip" + a + half + side, "Mouth" + a, 21);
                    }
                }
            }

            Add("EyeClosed", 30, read => Mean(Value(read, "EyeBlinkLeft"), Value(read, "EyeBlinkRight")), "EyeClosedLeft", "EyeClosedRight");
            Add("EyeSquint", 30, read => Math.Max(Value(read, "EyeSquintLeft"), Value(read, "EyeSquintRight")), "EyeSquintLeft", "EyeSquintRight");
            Add("EyeWide", 30, read => Math.Max(Value(read, "EyeWideLeft"), Value(read, "EyeWideRight")), "EyeWideLeft", "EyeWideRight");
            Add("BrowDown", 30, read => Mean(Value(read, "BrowDownLeft"), Value(read, "BrowDownRight")), "BrowLowererLeft", "BrowPinchLeft", "BrowLowererRight", "BrowPinchRight");
            Add("BrowUp", 35, read => Mean(BrowUp(read, "Left"), BrowUp(read, "Right")), "BrowInnerUpLeft", "BrowOuterUpLeft", "BrowInnerUpRight", "BrowOuterUpRight");
            foreach (var action in new[] { "NoseSneer", "CheekSquint", "MouthUpperUp", "MouthLowerDown", "MouthStretch", "MouthDimple", "MouthPress" })
            {
                var a = action;
                Add(a, 30, read => Mean(Value(read, a + "Left"), Value(read, a + "Right")), a + "Left", a + "Right");
            }
            Add("LipSuck", 35, read => Mean(Value(read, "MouthRollUpper"), Value(read, "MouthRollLower")), LipCoverage("Suck"));
            Add("MouthSmile", 30, read => Mean(Value(read, "MouthSmileLeft"), Value(read, "MouthSmileRight")), "MouthCornerPullLeft", "MouthCornerSlantLeft", "MouthCornerPullRight", "MouthCornerSlantRight");
            Add("MouthSad", 35, read => Mean(Sad(read, "Left"), Sad(read, "Right")), "MouthFrownLeft", "MouthStretchLeft", "MouthFrownRight", "MouthStretchRight");
            Add("MouthOpen", 35, read => (Value(read, "MouthUpperUpLeft") + Value(read, "MouthUpperUpRight") + Value(read, "MouthLowerDownLeft") + Value(read, "MouthLowerDownRight")) * 0.25f, "MouthUpperUpLeft", "MouthUpperUpRight", "MouthLowerDownLeft", "MouthLowerDownRight");

            // Only these names overlap the ARKit 52-channel vocabulary. They
            // remain usable but cannot by themselves establish a UE profile.
            foreach (var input in new[]
            {
                "BrowDownLeft", "BrowDownRight", "BrowInnerUp", "BrowOuterUpLeft", "BrowOuterUpRight",
                "CheekPuff", "CheekSquintLeft", "CheekSquintRight",
                "EyeBlinkLeft", "EyeBlinkRight", "EyeLookDownLeft", "EyeLookDownRight", "EyeLookInLeft", "EyeLookInRight",
                "EyeLookOutLeft", "EyeLookOutRight", "EyeLookUpLeft", "EyeLookUpRight", "EyeSquintLeft", "EyeSquintRight", "EyeWideLeft", "EyeWideRight",
                "JawForward", "JawLeft", "JawOpen", "JawRight", "MouthClose", "MouthDimpleLeft", "MouthDimpleRight",
                "MouthFrownLeft", "MouthFrownRight", "MouthFunnel", "MouthLeft", "MouthLowerDownLeft", "MouthLowerDownRight",
                "MouthPressLeft", "MouthPressRight", "MouthPucker", "MouthRight", "MouthRollLower", "MouthRollUpper",
                "MouthShrugLower", "MouthShrugUpper", "MouthSmileLeft", "MouthSmileRight", "MouthStretchLeft", "MouthStretchRight",
                "MouthUpperUpLeft", "MouthUpperUpRight", "NoseSneerLeft", "NoseSneerRight", "TongueOut"
            })
            {
                SharedNames.Add(Normalize(input));
            }
            var names = new List<string>(Definitions.Keys);
            names.Sort(StringComparer.Ordinal);
            RegisteredNames = names.AsReadOnly();
        }

        public static bool TryCanonicalize(string value, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Trim();
            // Exporter menu expressions are binary manual selections, even
            // when their final menu label matches a tracking channel.
            var menuBoundary = value.IndexOf('/');
            if (menuBoundary >= 0 && string.Equals(value.Substring(0, menuBoundary).Trim(),
                "VRChat", StringComparison.OrdinalIgnoreCase)) return false;
            if (NormalizedNames.TryGetValue(Normalize(value), out canonical)) return true;
            var boundary = Math.Max(value.LastIndexOf('.'), Math.Max(value.LastIndexOf('/'), value.LastIndexOf(':')));
            if (boundary < 0 || boundary == value.Length - 1) return false;
            return NormalizedNames.TryGetValue(Normalize(value.Substring(boundary + 1)), out canonical);
        }

        public static bool IsExplicit(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsDistinctive(string canonical)
        {
            string name;
            return TryCanonicalize(canonical, out name) && !SharedNames.Contains(Normalize(name));
        }

        public static float Evaluate(string canonical, Func<string, float> read)
        {
            Definition definition;
            if (!TryDefinition(canonical, out definition) || read == null) return 0f;
            return Clamp(definition.Evaluate(read));
        }

        public static bool IsBlink(string canonical)
        {
            string name;
            return TryCanonicalize(canonical, out name) && (name == "EyeClosed" || name == "EyeClosedLeft" || name == "EyeClosedRight");
        }

        public static bool IsEyeLook(string canonical)
        {
            string name;
            return TryCanonicalize(canonical, out name) && name.StartsWith("EyeLook", StringComparison.Ordinal);
        }

        /// <summary>
        /// Selects one representation for each anatomical constituent in a
        /// single mesh. Direct forms precede split forms, then side composites,
        /// symmetric forms and whole-face composites. Partial sets are valid.
        /// </summary>
        public static IReadOnlyList<string> SelectCandidates(IEnumerable<string> canonicalNames)
        {
            var candidates = new List<Definition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (canonicalNames != null)
            {
                foreach (var name in canonicalNames)
                {
                    Definition definition;
                    if (TryDefinition(name, out definition) && seen.Add(definition.Name)) candidates.Add(definition);
                }
            }
            candidates.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : StringComparer.Ordinal.Compare(a.Name, b.Name));
            var occupied = new HashSet<string>(StringComparer.Ordinal);
            var selected = new List<string>();
            foreach (var candidate in candidates)
            {
                var conflict = false;
                foreach (var leaf in candidate.Coverage)
                {
                    if (occupied.Contains(leaf)) { conflict = true; break; }
                }
                if (conflict) continue;
                selected.Add(candidate.Name);
                foreach (var leaf in candidate.Coverage) occupied.Add(leaf);
            }
            return selected.AsReadOnly();
        }

        public static bool Conflicts(string a, string b)
        {
            Definition first;
            Definition second;
            if (!TryDefinition(a, out first) || !TryDefinition(b, out second)) return false;
            foreach (var left in first.Coverage)
            {
                foreach (var right in second.Coverage)
                {
                    if (left == right) return true;
                }
            }
            return false;
        }

        static bool TryDefinition(string value, out Definition definition)
        {
            definition = null;
            string canonical;
            return TryCanonicalize(value, out canonical) && Definitions.TryGetValue(canonical, out definition);
        }

        static void Direct(string name, string input, int priority = 10, params string[] coverage)
        {
            Add(name, priority, read => Value(read, input), coverage);
        }

        static void Add(string name, int priority, Func<Func<string, float>, float> evaluate, params string[] coverage)
        {
            Definitions.Add(name, new Definition { Name = name, Priority = priority, Evaluate = evaluate, Coverage = coverage.Length == 0 ? new[] { name } : coverage });
            NormalizedNames.Add(Normalize(name), name);
        }

        static string[] LipCoverage(string action)
        {
            return new[] { "Lip" + action + "UpperLeft", "Lip" + action + "UpperRight", "Lip" + action + "LowerLeft", "Lip" + action + "LowerRight" };
        }

        static float BrowUp(Func<string, float> read, string side)
        {
            return Value(read, "BrowOuterUp" + side) * 0.6f + Value(read, "BrowInnerUp") * 0.4f;
        }

        static float Sad(Func<string, float> read, string side)
        {
            return Math.Max(Value(read, "MouthFrown" + side), Value(read, "MouthStretch" + side));
        }

        static float Value(Func<string, float> read, string name) { return Clamp(read(name)); }
        static float Mean(float a, float b) { return (a + b) * 0.5f; }
        static float Clamp(float value) { return float.IsNaN(value) || float.IsInfinity(value) ? 0f : Math.Max(0f, Math.Min(1f, value)); }

        static string Normalize(string value)
        {
            var normalized = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (character == '_' || character == '-' || char.IsWhiteSpace(character)) continue;
                normalized.Append(char.ToLowerInvariant(character));
            }
            return normalized.ToString();
        }
    }
}
