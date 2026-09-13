using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;

namespace VRVlog.LilToonExporter
{
    // Serialized SDK data only: never invoke the SDK's runtime delegates or
    // install a handler on its process-wide static events.
    internal static class VrChatParameterDriver
    {
        internal sealed class Operation
        {
            internal string Kind, Destination, Source;
            internal float Value, SourceMin, SourceMax, DestinationMin, DestinationMax;
            internal bool ConvertRange;
            internal string Error;
        }

        internal sealed class Program
        {
            internal string Location, Error;
            internal bool LocalOnly;
            internal readonly List<Operation> Operations = new List<Operation>();
        }

        internal static readonly HashSet<string> BuiltIn = new HashSet<string>(new[]
        {
            "IsLocal", "Viseme", "Voice", "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight",
            "AngularY", "VelocityX", "VelocityY", "VelocityZ", "VelocityMagnitude", "Upright", "Grounded", "Seated",
            "AFK", "TrackingType", "VRMode", "MuteSelf", "InStation", "Earmuffs", "IsOnFriendsList",
            "AvatarVersion", "ScaleModified", "ScaleFactor", "ScaleFactorInverse", "EyeHeightAsMeters",
            "EyeHeightAsPercent", "IsAnimatorEnabled", "PreviewMode"
        }, StringComparer.Ordinal);

        internal static bool IsDriver(StateMachineBehaviour value) => value != null &&
            (value.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAvatarParameterDriver" ||
             value.GetType().FullName == "VRC.SDKBase.VRC_AvatarParameterDriver");

        internal static bool IsTracking(StateMachineBehaviour value) => value != null &&
            (value.GetType().FullName == "VRC.SDK3.Avatars.Components.VRCAnimatorTrackingControl" ||
             value.GetType().FullName == "VRC.SDKBase.VRC_AnimatorTrackingControl");

        internal static Program Read(StateMachineBehaviour behaviour, string location)
        {
            var result = new Program { Location = location };
            try
            {
                result.LocalOnly = Required(behaviour, "localOnly") is bool local ? local : throw new InvalidOperationException("localOnlyが不正です。");
                if (!(Required(behaviour, "parameters") is IEnumerable items)) throw new InvalidOperationException("parametersが不正です。");
                foreach (var item in items)
                {
                    var op = new Operation();
                    result.Operations.Add(op);
                    try
                    {
                        op.Destination = Required(item, "name") as string;
                        op.Kind = Required(item, "type").ToString();
                        if (string.IsNullOrEmpty(op.Destination)) throw new InvalidOperationException("書き込み先がありません。");
                        switch (op.Kind)
                        {
                            case "Set": case "Add": op.Value = Number(item, "value"); break;
                            case "Copy":
                                op.Source = Required(item, "source") as string;
                                if (string.IsNullOrEmpty(op.Source)) throw new InvalidOperationException("Copy元がありません。");
                                op.ConvertRange = Required(item, "convertRange") is bool convert ? convert : throw new InvalidOperationException("convertRangeが不正です。");
                                if (op.ConvertRange)
                                {
                                    op.SourceMin = Number(item, "sourceMin"); op.SourceMax = Number(item, "sourceMax");
                                    op.DestinationMin = Number(item, "destMin"); op.DestinationMax = Number(item, "destMax");
                                    if (op.SourceMin == op.SourceMax) throw new InvalidOperationException("Copy元の範囲が0です。");
                                }
                                break;
                            case "Random": op.Error = "Randomは固定表情の値を確定できません。"; break;
                            default: throw new InvalidOperationException("未対応の操作です。");
                        }
                    }
                    catch (Exception error) when (error is InvalidOperationException || error is FormatException || error is InvalidCastException || error is OverflowException)
                    { op.Error = error.Message; }
                    if (string.IsNullOrEmpty(op.Destination)) result.Error = "Driverの書き込み先を特定できません。";
                }
            }
            catch (InvalidOperationException error) { result.Error = error.Message; }
            return result;
        }

