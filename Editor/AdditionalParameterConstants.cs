using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace VRVlog.LilToonExporter
{
    // An additional playable need not execute a reset whose only possible
    // effect is assigning the value already present throughout this probe.
    // Prove every producer together; a reset cannot certify its own gate.
    internal static class AdditionalParameterConstants
    {
        internal static Dictionary<string, float> Prove(RuntimeAnimatorController runtime,
            VrChatExpressionMenu.Source source, FixedExpressionContext context,
            IDictionary<string, float> defaults = null, IDictionary<string, float> selection = null)
        {
            var result = new Dictionary<string, float>(StringComparer.Ordinal);
            if (source == null || source.OtherControllers.Count == 0) return result;
            var runtimes = new[] { runtime }.Concat(source.OtherControllers.Where(value => value != null)).Distinct().ToArray();
            var fxParameters = new HashSet<string>(ExpressionDependencies.Controller(runtime).parameters.Where(parameter => parameter != null)
                .Select(parameter => parameter.name), StringComparer.Ordinal);
            var declarations = new Dictionary<string, List<AnimatorControllerParameter>>(StringComparer.Ordinal);
            var raw = new List<ExpressionDependencies.Layer>();
            var unknown = new List<string>();
            foreach (var current in runtimes)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in ExpressionDependencies.Controller(current).parameters)
                {
                    if (parameter == null || string.IsNullOrEmpty(parameter.name) || !names.Add(parameter.name)) return result;
                    if (!declarations.TryGetValue(parameter.name, out var values))
                        declarations.Add(parameter.name, values = new List<AnimatorControllerParameter>());
                    values.Add(parameter);
                }
                raw.AddRange(ExpressionDependencies.Inspect(current, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true,
                    typedConditions: true, fxLayerCount: ExpressionDependencies.Controller(runtime).layers.Length));
            }
            if (unknown.Count != 0) return result;
            var written = new HashSet<string>(raw.SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var initial = new Dictionary<string, float>(StringComparer.Ordinal);
            var invariant = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var pair in declarations)
            {
                var name = pair.Key; var values = pair.Value; var type = values[0].type;
                if (type != AnimatorControllerParameterType.Bool && type != AnimatorControllerParameterType.Int &&
                    type != AnimatorControllerParameterType.Float || values.Any(value => value.type != type) ||
                    values.Any(value => !Default(value).Equals(Default(values[0]))) ||
                    !NeutralShapeSnapshot.Finite(Default(values[0]))) continue;
                // External producers have no inferred constant. The explicit
                // export environment must supply their initial input.
                if ((VrChatParameterDriver.BuiltIn.Contains(name) || source.ExternalParameters.Contains(name)) &&
                    context?.Values.ContainsKey(name) != true) continue;
                var value = Default(values[0]);
                if (source.Defaults.TryGetValue(name, out var supplied)) value = supplied;
                if (defaults?.TryGetValue(name, out supplied) == true) value = supplied;
                if (context?.Values.TryGetValue(name, out supplied) == true) value = supplied;
                if (!NormalizeInput(value, type, out value)) continue;
                var final = selection?.TryGetValue(name, out supplied) == true ? supplied : value;
                if (!NormalizeInput(final, type, out final) || !value.Equals(final)) continue;
                initial.Add(name, value);
                if (!written.Contains(name)) invariant.Add(name, value);
            }
            // Only the unwritten seed may prune paths. A conflicting writer
            // behind a gate on the candidate remains possible until proved.
            var reachable = new List<ExpressionDependencies.Layer>();
            foreach (var current in runtimes)
                reachable.AddRange(ExpressionDependencies.Inspect(current, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), unknown, true,
                    invariant, typedConditions: true, fxLayerCount: ExpressionDependencies.Controller(runtime).layers.Length));
            if (unknown.Count != 0 || reachable.SelectMany(layer => layer.Clips)
                .Any(clip => AnimationUtility.GetAnimationEvents(clip).Length != 0)) return result;
            var otherWrites = new HashSet<string>(runtimes.Skip(1).SelectMany(current =>
                ExpressionDependencies.Inspect(current, null,
                    new Dictionary<StateMachineBehaviour, VrChatParameterDriver.Program>(), new List<string>(), true,
                    invariant, typedConditions: true, fxLayerCount: ExpressionDependencies.Controller(runtime).layers.Length))
                .SelectMany(layer => layer.Writes), StringComparer.Ordinal);
            var curves = new HashSet<string>(reachable.SelectMany(layer => layer.CurveWrites), StringComparer.Ordinal);
            var programs = reachable.SelectMany(layer => layer.DriverPrograms).Distinct().ToArray();
            if (programs.Any(program => program.Error != null)) return result;
            foreach (var pair in initial.Where(pair => otherWrites.Contains(pair.Key)))
            {
                var name = pair.Key;
                if (!fxParameters.Contains(name) || VrChatParameterDriver.BuiltIn.Contains(name) || curves.Contains(name)) continue;
                var type = declarations[name][0].type;
                if (source.ExpressionParameterTypes.TryGetValue(name, out var declaredType) && declaredType != type.ToString()) continue;
                var operations = programs.SelectMany(program => program.Operations).Where(operation => operation.Destination == name).ToArray();
                if (operations.Length == 0 || operations.Any(operation => operation.Kind != "Set" || operation.Error != null ||
                    !SetValue(operation.Value, type, source.ExpressionParameters.Contains(name), out var value) || !value.Equals(pair.Value))) continue;
                result.Add(name, pair.Value);
            }
            return result;
        }

        private static float Default(AnimatorControllerParameter parameter) =>
            parameter.type == AnimatorControllerParameterType.Bool ? (parameter.defaultBool ? 1 : 0) :
            parameter.type == AnimatorControllerParameterType.Int ? parameter.defaultInt : parameter.defaultFloat;

        private static bool NormalizeInput(float value, AnimatorControllerParameterType type, out float normalized)
        {
            normalized = value;
            if (!NeutralShapeSnapshot.Finite(value)) return false;
            if (type == AnimatorControllerParameterType.Bool) normalized = value == 0 ? 0 : 1;
            if (type == AnimatorControllerParameterType.Int)
            {
                if (value < int.MinValue || (double)value > int.MaxValue) return false;
                normalized = Mathf.RoundToInt(value);
            }
            return true;
        }

        private static bool SetValue(float value, AnimatorControllerParameterType type, bool expressionParameter, out float normalized)
        {
            normalized = value;
            if (!NeutralShapeSnapshot.Finite(value)) return false;
            if (type == AnimatorControllerParameterType.Bool) normalized = value == 0 ? 0 : 1;
            if (type == AnimatorControllerParameterType.Int) normalized = (float)Math.Truncate(value);
            if (expressionParameter)
            {
                if (type == AnimatorControllerParameterType.Int) normalized = Mathf.Clamp(normalized, 0, 255);
                if (type == AnimatorControllerParameterType.Float) normalized = Mathf.Clamp(normalized, -1, 1);
            }
            return type != AnimatorControllerParameterType.Int || normalized >= int.MinValue && (double)normalized <= int.MaxValue;
        }
    }
}
