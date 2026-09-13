using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// A driver that can deliver simulated input to the running game
    /// (docs/design/tier7-simulated-input-contract.md §10).
    ///
    /// Implementations live in *optional* assemblies, so the bridge package can stay
    /// dependency-free: Unity can only simulate keyboard and mouse through
    /// <c>com.unity.inputsystem</c>, which is not a built-in module and which many
    /// projects deliberately do not have (contract §1).
    /// </summary>
    public interface IRevvyInputBackend
    {
        /// <summary>Stable identifier, e.g. <c>unity-input-system</c>. Callers may branch on this.</summary>
        string Name { get; }

        /// <summary>Human-readable detail. Callers must NOT branch on this.</summary>
        string Detail { get; }

        /// <summary>Device class names currently available for simulation.</summary>
        IReadOnlyList<string> Devices { get; }

        /// <summary>Game-view size in pixels, or 0×0 when there is no game view yet.</summary>
        void GetScreenSize(out int width, out int height);

        /// <param name="key">A neutral key name from the contract §9 vocabulary.</param>
        void SetKey(string key, bool pressed);

        /// <param name="button">One of <c>left</c>, <c>right</c>, <c>middle</c>.</param>
        void SetMouseButton(string button, bool pressed);

        /// <summary>Coordinates are contract-space: pixels, origin top-left, y increasing downward.</summary>
        void MoveMouse(int x, int y);
    }

    /// <summary>
    /// Locates the optional input backend.
    ///
    /// Discovery mirrors <see cref="RevvyToolRegistry"/>: scan loaded assemblies for an
    /// implementation. The main assembly cannot reference the optional one — that is the
    /// whole point of the optional assembly — so reflection is the only link.
    /// </summary>
    public static class RevvyInputBackends
    {
        /// <summary>Contract §13: <c>none</c> disables simulation entirely.</summary>
        public const string EnvBackend = "REVVY_INPUT_BACKEND";

        private static readonly object Gate = new object();
        private static IRevvyInputBackend _cached;
        private static bool _scanned;

        /// <summary>Drops the cache so the next call rescans. Used after a domain reload.</summary>
        public static void Invalidate()
        {
            lock (Gate)
            {
                _cached = null;
                _scanned = false;
            }
        }

        /// <summary>True when the operator disabled simulation (contract §13).</summary>
        public static bool DisabledByOperator()
        {
            string configured = Environment.GetEnvironmentVariable(EnvBackend);
            return !string.IsNullOrEmpty(configured) &&
                   string.Equals(configured.Trim(), "none", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The active backend, or null when input cannot be simulated here.</summary>
        public static IRevvyInputBackend Resolve()
        {
            if (DisabledByOperator())
            {
                return null;
            }

            lock (Gate)
            {
                if (_scanned)
                {
                    return _cached;
                }

                _scanned = true;
                _cached = Scan();
                return _cached;
            }
        }

        /// <summary>Why no backend was found, for the error payload (contract §8).</summary>
        public static string DescribeAbsence()
        {
            if (DisabledByOperator())
            {
                return "disabled by " + EnvBackend + "=none";
            }

            return "com.unity.inputsystem not present or Active Input Handling excludes it";
        }

        private static IRevvyInputBackend Scan()
        {
            Type contract = typeof(IRevvyInputBackend);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

                string name = assembly.GetName().Name ?? string.Empty;
                if (!name.StartsWith("RevStudio.Revvy", StringComparison.Ordinal))
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
                    types = partial.Types.Where(t => t != null).ToArray();
                }
                catch (Exception exception)
                {
                    RevvyLog.Verbose("Input backend scan skipped " + name + ": " + RevvyLog.Describe(exception));
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type.IsAbstract || type.IsInterface || !contract.IsAssignableFrom(type))
                    {
                        continue;
                    }

                    try
                    {
                        IRevvyInputBackend backend = (IRevvyInputBackend)Activator.CreateInstance(type);
                        RevvyLog.Verbose("Input backend resolved: " + backend.Name);
                        return backend;
                    }
                    catch (Exception exception)
                    {
                        RevvyLog.Warn(
                            "Input backend " + type.FullName + " could not be created: " +
                            RevvyLog.Describe(exception));
                    }
                }
            }

            return null;
        }
    }
}