        private static object Required(object value, string name) => VrChatExpressionMenu.Member(value, name) ??
            throw new InvalidOperationException("Driverの設定を読み取れません: " + name);

        private static float Number(object value, string name)
        {
            var number = Convert.ToSingle(Required(value, name), CultureInfo.InvariantCulture);
            if (float.IsNaN(number) || float.IsInfinity(number)) throw new InvalidOperationException("Driverの数値が不正です: " + name);
            return number;
        }

        internal static void Execute(Program program, IDictionary<string, AnimatorControllerParameterType> types,
            ISet<string> expressionParameters, ISet<string> needed, bool isLocal, Func<string, double> read, Action<string, double> write)
        {
            if (program.LocalOnly && !isLocal) return;
            if (program.Error != null) throw new InvalidOperationException(program.Location + " / Parameter Driver: " + program.Error);
            for (var index = 0; index < program.Operations.Count; index++)
            {
                var op = program.Operations[index];
                // Known SDK operations whose destinations cannot reach this
                // expression need not be approximated (including Random).
                if (!needed.Contains(op.Destination)) continue;
                var label = program.Location + " / Parameter Driver " + (index + 1) + " (" + op.Kind + " → " + op.Destination + ")";
                try
                {
                    if (op.Error != null) throw new InvalidOperationException(op.Error);
                    if (BuiltIn.Contains(op.Destination)) throw new InvalidOperationException("VRChat組み込みパラメーターへの書き込みはできません。");
                    if (!types.TryGetValue(op.Destination, out var type) || type == AnimatorControllerParameterType.Trigger)
                        throw new InvalidOperationException("書き込み先の型を解決できません。");
                    double value;
                    if (op.Kind == "Copy")
                    {
                        if (BuiltIn.Contains(op.Source)) throw new InvalidOperationException("VRChat組み込みパラメーターをCopy元として再現できません。");
                        if (!types.TryGetValue(op.Source, out var sourceType) || sourceType == AnimatorControllerParameterType.Trigger)
                            throw new InvalidOperationException("Copy元の型を解決できません: " + op.Source);
                        value = read(op.Source);
                        if (op.ConvertRange)
                        {
                            var t = Math.Max(0, Math.Min(1, (value - op.SourceMin) / ((double)op.SourceMax - op.SourceMin)));
                            value = op.DestinationMin + t * ((double)op.DestinationMax - op.DestinationMin);
                        }
                    }
                    else if (op.Kind == "Add")
                    {
                        if (type == AnimatorControllerParameterType.Bool) throw new InvalidOperationException("BoolへのAddは未対応です。");
                        value = read(op.Destination) + (double)op.Value;
                    }
                    else if (op.Kind == "Set") value = op.Value;
                    else throw new InvalidOperationException("固定表情に変換できないDriver操作です。");
                    if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("演算結果が不正です。");
                    if (type == AnimatorControllerParameterType.Bool) value = value == 0 ? 0 : 1;
                    if (type == AnimatorControllerParameterType.Int)
                        value = op.Kind == "Copy" ? Math.Floor(value) : Math.Truncate(value);
                    if (expressionParameters.Contains(op.Destination))
                    {
                        if (type == AnimatorControllerParameterType.Int) value = Math.Max(0, Math.Min(255, value));
                        if (type == AnimatorControllerParameterType.Float) value = Math.Max(-1, Math.Min(1, value));
                    }
                    if (type == AnimatorControllerParameterType.Int && (value < int.MinValue || value > int.MaxValue) || Math.Abs(value) > float.MaxValue)
                        throw new InvalidOperationException("演算結果がパラメーターの範囲を超えます。");
                    write(op.Destination, value);
                }
                catch (InvalidOperationException error) { throw new InvalidOperationException(label + ": " + error.Message); }
            }
        }
    }
}
