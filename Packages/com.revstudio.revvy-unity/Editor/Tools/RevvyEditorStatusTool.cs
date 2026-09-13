#if UNITY_6000_6_OR_NEWER
using RevvyObjectId = System.UInt64;
#else
using RevvyObjectId = System.Int32;
#endif

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevStudio.Revvy.Editor
{
    // Preserve the complete EntityId in session-scoped opaque node IDs.
    internal static class RevvyObjectIdentity
    {
        internal static RevvyObjectId GetId(UnityEngine.Object target)
        {
#if UNITY_6000_6_OR_NEWER
            return EntityId.ToULong(target.GetEntityId());
#else
            return target.GetInstanceID();
#endif
        }
        internal static UnityEngine.Object Resolve(RevvyObjectId id)
        {
#if UNITY_6000_6_OR_NEWER
            return Resources.EntityIdToObject(EntityId.FromULong(id));
#else
            return Resources.EntityIdToObject((EntityId)id);
#endif
        }
    }

    /// <summary>
    /// Tier-1 tool #4 (contract §4): engine version, project identity, play-mode state,
    /// dirty-asset state.
    /// </summary>
    [InitializeOnLoad]
    public static class RevvyEditorStatusTool
    {
        private const double PlayTransitionTimeoutSeconds = 30d;
        private const string PlayTransitionPendingKey = "RevStudio.Revvy.PlayTransition.Pending";
        private const string PlayTransitionTargetKey = "RevStudio.Revvy.PlayTransition.Target";
        private const string PlayTransitionDeadlineKey = "RevStudio.Revvy.PlayTransition.DeadlineUtcTicks";
        private const string PlayTransitionErrorKey = "RevStudio.Revvy.PlayTransition.Error";
        private static bool? _requestedPlaying;
        private static long _playTransitionDeadlineUtcTicks;
        private static int _playTransitionSequence;
        private static string _playTransitionError;
        private static bool _playModeStateHooked;

        static RevvyEditorStatusTool()
        {
            EnsurePlayModeStateHooked();
            EditorApplication.delayCall += RefreshPlayTransition;
        }

        [RevvyTool(
            "editor_status",
            "Report Unity editor state: engine version, project name/path, play-mode state, " +
            "compilation state, open scenes and whether they have unsaved changes, and bridge health. " +
            "Call this first to confirm which project the bridge is attached to.",
            Title = "Editor status",
            ReadOnly = true,
            Idempotent = true)]
        public static RevvyJson EditorStatus()
        {
            RevvyEditorFacts.Refresh();

            RevvyJson result = RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("engine_version", Application.unityVersion)
                .Set("project_name", RevvyEditorFacts.CachedProjectName)
                .Set("project_path", RevvyEditorFacts.CachedProjectPath)
                .Set("data_path", RevvyEditorFacts.CachedDataPath)
                .Set("play_mode", RevvyEditorFacts.DescribePlayMode())
                .Set("is_playing", EditorApplication.isPlaying)
                .Set("is_paused", EditorApplication.isPaused)
                .Set("is_compiling", EditorApplication.isCompiling)
                .Set("is_updating_assets", EditorApplication.isUpdating)
                .Set("platform", EditorUserBuildSettings.activeBuildTarget.ToString());

            RevvyJson scenes = RevvyJson.Array();
            bool anyDirty = false;
            int sceneCount = SceneManager.sceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid())
                {
                    continue;
                }

                anyDirty = anyDirty || scene.isDirty;
                scenes.Add(RevvyJson.Object()
                    .Set("name", scene.name ?? string.Empty)
                    .Set("path", scene.path ?? string.Empty)
                    .Set("loaded", scene.isLoaded)
                    .Set("dirty", scene.isDirty)
                    .Set("root_count", scene.isLoaded ? scene.rootCount : 0));
            }

            result.Set("open_scenes", scenes);

            // Prefab isolation ("prefab stage") has its own dirty flag that the scene
            // list above cannot see.
            PrefabStage prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                anyDirty = anyDirty || prefabStage.scene.isDirty;
                result.Set("prefab_stage", RevvyJson.Object()
                    .Set("asset_path", prefabStage.assetPath ?? string.Empty)
                    .Set("dirty", prefabStage.scene.isDirty));
            }

            result.Set("has_unsaved_changes", anyDirty);
            result.Set("dirty_scene_count", CountDirtyScenes());

            RevvyEditorServer server = RevvyEditorServer.Instance;
            result.Set("bridge", RevvyJson.Object()
                .Set("running", server != null && server.IsRunning)
                .Set("port", server != null ? server.Port : RevvyEnv.ResolveEditorPort())
                .Set("endpoint", RevvyEditorServer.EndpointPath)
                .Set("tool_count", RevvyToolRegistry.Count)
                .Set("config_path", RevvyEnv.ResolveConfigPath())
                .Set("sessions", RevvyMcpHandler.SessionCount));

            // TODO(tier-2): enumerate individually dirty assets. Unity exposes no public
            // "all dirty assets" query; the honest answer today is the scene/prefab flags
            // above plus AssetDatabase.SaveAssets() as the write-through.
            return result;
        }

        [RevvyTool(
            "manage_play_mode",
            "Inspect, start, stop, pause, resume, or single-frame step play mode for the " +
            "currently edited scene.",
            Destructive = true,
            OpenWorld = true,
            ReadOnlyActions = new[] { "status" })]
        public static RevvyJson ManagePlayMode(
            [RevvyToolParam(
                "Operation to perform.",
                Enum = new[] { "status", "start", "stop", "pause", "resume", "step" })]
            string action,
            [RevvyToolParam(
                "For step: how many frames to advance. Each frame is advanced individually " +
                "and the game stays paused afterwards.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":1,\"maximum\":60}")]
            int frames = 1)
        {
            string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

            // Tier-9 §4: `frames` defaults to 1, so an explicit 1 is indistinguishable
            // from omission and is never rejected as unused. Anything else on a
            // non-step action is a caller error.
            if (normalized != "step" && frames != DefaultStepFrames)
            {
                throw new RevvyToolException(
                    "Argument 'frames' is not used by action '" + normalized + "'; omit it.",
                    "invalid_argument");
            }

            switch (normalized)
            {
                case "status":
                    return BuildPlayModeResult(normalized, false, false);
                case "start":
                    return RequestPlayMode(true, normalized);
                case "stop":
                    return RequestPlayMode(false, normalized);
                case "pause":
                    return RequestPause(true, normalized);
                case "resume":
                    return RequestPause(false, normalized);
                case "step":
                    return RequestStep(frames);
                default:
                    throw new RevvyToolException(
                        "Unknown action '" + action +
                        "'. Expected one of: status, start, stop, pause, resume, step.",
                        "invalid_argument");
            }
        }

        // ------------------------------------------------------------------ tier-9

        private const int DefaultStepFrames = 1;
        private const int MinimumStepFrames = 1;
        private const int MaximumStepFrames = 60;
        private const string PendingStepsKey = "RevStudio.Revvy.PlayMode.PendingSteps";

        private static int _pendingSteps;
        private static bool _stepPumpHooked;

        /// <summary>
        /// Contract §5: Unity accepts pause and step in edit mode, does nothing, and
        /// leaves a flag that makes the next play session start paused. Refuse instead.
        /// </summary>
        private static void RequirePlayModeForFrameControl(string action)
        {
            RefreshPlayTransition();
            if (EditorApplication.isPlaying)
            {
                return;
            }

            throw new RevvyToolException(
                "Action '" + action + "' requires play mode; Unity would accept it in edit mode " +
                "and do nothing. Start play mode with action=start and retry.",
                "play_mode_required");
        }

        private static RevvyJson RequestPause(bool targetPaused, string action)
        {
            RequirePlayModeForFrameControl(action);

            if (EditorApplication.isPaused == targetPaused)
            {
                return BuildPlayModeResult(action, false, true);
            }

            // Resuming abandons any owed frames: the caller asked for free-running play,
            // not for the remainder of a step sequence to fire first.
            if (!targetPaused)
            {
                SetPendingSteps(0);
            }

            EditorApplication.isPaused = targetPaused;
            return BuildPlayModeResult(action, true, true);
        }

        /// <summary>
        /// Contract §6: Step() lands on a later editor tick, so this queues the frames and
        /// returns. Blocking the main thread would stall every other tool and still could
        /// not manufacture a game frame.
        /// </summary>
        private static RevvyJson RequestStep(int frames)
        {
            if (frames < MinimumStepFrames || frames > MaximumStepFrames)
            {
                throw new RevvyToolException(
                    "frames must be between " + MinimumStepFrames + " and " + MaximumStepFrames + ".",
                    "invalid_argument");
            }

            RequirePlayModeForFrameControl("step");

            // Stepping implies pausing: a step into a free-running game is meaningless.
            if (!EditorApplication.isPaused)
            {
                EditorApplication.isPaused = true;
            }

            EnsureStepPumpHooked();
            SetPendingSteps(_pendingSteps + frames);
            return BuildPlayModeResult("step", true, true);
        }

        private static void EnsureStepPumpHooked()
        {
            if (_stepPumpHooked)
            {
                return;
            }

            _stepPumpHooked = true;
            EditorApplication.update += PumpPendingSteps;
        }

        /// <summary>
        /// One Step() per editor tick. Step() is asynchronous, so issuing N in a row
        /// inside one tick would collapse into fewer than N frames (contract §10).
        /// </summary>
        private static void PumpPendingSteps()
        {
            RestorePendingSteps();
            if (_pendingSteps <= 0)
            {
                return;
            }

            // A step has no meaning without a running game; abandon rather than replay
            // them into the next session (contract §6).
            if (!EditorApplication.isPlaying)
            {
                SetPendingSteps(0);
                return;
            }

            EditorApplication.Step();
            SetPendingSteps(_pendingSteps - 1);
        }

        private static void SetPendingSteps(int value)
        {
            _pendingSteps = value < 0 ? 0 : value;
            SessionState.SetInt(PendingStepsKey, _pendingSteps);
        }

        private static void RestorePendingSteps()
        {
            if (_pendingSteps == 0)
            {
                _pendingSteps = SessionState.GetInt(PendingStepsKey, 0);
            }
        }

        internal static int PendingSteps
        {
            get
            {
                RestorePendingSteps();
                return _pendingSteps;
            }
        }

        private static RevvyJson RequestPlayMode(bool targetPlaying, string action)
        {
            RefreshPlayTransition();
            // Deferred failures are delivered by status; a new mutation supersedes them.
            ClearPlayTransitionError();

            if (_requestedPlaying.HasValue && _requestedPlaying.Value == targetPlaying)
            {
                return BuildPlayModeResult(action, false, true);
            }

            // isPlayingOrWillChangePlaymode reflects the editor's current target even
            // while isPlaying still reports the state being exited or entered.
            if (!_requestedPlaying.HasValue &&
                EditorApplication.isPlayingOrWillChangePlaymode == targetPlaying)
            {
                return BuildPlayModeResult(action, false, true);
            }

            if (targetPlaying)
            {
                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
                {
                    throw new RevvyToolException(
                        "Cannot start play mode because the active scene has not been saved.");
                }

                if (EditorApplication.isCompiling)
                {
                    throw new RevvyToolException(
                        "Cannot start play mode while Unity is compiling scripts.");
                }

                if (EditorApplication.isUpdating)
                {
                    throw new RevvyToolException(
                        "Cannot start play mode while Unity is importing or updating assets.");
                }
            }

            QueuePlayTransition(targetPlaying);
            return BuildPlayModeResult(action, true, true);
        }

        private static void QueuePlayTransition(bool targetPlaying)
        {
            int sequence = ++_playTransitionSequence;
            _requestedPlaying = targetPlaying;
            _playTransitionDeadlineUtcTicks =
                DateTime.UtcNow.AddSeconds(PlayTransitionTimeoutSeconds).Ticks;
            _playTransitionError = null;
            PersistPlayTransition();
            EnsurePlayModeStateHooked();

            // Let the MCP result leave the socket before a possible domain reload.
            EditorApplication.delayCall += () => ApplyPlayTransition(sequence, targetPlaying);
        }

        private static void ApplyPlayTransition(int sequence, bool targetPlaying)
        {
            if (sequence != _playTransitionSequence ||
                !_requestedPlaying.HasValue ||
                _requestedPlaying.Value != targetPlaying)
            {
                return;
            }

            try
            {
                if (targetPlaying)
                {
                    if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                    {
                        FailPlayTransition(
                            sequence,
                            "Cannot start play mode because Unity began compiling or updating assets " +
                            "before the deferred transition could run.");
                        return;
                    }

                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                    {
                        EditorApplication.EnterPlaymode();
                    }
                }
                else if (EditorApplication.isPlaying ||
                         EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    EditorApplication.ExitPlaymode();
                }
            }
            catch (Exception exception)
            {
                FailPlayTransition(
                    sequence,
                    "Play-mode transition failed: " + RevvyLog.Describe(exception));
                RevvyLog.Exception("Play-mode transition failed", exception);
            }
        }

        private static RevvyJson BuildPlayModeResult(string action, bool changed, bool includeVerify)
        {
            RefreshPlayTransition();
            ThrowPlayTransitionError();

            bool playing = EditorApplication.isPlaying;
            bool apiTransitioning =
                playing != EditorApplication.isPlayingOrWillChangePlaymode;
            bool transitioning = apiTransitioning || _requestedPlaying.HasValue;
            Scene scene = SceneManager.GetActiveScene();
            string scenePath = scene.IsValid() ? scene.path ?? string.Empty : string.Empty;

            RevvyJson result = RevvyJson.Object()
                .Set("success", true)
                .Set("action", action)
                .Set("engine", RevvyEnv.EngineId)
                .Set("changed", changed)
                .Set("transition_pending", transitioning)
                // Tier-9 §7: paused and pending_steps are additive; every Tier-2 field
                // keeps its name and meaning. paused is reported in edit mode too, so a
                // stale flag the bridge did not set stays visible instead of surprising.
                .Set("state", RevvyJson.Object()
                    .Set("playing", playing)
                    .Set("paused", EditorApplication.isPaused)
                    .Set("transitioning", transitioning)
                    .Set("pending_steps", PendingSteps)
                    .Set("scene", scenePath))
                .Set("capabilities", RevvyJson.Object()
                    .Set("pause", true)
                    .Set("step", true));

            if (includeVerify)
            {
                result.Set("verify_with", RevvyJson.Object()
                    .Set("tool", "manage_play_mode")
                    .Set("arguments", RevvyJson.Object().Set("action", "status")));
            }

            return result;
        }

        private static void RefreshPlayTransition()
        {
            RestorePlayTransition();
            if (!_requestedPlaying.HasValue)
            {
                return;
            }

            bool playing = EditorApplication.isPlaying;
            bool apiTransitioning =
                playing != EditorApplication.isPlayingOrWillChangePlaymode;
            bool reachedTarget = playing == _requestedPlaying.Value && !apiTransitioning;
            if (reachedTarget)
            {
                ClearPlayTransition(_playTransitionSequence);
            }
            else if (DateTime.UtcNow.Ticks >= _playTransitionDeadlineUtcTicks)
            {
                string targetName = _requestedPlaying.Value ? "playing" : "stopped";
                FailPlayTransition(
                    _playTransitionSequence,
                    "Unity did not reach the requested " + targetName + " state within " +
                    PlayTransitionTimeoutSeconds.ToString("0") + " seconds.");
            }
        }

        internal static bool IsPlayModeActiveOrTransitioning()
        {
            RefreshPlayTransition();
            return EditorApplication.isPlaying ||
                   EditorApplication.isPlayingOrWillChangePlaymode ||
                   _requestedPlaying.HasValue;
        }

        private static void EnsurePlayModeStateHooked()
        {
            if (_playModeStateHooked)
            {
                return;
            }

            _playModeStateHooked = true;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange _)
        {
            RefreshPlayTransition();
        }

        private static void FailPlayTransition(int sequence, string message)
        {
            if (sequence != _playTransitionSequence)
            {
                return;
            }

            _playTransitionError = message;
            ClearPlayTransition(sequence);
            SessionState.SetString(PlayTransitionErrorKey, message);
            RevvyLog.Warn(message);
        }

        private static void ThrowPlayTransitionError()
        {
            if (string.IsNullOrEmpty(_playTransitionError))
            {
                return;
            }

            string message = _playTransitionError;
            ClearPlayTransitionError();
            throw new RevvyToolException(message);
        }

        private static void ClearPlayTransitionError()
        {
            _playTransitionError = null;
            SessionState.SetString(PlayTransitionErrorKey, string.Empty);
        }

        private static void PersistPlayTransition()
        {
            SessionState.SetBool(PlayTransitionPendingKey, _requestedPlaying.HasValue);
            SessionState.SetBool(
                PlayTransitionTargetKey,
                _requestedPlaying.HasValue && _requestedPlaying.Value);
            SessionState.SetString(
                PlayTransitionDeadlineKey,
                _playTransitionDeadlineUtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            SessionState.SetString(PlayTransitionErrorKey, string.Empty);
        }

        private static void RestorePlayTransition()
        {
            if (string.IsNullOrEmpty(_playTransitionError))
            {
                _playTransitionError = SessionState.GetString(PlayTransitionErrorKey, string.Empty);
            }

            if (_requestedPlaying.HasValue ||
                !SessionState.GetBool(PlayTransitionPendingKey, false))
            {
                return;
            }

            _requestedPlaying = SessionState.GetBool(PlayTransitionTargetKey, false);
            long deadline;
            if (!long.TryParse(
                    SessionState.GetString(PlayTransitionDeadlineKey, string.Empty),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out deadline))
            {
                deadline = DateTime.UtcNow.Ticks;
            }

            _playTransitionDeadlineUtcTicks = deadline;
            _playTransitionSequence++;
            EnsurePlayModeStateHooked();
        }

        private static void ClearPlayTransition(int sequence)
        {
            if (sequence != _playTransitionSequence)
            {
                return;
            }

            _requestedPlaying = null;
            _playTransitionDeadlineUtcTicks = 0L;
            SessionState.SetBool(PlayTransitionPendingKey, false);
            SessionState.SetString(PlayTransitionDeadlineKey, string.Empty);
        }

        private static int CountDirtyScenes()
        {
            int dirty = 0;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isDirty)
                {
                    dirty++;
                }
            }

            return dirty;
        }

        /// <summary>Snapshot used by the status window; kept here so both share one shape.</summary>
        public static IReadOnlyList<string> DescribeOpenScenes()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid())
                {
                    continue;
                }

                names.Add(scene.name + (scene.isDirty ? "*" : string.Empty));
            }

            return names;
        }
    }

    [InitializeOnLoad]
    public static class RevvyInspectSceneTool
    {
        private const int MaximumDepth = 16;
        private const int MaximumOffset = 10000;
        private const int MaximumLimit = 500;
        private static readonly HashSet<RevvyObjectId> EmittedInstanceIds = new HashSet<RevvyObjectId>();
        private static Scene? _emittedScene;

        static RevvyInspectSceneTool()
        {
            EditorApplication.hierarchyChanged -= ClearEmittedIds;
            EditorApplication.hierarchyChanged += ClearEmittedIds;
            EditorSceneManager.activeSceneChangedInEditMode -= OnActiveSceneChanged;
            EditorSceneManager.activeSceneChangedInEditMode += OnActiveSceneChanged;
        }

        [RevvyTool(
            "inspect_scene",
            "Inspect a bounded portion of the currently edited scene hierarchy without modifying editor state.",
            Title = "Inspect scene",
            ReadOnly = true,
            Idempotent = true,
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson InspectScene(
            [RevvyToolParam(
                "Opaque node ID returned by an earlier inspect_scene call. Empty starts at all scene roots.")]
            string root_id = "",
            [RevvyToolParam(
                "Maximum descendant depth relative to each traversal root. Depth zero returns only the root objects.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":0,\"maximum\":16}")]
            int max_depth = 3,
            [RevvyToolParam(
                "Number of entries to skip in deterministic depth-first pre-order.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":0,\"maximum\":10000}")]
            int offset = 0,
            [RevvyToolParam(
                "Maximum hierarchy entries to return.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}")]
            int limit = 200)
        {
            try
            {
                if (max_depth < 0 || max_depth > MaximumDepth)
                {
                    throw SceneFailure("invalid_argument", "max_depth must be between 0 and 16.");
                }

                if (offset < 0 || offset > MaximumOffset)
                {
                    throw SceneFailure("invalid_argument", "offset must be between 0 and 10000.");
                }

                if (limit < 1 || limit > MaximumLimit)
                {
                    throw SceneFailure("invalid_argument", "limit must be between 1 and 500.");
                }

                if (RevvyEditorStatusTool.IsPlayModeActiveOrTransitioning())
                {
                    throw SceneFailure(
                        "play_mode_active",
                        "Scene inspection is unavailable while play mode is active or transitioning.");
                }

                if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                {
                    throw SceneFailure(
                        "no_edited_scene",
                        "Scene inspection is unavailable while editing a Prefab Stage.");
                }

                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                {
                    throw SceneFailure("no_edited_scene", "There is no currently edited scene.");
                }

                EnsureEmissionScene(scene);
                string normalizedRootId = root_id ?? string.Empty;
                List<GameObject> traversalRoots = new List<GameObject>();
                if (normalizedRootId.Length == 0)
                {
                    traversalRoots.AddRange(scene.GetRootGameObjects());
                }
                else
                {
                    GameObject resolved = ResolveRoot(normalizedRootId, scene);
                    if (resolved == null)
                    {
                        throw SceneFailure(
                            "invalid_root_id",
                            "root_id does not identify a GameObject in the currently edited scene.");
                    }

                    traversalRoots.Add(resolved);
                }

                ScenePage page = new ScenePage();
                foreach (GameObject root in traversalRoots)
                {
                    if (Visit(root, scene, 0, max_depth, offset, limit, page))
                    {
                        break;
                    }
                }

                RevvyJson result = RevvyJson.Object()
                    .Set("success", true)
                    .Set("engine", RevvyEnv.EngineId)
                    .Set("scene", RevvyJson.Object()
                        .Set("name", scene.name ?? string.Empty)
                        .Set("path", scene.path ?? string.Empty)
                        .Set("saved", !string.IsNullOrEmpty(scene.path)))
                    .Set("root_id", normalizedRootId)
                    .Set("max_depth", max_depth)
                    .Set("offset", offset)
                    .Set("limit", limit)
                    .Set("returned", page.Nodes.Count)
                    .Set("truncated", page.HasMore)
                    .Set("next_offset", page.HasMore
                        ? RevvyJson.Number(offset + page.Nodes.Count)
                        : RevvyJson.Null())
                    .Set("nodes", page.Nodes);

                return result;
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Scene inspection failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        private static GameObject ResolveRoot(string rootId, Scene scene)
        {
            const string Prefix = "unity:";
            if (!rootId.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return null;
            }

            RevvyObjectId instanceId;
            if (!RevvyObjectId.TryParse(
                    rootId.Substring(Prefix.Length),
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out instanceId))
            {
                return null;
            }

            string canonical = Prefix + instanceId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(rootId, canonical, StringComparison.Ordinal) ||
                !EmittedInstanceIds.Contains(instanceId))
            {
                return null;
            }

            GameObject candidate = RevvyObjectIdentity.Resolve(instanceId) as GameObject;
            return candidate != null && candidate.scene.handle == scene.handle ? candidate : null;
        }

        private static bool Visit(
            GameObject gameObject,
            Scene scene,
            int depth,
            int maxDepth,
            int offset,
            int limit,
            ScenePage page)
        {
            if (page.Seen < offset)
            {
                page.Seen++;
            }
            else if (page.Nodes.Count < limit)
            {
                page.Nodes.Add(Describe(gameObject, scene, depth));
                page.Seen++;
            }
            else
            {
                page.HasMore = true;
                return true;
            }

            if (depth >= maxDepth)
            {
                return false;
            }

            Transform transform = gameObject.transform;
            for (int i = 0; i < transform.childCount; i++)
            {
                GameObject child = transform.GetChild(i).gameObject;
                if (child.scene.handle == scene.handle &&
                    Visit(child, scene, depth + 1, maxDepth, offset, limit, page))
                {
                    return true;
                }
            }

            return false;
        }

        private static RevvyJson Describe(GameObject gameObject, Scene scene, int depth)
        {
            Transform parent = gameObject.transform.parent;
            string parentId = parent != null && parent.gameObject.scene.handle == scene.handle
                ? FormatNodeId(parent.gameObject)
                : string.Empty;

            RevvyObjectId instanceId = RevvyObjectIdentity.GetId(gameObject);
            EmittedInstanceIds.Add(instanceId);

            return RevvyJson.Object()
                .Set("id", FormatNodeId(instanceId))
                .Set("parent_id", parentId)
                .Set("name", gameObject.name ?? string.Empty)
                .Set("path", DisplayPath(gameObject, scene))
                .Set("type", "GameObject")
                .Set("depth", depth)
                .Set("child_count", gameObject.transform.childCount);
        }

        private static string FormatNodeId(GameObject gameObject)
        {
            return FormatNodeId(RevvyObjectIdentity.GetId(gameObject));
        }

        private static string FormatNodeId(RevvyObjectId instanceId)
        {
            return "unity:" + instanceId.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void EnsureEmissionScene(Scene scene)
        {
            if (_emittedScene.HasValue && _emittedScene.Value.handle == scene.handle)
            {
                return;
            }

            ResetEmittedIds();
            _emittedScene = scene;
        }

        private static void OnActiveSceneChanged(Scene _, Scene next)
        {
            ResetEmittedIds();
            _emittedScene = next.IsValid() ? (Scene?)next : null;
        }

        /// <summary>
        /// Drops IDs whose GameObject is gone or has left the emission scene.
        ///
        /// This is a prune, not a wipe. A structural edit must not invalidate the IDs of
        /// objects that survived it: Tier-3 `mutate_scene` has to create an object and
        /// address it on the very next call, and Unity raises `hierarchyChanged`
        /// asynchronously — after the mutating call already handed its ID back to the
        /// caller, so a wipe here would be an unwinnable race. Resolution independently
        /// re-checks liveness and scene membership, so this set only has to stay
        /// bounded, never authoritative.
        /// See docs/design/tier3-scene-mutation-contract.md §4.1.
        /// </summary>
        private static void ClearEmittedIds()
        {
            if (EmittedInstanceIds.Count == 0)
            {
                return;
            }

            List<RevvyObjectId> stale = null;
            foreach (RevvyObjectId instanceId in EmittedInstanceIds)
            {
                GameObject candidate = RevvyObjectIdentity.Resolve(instanceId) as GameObject;
                if (candidate != null && _emittedScene.HasValue && candidate.scene.handle == _emittedScene.Value.handle)
                {
                    continue;
                }

                if (stale == null)
                {
                    stale = new List<RevvyObjectId>();
                }

                stale.Add(instanceId);
            }

            if (stale == null)
            {
                return;
            }

            foreach (RevvyObjectId instanceId in stale)
            {
                EmittedInstanceIds.Remove(instanceId);
            }
        }

        /// <summary>Full wipe, for scene switches where every previously emitted ID is meaningless.</summary>
        private static void ResetEmittedIds()
        {
            EmittedInstanceIds.Clear();
        }

        private static string DisplayPath(GameObject gameObject, Scene scene)
        {
            List<string> names = new List<string>();
            Transform current = gameObject.transform;
            while (current != null && current.gameObject.scene.handle == scene.handle)
            {
                names.Add(current.name ?? string.Empty);
                current = current.parent;
            }

            names.Reverse();
            return "/" + string.Join("/", names.ToArray());
        }

        private static RevvyToolException SceneFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }

        // -------------------------------------------------------------- Tier-3 hooks
        //
        // `mutate_scene` must speak the same opaque-ID dialect as `inspect_scene`, so
        // the emitted-ID registry stays owned here and is reached through these
        // accessors rather than being duplicated. The guard checks are deliberately
        // NOT shared: their exact inline shape in InspectScene is asserted by
        // Tests/Python/test_scene_inspection_source.py.

        /// <summary>Scopes the registry to <paramref name="scene"/>, wiping it on a scene switch.</summary>
        internal static void BindEmissionScene(Scene scene)
        {
            EnsureEmissionScene(scene);
        }

        /// <summary>
        /// Resolves an ID this bridge previously emitted for <paramref name="scene"/>.
        /// Returns null for malformed, non-canonical, never-emitted, stale, and
        /// out-of-scene IDs alike; the caller decides which error code that maps to.
        /// </summary>
        internal static GameObject ResolveEmittedId(string id, Scene scene)
        {
            return ResolveRoot(id ?? string.Empty, scene);
        }

        /// <summary>
        /// Formats an ID and makes it addressable by later calls. Use only for IDs the
        /// response actually returns — a merely-described parent stays unaddressable,
        /// matching <see cref="Describe"/>.
        /// </summary>
        internal static string EmitNodeId(GameObject gameObject)
        {
            RevvyObjectId instanceId = RevvyObjectIdentity.GetId(gameObject);
            EmittedInstanceIds.Add(instanceId);
            return FormatNodeId(instanceId);
        }

        /// <summary>Formats an ID without registering it (descriptive `parent_id` fields).</summary>
        internal static string DescribeNodeId(GameObject gameObject)
        {
            return FormatNodeId(gameObject);
        }

        /// <summary>Display-only hierarchy path, shared so both tools render it identically.</summary>
        internal static string DescribeDisplayPath(GameObject gameObject, Scene scene)
        {
            return DisplayPath(gameObject, scene);
        }

        private sealed class ScenePage
        {
            public readonly RevvyJson Nodes = RevvyJson.Array();
            public int Seen;
            public bool HasMore;
        }
    }
}
