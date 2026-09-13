using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-1 tool #1 (contract §4): the universal editor escape hatch.
    ///
    /// <b>Skeleton scope.</b> Two mechanisms work today and need no compiler:
    /// <list type="bullet">
    /// <item><c>invoke</c> — call any public static method in a loaded editor assembly.</item>
    /// <item><c>menu_item</c> — fire any editor menu command by path.</item>
    /// </list>
    /// Free-form C# source (<c>compile</c>) is stubbed behind a feature flag; see the
    /// TODO on <see cref="CompileAndRun"/>.
    /// </summary>
    public static class RevvyExecuteScriptTool
    {
        [RevvyTool(
            "execute_script",
            "Run editor-side C# without a round trip through the project's own code. " +
            "Actions: invoke (call a public static method by fully-qualified name, e.g. " +
            "'UnityEditor.AssetDatabase.SaveAssets', with positional args), menu_item (execute an editor menu " +
            "command by path, e.g. 'File/Save Project'), describe (list the public static methods of a type, " +
            "for discovering invoke targets), compile (arbitrary C# source — NOT implemented yet, requires " +
            "REVVY_UNITY_ALLOW_DYNAMIC_COMPILE). This tool can do anything the editor can do: destructive.",
            Title = "Execute editor script",
            Destructive = true,
            OpenWorld = true,
            ReadOnlyActions = new[] { "describe" },
            TimeoutMs = 120000)]
        public static RevvyJson ExecuteScript(
            [RevvyToolParam("Execution mechanism.",
                Enum = new[] { "invoke", "menu_item", "describe", "compile" })]
            string action = "invoke",
            [RevvyToolParam("For invoke/describe: fully-qualified target, e.g. " +
                            "'UnityEditor.AssetDatabase.SaveAssets' (invoke) or 'UnityEditor.AssetDatabase' " +
                            "(describe). For menu_item: the menu path.")]
            string target = null,
            [RevvyToolParam("Positional arguments for invoke, as a JSON array. Values are converted to the " +
                            "target method's parameter types.",
                SchemaJson = "{\"type\":\"array\"}")]
            RevvyJson args = null,
            [RevvyToolParam("Assembly simple name to disambiguate when several assemblies declare the type.")]
            string assembly = null,
            [RevvyToolParam("C# source for action=compile (feature-flagged, not implemented).")]
            string code = null)
        {
            string normalized = (action ?? "invoke").Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "invoke":
                    return Invoke(target, args, assembly);
                case "menu_item":
                    return ExecuteMenuItem(target);
                case "describe":
                    return Describe(target, assembly);
                case "compile":
                    return CompileAndRun(code);
                default:
                    throw new RevvyToolException(
                        "Unknown action '" + action + "'. Expected one of: invoke, menu_item, describe, compile.");
            }
        }

        // ------------------------------------------------------------------ invoke

        private static RevvyJson Invoke(string target, RevvyJson args, string assemblyName)
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new RevvyToolException("action=invoke requires 'target' (e.g. 'UnityEditor.AssetDatabase.Refresh').");
            }

            int lastDot = target.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == target.Length - 1)
            {
                throw new RevvyToolException(
                    "'target' must be a fully-qualified static method: 'Namespace.Type.Method'.");
            }

            string typeName = target.Substring(0, lastDot);
            string methodName = target.Substring(lastDot + 1);

            Type type = ResolveType(typeName, assemblyName);
            if (type == null)
            {
                throw new RevvyToolException(
                    "Type '" + typeName + "' not found in any loaded assembly" +
                    (string.IsNullOrEmpty(assemblyName) ? "." : " named '" + assemblyName + "'."));
            }

            int argumentCount = args != null && args.IsArray ? args.Count : 0;
            MethodInfo method = SelectMethod(type, methodName, argumentCount);
            if (method == null)
            {
                throw new RevvyToolException(
                    "No public static '" + methodName + "' on " + type.FullName + " accepting " +
                    argumentCount + " argument(s). Use action=describe to list candidates.");
            }

            ParameterInfo[] parameters = method.GetParameters();
            object[] bound = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (args != null && args.IsArray && i < args.Count && !args[i].IsNull)
                {
                    bound[i] = RevvyToolRegistry.CoerceValue(
                        args[i], parameters[i].ParameterType, "execute_script", parameters[i].Name);
                }
                else if (parameters[i].HasDefaultValue)
                {
                    bound[i] = parameters[i].DefaultValue;
                }
                else
                {
                    throw new RevvyToolException(
                        "Argument " + i + " ('" + parameters[i].Name + "') is required by " + target + ".");
                }
            }

            object returned;
            try
            {
                returned = method.Invoke(null, bound);
            }
            catch (TargetInvocationException invocation)
            {
                throw new RevvyToolException(
                    target + " threw " + RevvyLog.Describe(invocation.InnerException ?? invocation));
            }

            RevvyJson result = RevvyJson.Object()
                .Set("success", true)
                .Set("action", "invoke")
                .Set("target", type.FullName + "." + method.Name)
                .Set("signature", DescribeSignature(method))
                .Set("returns", method.ReturnType == typeof(void) ? "void" : method.ReturnType.Name);

            if (method.ReturnType != typeof(void))
            {
                result.Set("value", DescribeValue(returned));
            }

            return result;
        }

        private static MethodInfo SelectMethod(Type type, string methodName, int argumentCount)
        {
            MethodInfo[] candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                .Where(m => !m.IsGenericMethodDefinition)
                .ToArray();

            if (candidates.Length == 0)
            {
                return null;
            }

            // Exact arity first, then the shortest overload whose extra parameters are optional.
            foreach (MethodInfo candidate in candidates.OrderBy(m => m.GetParameters().Length))
            {
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length < argumentCount)
                {
                    continue;
                }

                bool satisfiable = true;
                for (int i = argumentCount; i < parameters.Length; i++)
                {
                    if (!parameters[i].HasDefaultValue)
                    {
                        satisfiable = false;
                        break;
                    }
                }

                if (satisfiable)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Type ResolveType(string typeName, string assemblyName)
        {
            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyName) &&
                    !string.Equals(candidate.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type type;
                try
                {
                    type = candidate.GetType(typeName, false, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        // ------------------------------------------------------------------ menu / describe

        private static RevvyJson ExecuteMenuItem(string menuPath)
        {
            if (string.IsNullOrEmpty(menuPath))
            {
                throw new RevvyToolException("action=menu_item requires 'target' (the menu path, e.g. 'File/Save Project').");
            }

            bool executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed)
            {
                throw new RevvyToolException(
                    "Menu item '" + menuPath + "' was not found or is currently disabled.");
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "menu_item")
                .Set("target", menuPath);
        }

        private static RevvyJson Describe(string typeName, string assemblyName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                throw new RevvyToolException("action=describe requires 'target' (a fully-qualified type name).");
            }

            Type type = ResolveType(typeName, assemblyName);
            if (type == null)
            {
                throw new RevvyToolException("Type '" + typeName + "' not found in any loaded assembly.");
            }

            RevvyJson methods = RevvyJson.Array();
            foreach (MethodInfo method in type
                         .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                         .Where(m => !m.IsSpecialName && !m.IsGenericMethodDefinition)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                methods.Add(RevvyJson.String(DescribeSignature(method)));
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("action", "describe")
                .Set("type", type.FullName)
                .Set("assembly", type.Assembly.GetName().Name)
                .Set("static_methods", methods)
                .Set("count", methods.Count);
        }

        // ------------------------------------------------------------------ compile

        /// <summary>
        /// TODO(tier-2): compile and run free-form C#.
        ///
        /// Plan: reference <c>Microsoft.CodeAnalysis.CSharp.Scripting</c> (already vendored
        /// inside the editor for some Unity versions, otherwise shipped as an optional
        /// sub-package so the base package stays dependency-free per contract §8.2),
        /// compile against the current editor assemblies via
        /// <c>CompilationPipeline.GetAssemblies(AssembliesType.Editor)</c>, and execute the
        /// emitted entry point on the main thread. Blocked on:
        ///   1. deciding how to ship Roslyn without adding a NuGet dependency to the base package;
        ///   2. an execution sandbox/consent story — arbitrary source is strictly more
        ///      dangerous than the reflection path, which is at least name-addressable and auditable.
        /// Until then this action fails loudly rather than pretending to work.
        /// </summary>
        private static RevvyJson CompileAndRun(string code)
        {
            if (!RevvyEnv.DynamicCompileEnabled())
            {
                throw new RevvyToolException(
                    "action=compile is disabled. Dynamic C# compilation is not implemented in this bridge " +
                    "version; set " + RevvyEnv.EnvAllowDynamicCompile + "=1 to see the stub response. " +
                    "Use action=invoke (public static method call) or action=menu_item instead.");
            }

            return RevvyJson.Object()
                .Set("success", false)
                .Set("action", "compile")
                .Set("error", "Roslyn dynamic compilation is not implemented in bridge version " +
                              RevvyMcpHandler.ServerVersion + ".")
                .Set("feature_flag", RevvyEnv.EnvAllowDynamicCompile)
                .Set("received_code_length", code != null ? code.Length : 0)
                .Set("workaround", "Use action=invoke to call an existing public static method, or " +
                                   "action=menu_item for editor menu commands.");
        }

        // ------------------------------------------------------------------ formatting

        private static string DescribeSignature(MethodInfo method)
        {
            List<string> parts = new List<string>();
            foreach (ParameterInfo parameter in method.GetParameters())
            {
                parts.Add(parameter.ParameterType.Name + " " + parameter.Name +
                          (parameter.HasDefaultValue ? " = ..." : string.Empty));
            }

            return method.ReturnType.Name + " " + method.Name + "(" + string.Join(", ", parts.ToArray()) + ")";
        }

        /// <summary>Renders a returned CLR value as JSON, flattening collections of scalars.</summary>
        private static RevvyJson DescribeValue(object value)
        {
            if (value == null)
            {
                return RevvyJson.Null();
            }

            if (value is string)
            {
                return RevvyJson.String((string)value);
            }

            if (value is bool)
            {
                return RevvyJson.Bool((bool)value);
            }

            if (value is float || value is double || value is decimal)
            {
                return RevvyJson.Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }

            if (value is sbyte || value is byte || value is short || value is ushort ||
                value is int || value is uint || value is long || value is ulong)
            {
                return RevvyJson.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            System.Collections.IEnumerable sequence = value as System.Collections.IEnumerable;
            if (sequence != null)
            {
                RevvyJson array = RevvyJson.Array();
                int emitted = 0;
                foreach (object item in sequence)
                {
                    if (emitted++ >= 200)
                    {
                        array.Add(RevvyJson.String("... [truncated]"));
                        break;
                    }

                    array.Add(RevvyJson.String(Convert.ToString(item, CultureInfo.InvariantCulture)));
                }

                return array;
            }

            return RevvyJson.String(Convert.ToString(value, CultureInfo.InvariantCulture));
        }
    }
}
