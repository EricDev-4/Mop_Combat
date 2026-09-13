using System;
using UnityEditor;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Bridge lifecycle owner.
    ///
    /// Runs on every domain load (<c>[InitializeOnLoad]</c>): installs the log capture,
    /// refreshes cached editor facts, starts the loopback MCP server, and writes the
    /// runtime config the proxy discovers us through (contract §2).
    ///
    /// Domain reload is the tricky part of an in-editor server: Unity tears down and
    /// rebuilds the scripting domain on every recompile, but background threads and
    /// sockets are not automatically reclaimed. The listener is therefore stopped
    /// explicitly in <c>AssemblyReloadEvents.beforeAssemblyReload</c> and restarted by
    /// the next static constructor.
    /// </summary>
    [InitializeOnLoad]
    public static class RevvyBridge
    {
        private static bool _shutdownHooked;

        static RevvyBridge()
        {
            // Deferred to the first editor tick: during the static constructor the
            // AssetDatabase and other subsystems are not guaranteed to be ready.
            EditorApplication.delayCall += Bootstrap;
        }

        /// <summary>True when the loopback listener is accepting connections.</summary>
        public static bool IsRunning
        {
            get
            {
                RevvyEditorServer server = RevvyEditorServer.Instance;
                return server != null && server.IsRunning;
            }
        }

        public static int Port
        {
            get
            {
                RevvyEditorServer server = RevvyEditorServer.Instance;
                return server != null ? server.Port : RevvyEnv.ResolveEditorPort();
            }
        }

        public static string LastError
        {
            get
            {
                RevvyEditorServer server = RevvyEditorServer.Instance;
                return server != null ? server.LastError : null;
            }
        }

        private static void Bootstrap()
        {
            HookShutdown();
            RevvyLogBuffer.Install();
            RevvyEditorFacts.Refresh();

            // The tool set can change across a recompile; force a rescan.
            RevvyToolRegistry.Invalidate();

            if (!RevvyEnv.AutoStartEnabled())
            {
                RevvyLog.Info("Autostart disabled (" + RevvyEnv.EnvAutoStart +
                              "); use Revvy/Status to start the bridge manually.");
                return;
            }

            Start();
        }

        /// <summary>Starts the listener and publishes the runtime config. Safe to call repeatedly.</summary>
        public static bool Start()
        {
            HookShutdown();
            RevvyLogBuffer.Install();
            RevvyEditorFacts.Refresh();

            int port = RevvyEnv.ResolveEditorPort();
            RevvyEditorServer server = RevvyEditorServer.StartShared(port);
            if (server == null)
            {
                return false;
            }

            // Written after a successful bind so the proxy never sees a port we do not hold.
            RevvyProxyConfigWriter.WriteStartupConfig(port);
            return true;
        }

        public static void Stop()
        {
            RevvyEditorServer.StopShared();
            RevvyProxyConfigWriter.MarkBridgeStopped();
        }

        public static bool Restart()
        {
            Stop();
            return Start();
        }

        /// <summary>Rewrites revvy-proxy.json + tools-manifest.json without touching the listener.</summary>
        public static bool RepublishConfig()
        {
            return RevvyProxyConfigWriter.WriteStartupConfig(Port);
        }

        private static void HookShutdown()
        {
            if (_shutdownHooked)
            {
                return;
            }

            _shutdownHooked = true;

            // Order matters: the socket must be released before the domain is torn down,
            // otherwise the port stays bound by an orphaned thread until the editor exits.
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
        }

        private static void OnBeforeAssemblyReload()
        {
            RevvyLog.Verbose("Domain reload — releasing the MCP listener.");
            RevvyEditorServer.StopShared();
            RevvyToolRegistry.Invalidate();
        }

        private static void OnEditorQuitting()
        {
            Stop();
            RevvyLogBuffer.Uninstall();
        }
    }

    /// <summary>
    /// Entry points for headless / CI runs (contract §8.2, §7):
    /// <c>Unity -batchmode -nographics -projectPath &lt;p&gt; -executeMethod
    /// RevStudio.Revvy.Editor.RevvyBridgeCli.StartFromCommandLine</c>.
    ///
    /// Port, state directory, and config path all come from env vars or
    /// <c>-revvyEditorPort</c> / <c>-revvyStateDir</c> / <c>-revvyConfig</c>.
    /// </summary>
    public static class RevvyBridgeCli
    {
        /// <summary>Starts the bridge and keeps the editor process alive (do NOT pass -quit).</summary>
        public static void StartFromCommandLine()
        {
            if (RevvyBridge.Start())
            {
                Debug.Log(RevvyLog.Prefix + "Headless bridge started on port " + RevvyBridge.Port + ".");
                return;
            }

            Debug.LogError(RevvyLog.Prefix + "Headless bridge failed to start: " +
                           (RevvyBridge.LastError ?? "unknown error"));
            EditorApplication.Exit(1);
        }

        /// <summary>
        /// Writes revvy-proxy.json + tools-manifest.json and exits. Useful in CI to
        /// validate schema generation without holding a socket open. Pair with
        /// <c>-quit</c> only if you drop the explicit Exit below.
        /// </summary>
        public static void PublishConfigAndExit()
        {
            RevvyEditorFacts.Refresh();
            bool ok = RevvyProxyConfigWriter.WriteStartupConfig(RevvyEnv.ResolveEditorPort());
            Debug.Log(RevvyLog.Prefix + "Config publish " + (ok ? "succeeded" : "failed") +
                      " (" + RevvyToolRegistry.Count + " tools).");
            EditorApplication.Exit(ok ? 0 : 1);
        }
    }
}
