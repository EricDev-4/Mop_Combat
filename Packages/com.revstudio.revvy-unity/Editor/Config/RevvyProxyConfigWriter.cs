using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Startup writer for the shared runtime config (contract §2).
    ///
    /// Two files land in the state directory:
    /// <list type="bullet">
    /// <item><c>revvy-proxy.json</c> — engine identity + ports the proxy reads to find us.</item>
    /// <item><c>tools-manifest.json</c> — the tool descriptor array the proxy serves
    /// <c>tools/list</c> from (the UE server writes this on startup; the Unity bridge
    /// must match or the proxy reports an empty tool set).</item>
    /// </list>
    ///
    /// Both writes are read-modify-write and atomic. Unknown fields are preserved,
    /// while known project-owned fields are retained only when the pre-write identity
    /// belongs to this project; a crash mid-write can never leave a torn file.
    /// </summary>
    public static class RevvyProxyConfigWriter
    {
        public const int ConfigVersion = 3;

        private static readonly object Gate = new object();
        private static readonly string[] ForeignProjectOwnedFields =
        {
            "port_scope",
            "proxy_port",
            "installed_project",
            "active_project",
            "bearer_token",
            "bearer_scope",
            "backend_auth",
            "full_access_token",
            "bridge_instance_id",
            "bridge_running",
        };

        /// <summary>Last write outcome, surfaced in the status window.</summary>
        public static string LastStatus { get; private set; }

        public static string LastWrittenPath { get; private set; }

        /// <summary>
        /// Writes both runtime files. Call on the main thread (it reads
        /// <c>Application.*</c> via <see cref="RevvyEditorFacts"/>).
        /// </summary>
        /// <returns>True when both files were written.</returns>
        public static bool WriteStartupConfig(int editorPort)
        {
            RevvyEditorFacts.Refresh();

            string configPath = RevvyEnv.ResolveConfigPath();
            string manifestPath = RevvyEnv.ResolveToolManifestPath();

            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(configPath));

                    RevvyJson config = ReadExisting(configPath);
                    SanitizeExistingProjectOwnership(config);
                    ApplyBridgeFields(config, editorPort);
                    WriteAtomic(configPath, config.ToJson(true));

                    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
                    WriteAtomic(manifestPath, RevvyToolRegistry.BuildToolListJson().ToJson(true));

                    LastWrittenPath = configPath;
                    LastStatus = "written " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    RevvyLog.Info("Runtime config written to " + configPath);
                    return true;
                }
                catch (Exception exception)
                {
                    LastStatus = "failed — " + RevvyLog.Describe(exception);
                    RevvyLog.Error("Failed to write runtime config at " + configPath + ": " +
                                   RevvyLog.Describe(exception));
                    return false;
                }
            }
        }

        /// <summary>
        /// Clears the engine claim on shutdown so a stale <c>engine: "unity"</c> does not
        /// mislead the proxy after the editor closes. Ports and unknown fields stay put.
        /// </summary>
        public static void MarkBridgeStopped()
        {
            string configPath = RevvyEnv.ResolveConfigPath();
            lock (Gate)
            {
                try
                {
                    if (!File.Exists(configPath))
                    {
                        return;
                    }

                    RevvyJson config = ReadExisting(configPath);
                    config.Set("bridge_running", false);
                    config.Set("updated_at", Timestamp());
                    WriteAtomic(configPath, config.ToJson(true));
                }
                catch (Exception exception)
                {
                    RevvyLog.Verbose("Shutdown config update skipped: " + RevvyLog.Describe(exception));
                }
            }
        }

        // ------------------------------------------------------------------ internals

        private static RevvyJson ReadExisting(string path)
        {
            if (!File.Exists(path))
            {
                return RevvyJson.Object();
            }

            try
            {
                string text = File.ReadAllText(path, Encoding.UTF8);
                RevvyJson parsed = RevvyJson.Parse(text);
                if (parsed != null && parsed.IsObject)
                {
                    return parsed;
                }

                RevvyLog.Warn("Existing " + Path.GetFileName(path) +
                              " is not a JSON object; it will be replaced.");
            }
            catch (Exception exception)
            {
                RevvyLog.Warn("Could not read " + path + " (" + RevvyLog.Describe(exception) +
                              "); writing a fresh file.");
            }

            return RevvyJson.Object();
        }

        /// <summary>
        /// Snapshot ownership before <see cref="ApplyBridgeFields"/> replaces engine
        /// and project identity. A copied/stale file must not retain its durable port
        /// claim, proxy route, installer owner, or credentials after this bridge
        /// republishes it as the current project. Unknown, non-owned fields remain.
        /// </summary>
        private static void SanitizeExistingProjectOwnership(RevvyJson config)
        {
            bool currentProjectOwned = RevvyEnv.RuntimeConfigIdentityMatchesCurrentProject(config);
            if (!currentProjectOwned)
            {
                foreach (string field in ForeignProjectOwnedFields)
                {
                    config.Remove(field);
                }

                return;
            }

            // Identity can match through project_path or active_project while a stale
            // installer record still names another workspace. Keep installer-owned
            // metadata only when that record independently belongs to this project.
            if (config.Has("installed_project") &&
                !RevvyEnv.RuntimeProjectObjectMatchesCurrentProject(config, "installed_project"))
            {
                config.Remove("installed_project");
            }
        }

        /// <summary>
        /// Sets only the fields the bridge owns. Everything else in the document —
        /// including keys this version has never heard of — is left untouched
        /// (contract §2: "bridges must preserve unknown fields").
        /// </summary>
        private static void ApplyBridgeFields(RevvyJson config, int editorPort)
        {
            config.Set("config_version", ConfigVersion);
            config.Set("engine", RevvyEnv.EngineId);
            config.Set("engine_version", RevvyEditorFacts.CachedUnityVersion);
            config.Set("bridge_instance_id", RevvyEditorFacts.BridgeInstanceId);
            config.Set("project_name", RevvyEditorFacts.CachedProjectName);
            config.Set("project_path", RevvyEditorFacts.CachedProjectPath);

            RevvyJson activeProject = config.Get("active_project");
            if (activeProject == null || !activeProject.IsObject)
            {
                activeProject = RevvyJson.Object();
                config.Set("active_project", activeProject);
            }

            activeProject.Set("name", RevvyEditorFacts.CachedProjectName);
            activeProject.Set("path", RevvyEditorFacts.CachedProjectPath);

            // ue_port keeps its legacy name; it is the editor bridge port for any engine.
            config.Set("ue_port", editorPort);

            // The proxy owns proxy_port and bearer_token. Seed defaults only when absent
            // so a running proxy's values are never stomped.
            RevvyJson proxyPort = config.Get("proxy_port");
            if (proxyPort == null ||
                proxyPort.Kind != RevvyJsonKind.Number ||
                !proxyPort.IsIntegral ||
                proxyPort.NumberValue < 1.0 ||
                proxyPort.NumberValue > 65535.0)
            {
                config.Set("proxy_port", RevvyEnv.DefaultProxyPort);
            }

            if (!config.Has("bearer_token"))
            {
                config.Set("bearer_token", string.Empty);
            }

            if (!config.Has("bearer_scope"))
            {
                config.Set("bearer_scope", "read_only");
            }

            config.Set("bridge_running", true);
            config.Set("updated_at", Timestamp());
        }

        private static string Timestamp()
        {
            return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Write-to-temp then swap. <see cref="File.Replace(string,string,string)"/> is the
        /// atomic path when the destination exists; a plain move covers first creation.
        /// A retry loop absorbs the brief sharing violations a concurrently-reading proxy
        /// can cause on Windows.
        /// </summary>
        private static void WriteAtomic(string path, string contents)
        {
            string directory = Path.GetDirectoryName(path);
            string temporary = Path.Combine(
                directory,
                Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");

            // UTF-8 without BOM: Python's json.load rejects a BOM-prefixed document.
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));

            const int Attempts = 5;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temporary, path, null, true);
                    }
                    else
                    {
                        File.Move(temporary, path);
                    }

                    return;
                }
                catch (IOException) when (attempt < Attempts)
                {
                    Thread.Sleep(40 * attempt);
                }
                catch (UnauthorizedAccessException) when (attempt < Attempts)
                {
                    Thread.Sleep(40 * attempt);
                }
            }

            // Unreachable: the final attempt has no exception filter, so its failure
            // propagates out of the loop with the original exception intact.
        }
    }
}
