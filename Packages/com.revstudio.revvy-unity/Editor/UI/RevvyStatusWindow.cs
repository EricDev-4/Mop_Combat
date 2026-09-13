using System;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Minimal status panel (menu <c>Revvy/Status</c>): is the bridge running, on which
    /// port, and against which project.
    ///
    /// IMGUI rather than UI Toolkit — no UXML/USS assets means no .meta GUID churn in a
    /// git-URL package (see META-POLICY.md).
    /// </summary>
    public sealed class RevvyStatusWindow : EditorWindow
    {
        private const double RepaintIntervalSeconds = 1.0;

        private Vector2 _scroll;
        private double _nextRepaint;

        [MenuItem("Revvy/Status", false, 0)]
        public static void Open()
        {
            RevvyStatusWindow window = GetWindow<RevvyStatusWindow>(false, "Revvy", true);
            window.minSize = new Vector2(360f, 280f);
            window.Show();
        }

        private void OnEnable()
        {
            EditorApplication.update += TickRepaint;
        }

        private void OnDisable()
        {
            EditorApplication.update -= TickRepaint;
        }

        /// <summary>The panel shows live counters, so repaint on a timer rather than on input only.</summary>
        private void TickRepaint()
        {
            if (EditorApplication.timeSinceStartup < _nextRepaint)
            {
                return;
            }

            _nextRepaint = EditorApplication.timeSinceStartup + RepaintIntervalSeconds;
            Repaint();
        }

        private void OnGUI()
        {
            RevvyEditorServer server = RevvyEditorServer.Instance;
            bool running = server != null && server.IsRunning;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(4f);
            DrawStatusBanner(running, server);
            EditorGUILayout.Space(8f);

            EditorGUILayout.LabelField("Server", EditorStyles.boldLabel);
            Row("State", running ? "Running" : "Stopped");
            Row("Endpoint", "http://127.0.0.1:" + RevvyBridge.Port + RevvyEditorServer.EndpointPath);
            Row("Port", RevvyBridge.Port.ToString(CultureInfo.InvariantCulture) +
                        (RevvyEnv.ResolveEditorPort() == RevvyEnv.DefaultEditorPort
                            ? " (default)"
                            : " (" + RevvyEnv.EnvEditorPort + " override)"));
            Row("Requests", server != null
                ? server.RequestCount.ToString(CultureInfo.InvariantCulture)
                : "0");
            Row("Sessions", RevvyMcpHandler.SessionCount.ToString(CultureInfo.InvariantCulture));
            Row("Protocol", RevvyMcpHandler.SupportedProtocolVersions[0]);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Project", EditorStyles.boldLabel);
            Row("Name", RevvyEditorFacts.CachedProjectName);
            Row("Path", RevvyEditorFacts.CachedProjectPath);
            Row("Unity", RevvyEditorFacts.CachedUnityVersion);
            Row("Play mode", RevvyEditorFacts.DescribePlayMode());

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Bridge", EditorStyles.boldLabel);
            Row("Tools", RevvyToolRegistry.Count.ToString(CultureInfo.InvariantCulture));
            Row("Main-thread queue", RevvyMainThread.PendingCount.ToString(CultureInfo.InvariantCulture) +
                                     " pending / " +
                                     RevvyMainThread.CompletedJobs.ToString(CultureInfo.InvariantCulture) +
                                     " done");
            Row("Log buffer", RevvyLogBuffer.TotalCaptured.ToString(CultureInfo.InvariantCulture) +
                              " captured, " +
                              RevvyLogBuffer.DroppedCount.ToString(CultureInfo.InvariantCulture) + " overwritten");
            Row("Config", RevvyProxyConfigWriter.LastStatus ?? "not written yet");
            Row("Config path", RevvyEnv.ResolveConfigPath());

            EditorGUILayout.Space(12f);
            DrawButtons(running);

            EditorGUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                "One editor bridge may hold port 8088 at a time. If the bridge cannot bind, close the other " +
                "editor (or Unreal instance) using the port, or set " + RevvyEnv.EnvEditorPort +
                " and update revvy-proxy.json's ue_port to match.",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private static void DrawStatusBanner(bool running, RevvyEditorServer server)
        {
            if (running)
            {
                EditorGUILayout.HelpBox(
                    "Bridge is running — " + server.DescribeState(), MessageType.Info);
                return;
            }

            string error = server != null ? server.LastError : null;
            EditorGUILayout.HelpBox(
                string.IsNullOrEmpty(error)
                    ? "Bridge is stopped."
                    : "Bridge is stopped.\n" + error,
                string.IsNullOrEmpty(error) ? MessageType.Warning : MessageType.Error);
        }

        private void DrawButtons(bool running)
        {
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(running))
            {
                if (GUILayout.Button("Start", GUILayout.Height(24f)))
                {
                    RevvyBridge.Start();
                }
            }

            using (new EditorGUI.DisabledScope(!running))
            {
                if (GUILayout.Button("Stop", GUILayout.Height(24f)))
                {
                    RevvyBridge.Stop();
                }
            }

            if (GUILayout.Button("Restart", GUILayout.Height(24f)))
            {
                RevvyBridge.Restart();
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Rewrite config"))
            {
                RevvyBridge.RepublishConfig();
            }

            if (GUILayout.Button("Reveal config"))
            {
                RevealConfig();
            }

            EditorGUILayout.EndHorizontal();
        }

        private static void RevealConfig()
        {
            string path = RevvyEnv.ResolveConfigPath();
            try
            {
                if (File.Exists(path))
                {
                    EditorUtility.RevealInFinder(path);
                    return;
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    EditorUtility.RevealInFinder(directory);
                    return;
                }

                RevvyLog.Warn("Config path does not exist yet: " + path);
            }
            catch (Exception exception)
            {
                RevvyLog.Exception("Could not reveal " + path, exception);
            }
        }

        private static void Row(string label, string value)
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(label, GUILayout.Width(130f));
            EditorGUILayout.SelectableLabel(
                value ?? string.Empty,
                EditorStyles.label,
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUILayout.EndHorizontal();
        }
    }
}
