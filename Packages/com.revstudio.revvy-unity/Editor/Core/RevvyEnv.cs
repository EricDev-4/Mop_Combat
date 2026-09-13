using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Environment / CLI-argument resolution and shared path layout.
    ///
    /// Contract §2 (state file location) and §8.2 ("all bridge config overridable
    /// via env/CLI args"). Explicit settings resolve CLI argument first and then the
    /// environment. The editor port additionally falls back to the project runtime
    /// config so a project-scoped proxy and a manually opened editor agree on a port.
    /// </summary>
    public static class RevvyEnv
    {
        /// <summary>Default editor bridge port. Contract §1 pins this to 8088.</summary>
        public const int DefaultEditorPort = 8088;

        /// <summary>Default proxy port, only written when the config file has no value yet.</summary>
        public const int DefaultProxyPort = 8089;

        public const string EngineId = "unity";

        public const string EnvEditorPort = "REVVY_EDITOR_PORT";
        public const string EnvStateDir = "REVVY_STATE_DIR";
        public const string EnvConfigPath = "REVVY_CONFIG";
        public const string EnvAutoStart = "REVVY_UNITY_AUTOSTART";
        public const string EnvAllowDynamicCompile = "REVVY_UNITY_ALLOW_DYNAMIC_COMPILE";

        private const string ArgEditorPort = "-revvyEditorPort";
        private const string ArgStateDir = "-revvyStateDir";
        private const string ArgConfigPath = "-revvyConfig";

        /// <summary>
        /// Bridge listen port: CLI -&gt; env -&gt; config <c>ue_port</c> -&gt; 8088.
        /// Invalid or unreadable candidates fall through to the next source.
        /// </summary>
        public static int ResolveEditorPort()
        {
            int port;
            if (TryParsePort(ReadCommandLineValue(ArgEditorPort), out port))
            {
                return port;
            }

            if (TryParsePort(Environment.GetEnvironmentVariable(EnvEditorPort), out port))
            {
                return port;
            }

            if (TryReadConfiguredEditorPort(out port))
            {
                return port;
            }

            return DefaultEditorPort;
        }

        private static bool TryReadConfiguredEditorPort(out int port)
        {
            port = 0;
            try
            {
                string path = ResolveConfigPath();
                if (!File.Exists(path))
                {
                    return false;
                }

                RevvyJson config = RevvyJson.Parse(File.ReadAllText(path));
                if (config == null || !config.IsObject)
                {
                    return false;
                }

                // A config path can be overridden and stale project files are often
                // copied between workspaces. Never bind a port merely because the JSON
                // is readable: the allocator's project-scope claim and editor identity
                // must both belong to this Unity project.
                if (!RuntimeConfigMatchesCurrentProject(config))
                {
                    return false;
                }

                RevvyJson configuredPort = config.Get("ue_port");
                if (configuredPort == null ||
                    configuredPort.Kind != RevvyJsonKind.Number ||
                    !configuredPort.IsIntegral ||
                    configuredPort.NumberValue < 1.0 ||
                    configuredPort.NumberValue > 65535.0)
                {
                    return false;
                }

                port = (int)configuredPort.NumberValue;
                return true;
            }
            catch (Exception)
            {
                // Port discovery must never prevent the editor bridge from starting.
                // The config writer reports path/write failures after a successful bind.
                port = 0;
                return false;
            }
        }

        internal static bool RuntimeConfigMatchesCurrentProject(RevvyJson config)
        {
            if (!string.Equals(
                    ReadString(config, "port_scope"),
                    "project",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return RuntimeConfigIdentityMatchesCurrentProject(config);
        }

        /// <summary>
        /// Checks only engine/project ownership. Writers call this before replacing
        /// identity fields so a copied runtime file cannot turn a foreign port pair
        /// into a current-project allocation merely by being republished.
        /// </summary>
        internal static bool RuntimeConfigIdentityMatchesCurrentProject(RevvyJson config)
        {
            if (!string.Equals(
                    ReadString(config, "engine"),
                    EngineId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string currentProject = CurrentProjectPath();
            if (string.IsNullOrEmpty(currentProject))
            {
                return false;
            }

            if (IsSameProjectPath(ReadString(config, "project_path"), currentProject))
            {
                return true;
            }

            return IsSameProjectPath(ReadProjectObjectPath(config, "installed_project"), currentProject) ||
                   IsSameProjectPath(ReadProjectObjectPath(config, "active_project"), currentProject);
        }

        internal static bool RuntimeProjectObjectMatchesCurrentProject(
            RevvyJson config,
            string field)
        {
            string currentProject = CurrentProjectPath();
            return !string.IsNullOrEmpty(currentProject) &&
                   IsSameProjectPath(ReadProjectObjectPath(config, field), currentProject);
        }

        private static string ReadProjectObjectPath(RevvyJson config, string field)
        {
            RevvyJson project = config.Get(field);
            return project != null && project.IsObject ? ReadString(project, "path") : null;
        }

        private static string ReadString(RevvyJson value, string field)
        {
            RevvyJson node = value != null ? value.Get(field) : null;
            return node != null && node.Kind == RevvyJsonKind.String ? node.AsString() : null;
        }

        private static string CurrentProjectPath()
        {
            string cached = RevvyEditorFacts.CachedProjectPath;
            if (!string.IsNullOrEmpty(cached))
            {
                return CanonicalizeProjectPath(cached);
            }

            try
            {
                DirectoryInfo parent = Directory.GetParent(Application.dataPath);
                return CanonicalizeProjectPath(parent != null ? parent.FullName : Application.dataPath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static string CanonicalizeProjectPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                string canonical = Path.GetFullPath(ExpandUser(path.Trim()))
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                string root = Path.GetPathRoot(canonical) ?? string.Empty;
                while (canonical.Length > root.Length &&
                       (canonical.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                        canonical.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)))
                {
                    canonical = canonical.Substring(0, canonical.Length - 1);
                }

                return canonical;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsSameProjectPath(string candidate, string currentProject)
        {
            string canonicalCandidate = CanonicalizeProjectPath(candidate);
            string canonicalCurrent = CanonicalizeProjectPath(currentProject);
            if (string.IsNullOrEmpty(canonicalCandidate) || string.IsNullOrEmpty(canonicalCurrent))
            {
                return false;
            }

            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(canonicalCandidate, canonicalCurrent, comparison);
        }

        private static bool TryParsePort(string raw, out int port)
        {
            port = 0;
            return !string.IsNullOrEmpty(raw) &&
                   int.TryParse(
                       raw,
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out port) &&
                   port > 0 && port <= 65535;
        }

        /// <summary>
        /// Project-local state directory. An explicit CLI or environment override wins;
        /// otherwise runtime files live under <c>&lt;project&gt;/.revvy</c>.
        /// </summary>
        public static string ResolveStateDirectory()
        {
            string configured = ResolveSetting(ArgStateDir, EnvStateDir);
            if (!string.IsNullOrEmpty(configured))
            {
                return Path.GetFullPath(ExpandUser(configured));
            }

            string dataPath = Application.dataPath;
            DirectoryInfo projectDirectory = Directory.GetParent(dataPath);
            if (projectDirectory != null)
            {
                return Path.Combine(projectDirectory.FullName, ".revvy");
            }

            return Path.Combine(dataPath, ".revvy");
        }

        /// <summary>Runtime config file path (contract §2): REVVY_CONFIG -&gt; &lt;state dir&gt;/revvy-proxy.json.</summary>
        public static string ResolveConfigPath()
        {
            string configured = ResolveSetting(ArgConfigPath, EnvConfigPath);
            if (!string.IsNullOrEmpty(configured))
            {
                return Path.GetFullPath(ExpandUser(configured));
            }

            return Path.Combine(ResolveStateDirectory(), "revvy-proxy.json");
        }

        /// <summary>
        /// Tool manifest the proxy serves <c>tools/list</c> from. The UE server writes
        /// this on startup; the Unity bridge must match for proxy parity
        /// (see Tests/Python/test_bridge_contract.py).
        /// </summary>
        public static string ResolveToolManifestPath()
        {
            return Path.Combine(ResolveStateDirectory(), "tools-manifest.json");
        }

        /// <summary>
        /// Autostart defaults to on. Set REVVY_UNITY_AUTOSTART=0 to keep the listener
        /// closed until the user presses Start in the Revvy/Status window.
        /// </summary>
        public static bool AutoStartEnabled()
        {
            return !IsFalsey(Environment.GetEnvironmentVariable(EnvAutoStart));
        }

        /// <summary>Feature flag for execute_script's (unimplemented) Roslyn path.</summary>
        public static bool DynamicCompileEnabled()
        {
            return IsTruthy(Environment.GetEnvironmentVariable(EnvAllowDynamicCompile));
        }

        public static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string normalized = value.Trim();
            return normalized == "1"
                || string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsFalsey(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            string normalized = value.Trim();
            return normalized == "0"
                || string.Equals(normalized, "false", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "no", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, "off", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads <c>-flag value</c> from the editor command line, else the env var.</summary>
        private static string ResolveSetting(string cliFlag, string environmentVariable)
        {
            string fromCli = ReadCommandLineValue(cliFlag);
            if (!string.IsNullOrEmpty(fromCli))
            {
                return fromCli;
            }

            return Environment.GetEnvironmentVariable(environmentVariable);
        }

        private static string ReadCommandLineValue(string flag)
        {
            string[] args;
            try
            {
                args = Environment.GetCommandLineArgs();
            }
            catch (Exception)
            {
                return null;
            }

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        private static string ExpandUser(string path)
        {
            if (string.IsNullOrEmpty(path) || path[0] != '~')
            {
                return path;
            }

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (path.Length == 1)
            {
                return home;
            }

            if (path[1] == '/' || path[1] == '\\')
            {
                return Path.Combine(home, path.Substring(2));
            }

            return path;
        }
    }
}
