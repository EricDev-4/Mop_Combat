using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Main-thread-only Unity facts, cached so socket threads can read them.
    ///
    /// <see cref="Application.unityVersion"/> and friends are main-thread APIs
    /// (contract §8.2). The startup writer, the <c>/health</c> probe, and the status
    /// window all need them off the main thread, so they are snapshotted once during
    /// <c>[InitializeOnLoad]</c> and refreshed whenever the project changes.
    /// </summary>
    public static class RevvyEditorFacts
    {
        private static readonly object Gate = new object();
        private static readonly string BridgeInstanceIdValue = Guid.NewGuid().ToString("N");
        private static string _unityVersion = "unknown";
        private static string _projectName = "UnityProject";
        private static string _projectPath = string.Empty;
        private static string _dataPath = string.Empty;

        /// <summary>Fresh identity for this Unity scripting-domain bridge session.</summary>
        public static string BridgeInstanceId
        {
            get { return BridgeInstanceIdValue; }
        }

        /// <summary>Engine version string (contract §2 <c>engine_version</c>), e.g. <c>6000.3.21f1</c>.</summary>
        public static string CachedUnityVersion
        {
            get
            {
                lock (Gate)
                {
                    return _unityVersion;
                }
            }
        }

        public static string CachedProjectName
        {
            get
            {
                lock (Gate)
                {
                    return _projectName;
                }
            }
        }

        /// <summary>Absolute project root (the folder containing <c>Assets/</c>).</summary>
        public static string CachedProjectPath
        {
            get
            {
                lock (Gate)
                {
                    return _projectPath;
                }
            }
        }

        /// <summary>Absolute path of the project's <c>Assets/</c> folder.</summary>
        public static string CachedDataPath
        {
            get
            {
                lock (Gate)
                {
                    return _dataPath;
                }
            }
        }

        /// <summary>Re-snapshots the cache. MUST be called on the main thread.</summary>
        public static void Refresh()
        {
            string version = Application.unityVersion;
            string dataPath = Application.dataPath;
            string productName = Application.productName;

            string root = dataPath;
            try
            {
                DirectoryInfo parent = Directory.GetParent(dataPath);
                if (parent != null)
                {
                    root = parent.FullName;
                }

                root = RevvyEnv.CanonicalizeProjectPath(root) ?? root;
                dataPath = Path.GetFullPath(dataPath);
            }
            catch (Exception)
            {
                // Fall back to dataPath; better a slightly wrong path than a failed startup.
            }

            string name = productName;
            if (string.IsNullOrEmpty(name))
            {
                name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            lock (Gate)
            {
                _unityVersion = string.IsNullOrEmpty(version) ? "unknown" : version;
                _projectName = string.IsNullOrEmpty(name) ? "UnityProject" : name;
                _projectPath = root;
                _dataPath = dataPath;
            }
        }

        /// <summary>Play-mode state as a stable enum string for tool payloads.</summary>
        public static string DescribePlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                return EditorApplication.isPaused ? "paused" : "playing";
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "entering_play_mode";
            }

            return "edit_mode";
        }
    }
}
