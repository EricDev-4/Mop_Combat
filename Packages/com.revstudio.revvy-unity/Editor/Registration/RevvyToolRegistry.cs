using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// One reflected <see cref="RevvyToolAttribute"/> method plus its generated schema.
    /// </summary>
    public sealed class RevvyToolDescriptor
    {
        public string Name;
        public string Description;
        public MethodInfo Method;
        public ParameterInfo[] Parameters;
        public RevvyToolAttribute Attribute;

        /// <summary>Cached MCP tool descriptor (name/description/inputSchema/annotations).</summary>
        public RevvyJson Schema;

        public int TimeoutMs
        {
            get
            {
                return Attribute.TimeoutMs > 0 ? Attribute.TimeoutMs : RevvyMainThread.DefaultTimeoutMs;
            }
        }
    }

    /// <summary>
    /// Reflection-based tool discovery and dispatch (contract §8.2).
    ///
    /// Scans every loaded assembly once for public static methods carrying
    /// <see cref="RevvyToolAttribute"/>, derives a JSON Schema from the method
    /// signature, and dispatches <c>tools/call</c> by binding JSON arguments onto the
    /// parameter list. The invocation itself is marshalled onto the Unity main thread.
    /// </summary>
    public static class RevvyToolRegistry
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, RevvyToolDescriptor> _tools;
        private static RevvyJson _listCache;

        /// <summary>Discovered tools, sorted by name. Scans on first use.</summary>
        public static IReadOnlyList<RevvyToolDescriptor> Tools
        {
            get
            {
                EnsureScanned();
                lock (Gate)
                {
                    return _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                }
            }
        }

        public static int Count
        {
            get
            {
                EnsureScanned();
                lock (Gate)
                {
                    return _tools.Count;
                }
            }
        }

        /// <summary>Drops the cache; the next access rescans. Used after domain reload.</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                _tools = null;
                _listCache = null;
            }
        }

        /// <summary>The <c>tools</c> array for <c>tools/list</c> and for tools-manifest.json.</summary>
        public static RevvyJson BuildToolListJson()
        {
            return BuildToolListJson(false);
        }

        /// <summary>
        /// Build a scope-aware tool list. Read-only callers see only purely
        /// read-only tools and mixed tools with at least one read-only action;
        /// mixed action enums are narrowed without mutating the cached Full schema.
        /// </summary>
        public static RevvyJson BuildToolListJson(bool readOnly)
        {
            EnsureScanned();
            lock (Gate)
            {
                if (!readOnly)
                {
                    if (_listCache == null)
                    {
                        RevvyJson fullArray = RevvyJson.Array();
                        foreach (RevvyToolDescriptor descriptor in
                                 _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
                        {
                            fullArray.Add(descriptor.Schema);
                        }

                        _listCache = fullArray;
                    }

                    return _listCache;
                }

                RevvyJson safeArray = RevvyJson.Array();
                foreach (RevvyToolDescriptor descriptor in
                         _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    if (!IsVisibleToReadOnlyScope(descriptor))
                    {
                        continue;
                    }

                    RevvyJson safeSchema = BuildReadOnlySchema(descriptor);
                    if (safeSchema != null)
                    {
                        safeArray.Add(safeSchema);
                    }
                }

                return safeArray;
            }
        }

        /// <summary>
        /// Final editor-side invariant for a signed read-only request. Proxy-side
        /// filtering is useful UX, but cannot be the authority for editor mutation.
        /// </summary>
        public static bool IsReadOnlyCallAllowed(
            string name,
            RevvyJson arguments,
            out string denial)
        {
            RevvyToolDescriptor descriptor = Find(name);
            if (descriptor == null || !IsVisibleToReadOnlyScope(descriptor))
            {
                denial = "This Revvy session is Safe/read-only. " +
                    "Start a Full access session to run mutating tools.";
                return false;
            }

            if (IsSafeReadOnlyTool(descriptor))
            {
                denial = null;
                return true;
            }

            string action = NormalizeAction(
                arguments != null && arguments.IsObject ? arguments.Get("action") : null);
            string[] allowed = ReadOnlyActions(descriptor);
            if (allowed.Contains(action, StringComparer.Ordinal))
            {
                denial = null;
                return true;
            }

            denial = "Action '" + action + "' needs a Full access session. " +
                "This Safe session offers: " + string.Join(", ", allowed) + ".";
            return false;
        }

        private static bool IsSafeReadOnlyTool(RevvyToolDescriptor descriptor)
        {
            return descriptor != null && descriptor.Attribute != null &&
                descriptor.Attribute.ReadOnly && !descriptor.Attribute.Destructive;
        }

        private static bool IsVisibleToReadOnlyScope(RevvyToolDescriptor descriptor)
        {
            return IsSafeReadOnlyTool(descriptor) || ReadOnlyActions(descriptor).Length > 0;
        }

        private static string[] ReadOnlyActions(RevvyToolDescriptor descriptor)
        {
            if (descriptor == null || descriptor.Attribute == null ||
                descriptor.Attribute.ReadOnlyActions == null)
            {
                return new string[0];
            }

            return descriptor.Attribute.ReadOnlyActions
                .Select(action => (action ?? string.Empty).Trim().ToLowerInvariant())
                .Where(action => action.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static string NormalizeAction(RevvyJson action)
        {
            return (action != null ? action.AsString(string.Empty) : string.Empty)
                .Trim()
                .ToLowerInvariant();
        }

        private static RevvyJson BuildReadOnlySchema(RevvyToolDescriptor descriptor)
        {
            if (IsSafeReadOnlyTool(descriptor))
            {
                return descriptor.Schema;
            }

            string[] allowed = ReadOnlyActions(descriptor);
            if (allowed.Length == 0)
            {
                return null;
            }

            // Parse a serialized copy: the Full descriptor is cached and published
            // in the manifest, so a Safe listing must never narrow it in place.
            RevvyJson narrowed = RevvyJson.Parse(descriptor.Schema.ToJson(false));
            RevvyJson schema = narrowed != null ? narrowed.Get("inputSchema") : null;
            RevvyJson properties = schema != null ? schema.Get("properties") : null;
            RevvyJson action = properties != null ? properties.Get("action") : null;
            RevvyJson actionEnum = action != null ? action.Get("enum") : null;
            if (actionEnum != null && actionEnum.IsArray)
            {
                HashSet<string> allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
                RevvyJson surviving = RevvyJson.Array();
                foreach (RevvyJson value in actionEnum.Items)
                {
                    string normalized = NormalizeAction(value);
                    if (allowedSet.Contains(normalized))
                    {
                        surviving.Add(value);
                    }
                }

                if (surviving.Count == 0)
                {
                    return null;
                }

                action.Set("enum", surviving);
            }

            string description = narrowed.Get("description") != null
                ? narrowed.Get("description").AsString(string.Empty)
                : string.Empty;
            narrowed.Set(
                "description",
                (description + " Safe (read-only) session: only these actions are available — " +
                 string.Join(", ", allowed) + ".").Trim());
            return narrowed;
        }

        public static RevvyToolDescriptor Find(string name)
        {
            EnsureScanned();
            lock (Gate)
            {
                RevvyToolDescriptor descriptor;
                return _tools.TryGetValue(name ?? string.Empty, out descriptor) ? descriptor : null;
            }
        }

        /// <summary>
        /// Binds <paramref name="arguments"/> to the tool signature and invokes it on the
        /// Unity main thread.
        /// </summary>
        /// <exception cref="RevvyToolException">Unknown tool, bad arguments, or tool failure.</exception>
        public static RevvyJson Invoke(string name, RevvyJson arguments)
        {
            RevvyToolDescriptor descriptor = Find(name);
            if (descriptor == null)
            {
                throw new RevvyToolException("Unknown tool: " + (name ?? "<null>"));
            }

            object[] bound = BindArguments(descriptor, arguments);

            try
            {
                return RevvyMainThread.Run(() =>
                {
                    object result = descriptor.Method.Invoke(null, bound);
                    RevvyJson json = result as RevvyJson;
                    if (json != null)
                    {
                        return json;
                    }

                    // Tools should return RevvyJson; tolerate void/other for forward compat.
                    RevvyJson wrapper = RevvyJson.Object().Set("success", true);
                    if (result != null)
                    {
                        wrapper.Set("result", Convert.ToString(result, CultureInfo.InvariantCulture));
                    }

                    return wrapper;
                }, descriptor.TimeoutMs);
            }
            catch (TargetInvocationException invocation)
            {
                RevvyToolException toolFailure = invocation.InnerException as RevvyToolException;
                if (toolFailure != null)
                {
                    throw new RevvyToolException(
                        toolFailure.Message,
                        toolFailure.ErrorCode,
                        invocation);
                }

                throw new RevvyToolException(RevvyLog.Describe(invocation.InnerException ?? invocation));
            }
        }

        // ------------------------------------------------------------------ scanning

        private static void EnsureScanned()
        {
            // The whole scan runs under the lock. Publishing an empty dictionary first
            // and filling it afterwards would let a concurrent request observe zero
            // tools mid-scan — a real race, since several socket threads can hit
            // tools/list simultaneously right after a domain reload.
            lock (Gate)
            {
                if (_tools != null)
                {
                    return;
                }

                Scan();
            }
        }

        private static void Scan()
        {
            Dictionary<string, RevvyToolDescriptor> discovered =
                new Dictionary<string, RevvyToolDescriptor>(StringComparer.Ordinal);

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (SkipAssembly(assembly))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException partial)
                {
                    // A half-loaded assembly still yields the types that did resolve.
                    types = partial.Types.Where(t => t != null).ToArray();
                }
                catch (Exception exception)
                {
                    RevvyLog.Verbose("Skipped assembly " + assembly.GetName().Name + ": " +
                                     RevvyLog.Describe(exception));
                    continue;
                }

                foreach (Type type in types)
                {
                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (MethodInfo method in methods)
                    {
                        RevvyToolAttribute attribute =
                            (RevvyToolAttribute)Attribute.GetCustomAttribute(method, typeof(RevvyToolAttribute));
                        if (attribute == null || string.IsNullOrEmpty(attribute.Name))
                        {
                            continue;
                        }

                        RevvyToolDescriptor existing;
                        if (discovered.TryGetValue(attribute.Name, out existing))
                        {
                            RevvyLog.Warn("Duplicate tool name '" + attribute.Name + "' on " +
                                          type.FullName + "." + method.Name + "; keeping " +
                                          existing.Method.DeclaringType.FullName + ".");
                            continue;
                        }

                        RevvyToolDescriptor descriptor = new RevvyToolDescriptor
                        {
                            Name = attribute.Name,
                            Description = attribute.Description ?? string.Empty,
                            Method = method,
                            Parameters = method.GetParameters(),
                            Attribute = attribute
                        };
                        descriptor.Schema = BuildSchema(descriptor);
                        discovered[attribute.Name] = descriptor;
                    }
                }
            }

            _tools = discovered;
            _listCache = null;
            RevvyLog.Verbose("Tool scan found " + discovered.Count + " tool(s).");
        }

        /// <summary>Skips BCL/Unity assemblies that can never carry a [RevvyTool].</summary>
        private static bool SkipAssembly(Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return true;
            }

            string name = assembly.GetName().Name ?? string.Empty;
            return name.StartsWith("System", StringComparison.Ordinal)
                || name.StartsWith("mscorlib", StringComparison.Ordinal)
                || name.StartsWith("netstandard", StringComparison.Ordinal)
                || name.StartsWith("Mono.", StringComparison.Ordinal)
                || name.StartsWith("UnityEngine", StringComparison.Ordinal)
                || name.StartsWith("UnityEditor", StringComparison.Ordinal)
                || name.StartsWith("Unity.", StringComparison.Ordinal)
                || name.StartsWith("nunit.", StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ schema

        private static RevvyJson BuildSchema(RevvyToolDescriptor descriptor)
        {
            RevvyJson properties = RevvyJson.Object();
            RevvyJson required = RevvyJson.Array();

            foreach (ParameterInfo parameter in descriptor.Parameters)
            {
                RevvyToolParamAttribute meta = (RevvyToolParamAttribute)
                    Attribute.GetCustomAttribute(parameter, typeof(RevvyToolParamAttribute));

                RevvyJson property = null;
                if (meta != null && !string.IsNullOrEmpty(meta.SchemaJson))
                {
                    property = RevvyJson.Parse(meta.SchemaJson);
                    if (property == null || !property.IsObject)
                    {
                        RevvyLog.Warn("Ignoring malformed SchemaJson on " + descriptor.Name + "." + parameter.Name);
                        property = null;
                    }
                }

                if (property == null)
                {
                    property = DescribeType(parameter.ParameterType);
                }

                if (meta != null)
                {
                    if (!string.IsNullOrEmpty(meta.Description))
                    {
                        property.Set("description", meta.Description);
                    }

                    if (meta.Enum != null && meta.Enum.Length > 0)
                    {
                        property.Set("enum", RevvyJson.ArrayOf(meta.Enum));
                    }
                }

                if (parameter.HasDefaultValue && parameter.DefaultValue != null &&
                    !(parameter.DefaultValue is DBNull))
                {
                    property.Set("default", FromClrValue(parameter.DefaultValue));
                }

                properties.Set(parameter.Name, property);

                bool isRequired = !parameter.HasDefaultValue || (meta != null && meta.Required);
                if (isRequired)
                {
                    required.Add(RevvyJson.String(parameter.Name));
                }
            }

            RevvyJson inputSchema = RevvyJson.Object()
                .Set("type", "object")
                .Set("properties", properties);
            if (required.Count > 0)
            {
                inputSchema.Set("required", required);
            }

            inputSchema.Set("additionalProperties", false);

            RevvyToolAttribute attribute = descriptor.Attribute;
            RevvyJson annotations = RevvyJson.Object();
            if (!string.IsNullOrEmpty(attribute.Title))
            {
                annotations.Set("title", attribute.Title);
            }

            annotations
                .Set("readOnlyHint", attribute.ReadOnly)
                .Set("destructiveHint", attribute.Destructive)
                .Set("idempotentHint", attribute.Idempotent)
                .Set("openWorldHint", attribute.OpenWorld);

            if (attribute.ReadOnlyActions != null && attribute.ReadOnlyActions.Length > 0)
            {
                annotations.Set("readOnlyActions", RevvyJson.ArrayOf(attribute.ReadOnlyActions));
            }

            return RevvyJson.Object()
                .Set("name", descriptor.Name)
                .Set("description", descriptor.Description)
                .Set("inputSchema", inputSchema)
                .Set("annotations", annotations);
        }

        private static RevvyJson DescribeType(Type type)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;

            if (effective == typeof(string))
            {
                return RevvyJson.Object().Set("type", "string");
            }

            if (effective == typeof(bool))
            {
                return RevvyJson.Object().Set("type", "boolean");
            }

            if (effective == typeof(int) || effective == typeof(long) ||
                effective == typeof(short) || effective == typeof(byte) ||
                effective == typeof(uint) || effective == typeof(ulong))
            {
                return RevvyJson.Object().Set("type", "integer");
            }

            if (effective == typeof(float) || effective == typeof(double) || effective == typeof(decimal))
            {
                return RevvyJson.Object().Set("type", "number");
            }

            if (effective.IsEnum)
            {
                return RevvyJson.Object()
                    .Set("type", "string")
                    .Set("enum", RevvyJson.ArrayOf(Enum.GetNames(effective)));
            }

            if (effective.IsArray)
            {
                return RevvyJson.Object()
                    .Set("type", "array")
                    .Set("items", DescribeType(effective.GetElementType()));
            }

            // RevvyJson (and anything else) is passed through as a free-form object.
            return RevvyJson.Object().Set("type", "object");
        }

        private static RevvyJson FromClrValue(object value)
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

            if (value is Enum)
            {
                return RevvyJson.String(value.ToString());
            }

            try
            {
                return RevvyJson.Number(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return RevvyJson.String(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }

        // ------------------------------------------------------------------ binding

        private static object[] BindArguments(RevvyToolDescriptor descriptor, RevvyJson arguments)
        {
            if (descriptor.Attribute.StrictSchema && arguments != null && !arguments.IsObject)
            {
                throw new RevvyToolException(
                    "Arguments for tool '" + descriptor.Name + "' must be a JSON object.",
                    "invalid_argument");
            }

            RevvyJson args = arguments != null && arguments.IsObject ? arguments : RevvyJson.Object();

            // additionalProperties:false is advertised in the schema; enforce it so typos
            // fail loudly instead of being silently dropped.
            HashSet<string> known = new HashSet<string>(
                descriptor.Parameters.Select(p => p.Name), StringComparer.Ordinal);
            foreach (string key in args.Keys)
            {
                if (!known.Contains(key))
                {
                    throw new RevvyToolException(
                        "Unknown argument '" + key + "' for tool '" + descriptor.Name + "'. Expected: " +
                        (known.Count > 0 ? string.Join(", ", known.ToArray()) : "(none)"),
                        "invalid_argument");
                }
            }

            object[] bound = new object[descriptor.Parameters.Length];
            for (int i = 0; i < descriptor.Parameters.Length; i++)
            {
                ParameterInfo parameter = descriptor.Parameters[i];
                RevvyJson value = args.Get(parameter.Name);

                if (value != null && descriptor.Attribute.StrictSchema)
                {
                    ValidateStrictArgument(descriptor, parameter, value);
                }

                if (value == null || value.IsNull)
                {
                    if (parameter.HasDefaultValue)
                    {
                        bound[i] = parameter.DefaultValue;
                        continue;
                    }

                    throw new RevvyToolException(
                        "Missing required argument '" + parameter.Name + "' for tool '" + descriptor.Name + "'.",
                        "invalid_argument");
                }

                bound[i] = CoerceValue(value, parameter.ParameterType, descriptor.Name, parameter.Name);
            }

            return bound;
        }

        private static void ValidateStrictArgument(
            RevvyToolDescriptor descriptor,
            ParameterInfo parameter,
            RevvyJson value)
        {
            RevvyJson inputSchema = descriptor.Schema.Get("inputSchema");
            RevvyJson properties = inputSchema != null ? inputSchema.Get("properties") : null;
            RevvyJson schema = properties != null ? properties.Get(parameter.Name) : null;
            RevvyJson typeNode = schema != null ? schema.Get("type") : null;
            string expected = typeNode != null ? typeNode.AsString(string.Empty) : string.Empty;
            bool typeMatches;
            switch (expected)
            {
                case "string":
                    typeMatches = value.Kind == RevvyJsonKind.String;
                    break;
                case "boolean":
                    typeMatches = value.Kind == RevvyJsonKind.Bool;
                    break;
                case "integer":
                    typeMatches = value.Kind == RevvyJsonKind.Number &&
                                  value.NumberValue == Math.Truncate(value.NumberValue);
                    break;
                case "number":
                    typeMatches = value.Kind == RevvyJsonKind.Number;
                    break;
                case "array":
                    typeMatches = value.IsArray;
                    break;
                case "object":
                    typeMatches = value.IsObject;
                    break;
                default:
                    typeMatches = true;
                    break;
            }

            if (!typeMatches)
            {
                throw new RevvyToolException(
                    "Argument '" + parameter.Name + "' of '" + descriptor.Name +
                    "' must be " + Article(expected) + expected + ".",
                    "invalid_argument");
            }

            if (value.Kind != RevvyJsonKind.Number || schema == null)
            {
                return;
            }

            RevvyJson minimum = schema.Get("minimum");
            RevvyJson maximum = schema.Get("maximum");
            if (minimum != null && value.NumberValue < minimum.NumberValue)
            {
                throw new RevvyToolException(
                    "Argument '" + parameter.Name + "' of '" + descriptor.Name +
                    "' must be at least " + minimum.AsString("0") + ".",
                    "invalid_argument");
            }

            if (maximum != null && value.NumberValue > maximum.NumberValue)
            {
                throw new RevvyToolException(
                    "Argument '" + parameter.Name + "' of '" + descriptor.Name +
                    "' must be at most " + maximum.AsString("0") + ".",
                    "invalid_argument");
            }
        }

        private static string Article(string value)
        {
            return !string.IsNullOrEmpty(value) && "aeiou".IndexOf(value[0]) >= 0 ? "an " : "a ";
        }

        /// <summary>
        /// JSON -&gt; CLR conversion for one argument. Internal so <c>execute_script</c> can
        /// reuse it when binding arguments for a reflected method call.
        /// </summary>
        internal static object CoerceValue(RevvyJson value, Type type, string toolName, string parameterName)
        {
            Type effective = Nullable.GetUnderlyingType(type) ?? type;

            if (effective == typeof(RevvyJson))
            {
                return value;
            }

            if (effective == typeof(string))
            {
                return value.AsString(string.Empty);
            }

            if (effective == typeof(bool))
            {
                return value.AsBool();
            }

            if (effective == typeof(int))
            {
                return value.AsInt();
            }

            if (effective == typeof(long))
            {
                return value.AsLong();
            }

            if (effective == typeof(float))
            {
                return (float)value.AsDouble();
            }

            if (effective == typeof(double))
            {
                return value.AsDouble();
            }

            if (effective.IsEnum)
            {
                string name = value.AsString(string.Empty);
                try
                {
                    return Enum.Parse(effective, name, true);
                }
                catch (Exception)
                {
                    throw new RevvyToolException(
                        "Argument '" + parameterName + "' of '" + toolName + "' must be one of: " +
                        string.Join(", ", Enum.GetNames(effective)));
                }
            }

            if (effective.IsArray)
            {
                Type element = effective.GetElementType();
                if (!value.IsArray)
                {
                    throw new RevvyToolException(
                        "Argument '" + parameterName + "' of '" + toolName + "' must be an array.");
                }

                Array array = Array.CreateInstance(element, value.Count);
                for (int i = 0; i < value.Count; i++)
                {
                    array.SetValue(CoerceValue(value[i], element, toolName, parameterName), i);
                }

                return array;
            }

            throw new RevvyToolException(
                "Unsupported parameter type " + effective.FullName + " on tool '" + toolName + "'.");
        }
    }
}
