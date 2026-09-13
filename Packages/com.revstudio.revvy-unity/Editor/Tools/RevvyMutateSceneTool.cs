using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-3 scene mutation (docs/design/tier3-scene-mutation-contract.md).
    ///
    /// One structural edit per call — create, rename, reparent, or delete — against the
    /// currently edited scene. Components, properties, transforms, prefabs, selection,
    /// and scene saving are all out of scope for this slice.
    ///
    /// Every action is wrapped in a single collapsed undo group, so one tool call is
    /// exactly one editor undo step. Dispatch arrives on the Unity main thread through
    /// <see cref="RevvyToolRegistry"/> / <see cref="RevvyMainThread"/>; this tool never
    /// starts a thread of its own.
    /// </summary>
    public static class RevvyMutateSceneTool
    {
        private const int MinimumNameLength = 1;
        private const int MaximumNameLength = 128;

        /// <summary>
        /// The curated, engine-neutral creation vocabulary (tier-5 contract §4).
        /// Closed set: adding a value is a contract change needing a mapping in both
        /// engines. `object` stays first and stays the schema default.
        /// </summary>
        private static readonly string[] SupportedNodeTypes =
        {
            "object",
            "camera",
            "light_point",
            "light_directional"
        };

        [RevvyTool(
            "mutate_scene",
            "Create, instantiate a prefab, rename, reparent, or delete a single object in the " +
            "currently edited scene.",
            Title = "Mutate scene",
            Destructive = true,
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson MutateScene(
            [RevvyToolParam(
                "Structural edit to perform.",
                Enum = new[] { "create", "rename", "reparent", "delete", "instantiate" })]
            string action,
            [RevvyToolParam(
                "For create and instantiate: opaque ID of the parent object. Empty places the object " +
                "at the scene root.")]
            string parent_id = "",
            [RevvyToolParam(
                "For create and rename: the object name. For instantiate: optional override of the " +
                "instance root name. 1 to 128 characters, not whitespace-only, no control characters.")]
            string name = "",
            [RevvyToolParam(
                "For create: the kind of object to create. Engine-neutral names only; use " +
                "execute_script for arbitrary engine classes.",
                Enum = new[] { "object", "camera", "light_point", "light_directional" })]
            string node_type = "object",
            [RevvyToolParam(
                "For rename, reparent, and delete: opaque ID of the object to modify.")]
            string target_id = "",
            [RevvyToolParam(
                "For reparent: opaque ID of the new parent. Empty moves the object to the scene root.")]
            string new_parent_id = "",
            [RevvyToolParam(
                "For instantiate: project-relative path of the asset to instantiate. Unity accepts " +
                "Assets/....prefab; Godot accepts res://....tscn or res://....scn.")]
            string asset_path = "")
        {
            try
            {
                // Contract §6: guards first, so a caller in play mode always sees
                // play_mode_active rather than a complaint about its arguments.
                //
                // These checks mirror inspect_scene's inline guards on purpose. Sharing
                // them would break Tests/Python/test_scene_inspection_source.py, which
                // asserts their literal shape inside InspectScene.
                if (RevvyEditorStatusTool.IsPlayModeActiveOrTransitioning())
                {
                    throw MutationFailure(
                        "play_mode_active",
                        "Scene mutation is unavailable while play mode is active or transitioning. " +
                        "Edits to a play-mode world are discarded when play stops.");
                }

                if (PrefabStageUtility.GetCurrentPrefabStage() != null)
                {
                    throw MutationFailure(
                        "no_edited_scene",
                        "Scene mutation is unavailable while editing a Prefab Stage.");
                }

                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                {
                    throw MutationFailure("no_edited_scene", "There is no currently edited scene.");
                }

                RevvyInspectSceneTool.BindEmissionScene(scene);

                string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();
                switch (normalized)
                {
                    case "create":
                        RejectUnused(normalized, "target_id", target_id);
                        RejectUnused(normalized, "new_parent_id", new_parent_id);
                        RejectUnused(normalized, "asset_path", asset_path);
                        return Create(scene, parent_id, name, node_type);
                    case "rename":
                        RejectUnused(normalized, "parent_id", parent_id);
                        RejectUnused(normalized, "new_parent_id", new_parent_id);
                        RejectUnused(normalized, "asset_path", asset_path);
                        RejectUnusedNodeType(normalized, node_type);
                        return Rename(scene, target_id, name);
                    case "reparent":
                        RejectUnused(normalized, "parent_id", parent_id);
                        RejectUnused(normalized, "name", name);
                        RejectUnused(normalized, "asset_path", asset_path);
                        RejectUnusedNodeType(normalized, node_type);
                        return Reparent(scene, target_id, new_parent_id);
                    case "delete":
                        RejectUnused(normalized, "parent_id", parent_id);
                        RejectUnused(normalized, "name", name);
                        RejectUnused(normalized, "new_parent_id", new_parent_id);
                        RejectUnused(normalized, "asset_path", asset_path);
                        RejectUnusedNodeType(normalized, node_type);
                        return Delete(scene, target_id);
                    case "instantiate":
                        RejectUnused(normalized, "target_id", target_id);
                        RejectUnused(normalized, "new_parent_id", new_parent_id);
                        RejectUnusedNodeType(normalized, node_type);
                        return Instantiate(scene, asset_path, parent_id, name);
                    default:
                        throw MutationFailure(
                            "invalid_argument",
                            "Unknown action '" + action +
                            "'. Expected one of: create, rename, reparent, delete, instantiate.");
                }
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Scene mutation failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        // ------------------------------------------------------------------ actions

        private static RevvyJson Create(Scene scene, string parentId, string name, string nodeType)
        {
            // Tier-5 contract §6: the type is validated before the name, so a caller
            // asking for a type that does not exist is told that first rather than
            // being sent to fix a name for an object that could never be created.
            string resolvedType = RequireNodeType(nodeType);
            string validated = RequireName(name);

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentId))
            {
                parent = RevvyInspectSceneTool.ResolveEmittedId(parentId, scene);
                if (parent == null)
                {
                    throw InvalidParent("parent_id", parentId);
                }
            }

            int group = BeginUndoGroup("Revvy create object");
            GameObject created = new GameObject(validated);
            Undo.RegisterCreatedObjectUndo(created, "Revvy create object");
            AttachTypedComponent(created, resolvedType);
            if (parent != null)
            {
                Undo.SetTransformParent(created.transform, parent.transform, "Revvy create object");
            }

            EndUndoGroup(group, scene);

            return BuildNodeResult(
                "create", true, scene, created,
                extra => extra.Set("node_type", resolvedType));
        }

        /// <summary>
        /// Applies the curated node_type vocabulary (tier-5 contract §4).
        ///
        /// Exactly one component is added, and no property is set beyond the light's
        /// type discriminator — Unity's own editor menu adds an AudioListener next to a
        /// camera, which this deliberately does not, because a second surprise component
        /// is not something a later inspect_scene would reveal.
        ///
        /// The add runs inside the caller's undo group, so the object and its component
        /// are reverted together as one step.
        /// </summary>
        private static void AttachTypedComponent(GameObject created, string nodeType)
        {
            switch (nodeType)
            {
                case "object":
                    return;
                case "camera":
                    Undo.AddComponent<Camera>(created);
                    return;
                case "light_point":
                    AddLight(created, LightType.Point);
                    return;
                case "light_directional":
                    AddLight(created, LightType.Directional);
                    return;
                default:
                    // Unreachable: RequireNodeType is the gate. Kept so a future
                    // vocabulary entry cannot silently produce a plain empty object.
                    throw MutationFailure(
                        "editor_api_failure",
                        "node_type '" + nodeType + "' passed validation but has no Unity mapping.");
            }
        }

        private static void AddLight(GameObject created, LightType lightType)
        {
            Light light = Undo.AddComponent<Light>(created);

            // No Undo.RecordObject: the component itself belongs to this undo group, so
            // undo destroys it outright and there is no prior field value to restore.
            // The discriminator is always assigned explicitly rather than relying on
            // Unity's default, so the two light values cannot drift apart across
            // engine versions (tier-5 contract §4).
            light.type = lightType;
        }

        /// <summary>
        /// Tier-4 instantiation (docs/design/tier4-instantiation-contract.md).
        ///
        /// The optional rename runs inside the same undo group as the instantiation, so
        /// the caller never observes an intermediate state under the prefab's own name
        /// and a single undo reverts both (tier-4 contract §3).
        /// </summary>
        private static RevvyJson Instantiate(Scene scene, string assetPath, string parentId, string name)
        {
            string normalizedAssetPath = RequireAssetPath(assetPath);

            // Contract §6: the unusable path is reported before the parent is resolved,
            // because fixing the parent would not make this call succeed.
            string validatedName = string.IsNullOrEmpty(name) ? null : RequireName(name);

            GameObject parent = null;
            if (!string.IsNullOrEmpty(parentId))
            {
                parent = RevvyInspectSceneTool.ResolveEmittedId(parentId, scene);
                if (parent == null)
                {
                    throw InvalidParent("parent_id", parentId);
                }
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(normalizedAssetPath);
            if (prefab == null)
            {
                throw MutationFailure(
                    "asset_not_found",
                    "No prefab asset exists at '" + normalizedAssetPath +
                    "'. Use manage_asset with action=search to locate it; the path is never guessed.");
            }

            int group = BeginUndoGroup("Revvy instantiate prefab");

            // InstantiatePrefab (not Object.Instantiate) so the instance keeps its prefab
            // connection; the scene overload puts it in the edited scene directly.
            GameObject instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (instance == null)
            {
                throw MutationFailure(
                    "editor_api_failure",
                    "Unity returned no instance for prefab '" + normalizedAssetPath + "'.");
            }

            Undo.RegisterCreatedObjectUndo(instance, "Revvy instantiate prefab");

            if (parent != null)
            {
                Undo.SetTransformParent(instance.transform, parent.transform, "Revvy instantiate prefab");
            }

            if (validatedName != null)
            {
                Undo.RecordObject(instance, "Revvy instantiate prefab");
                instance.name = validatedName;
            }

            EndUndoGroup(group, scene);

            return BuildNodeResult(
                "instantiate", true, scene, instance,
                extra => extra.Set("asset_path", normalizedAssetPath));
        }

        private static RevvyJson Rename(Scene scene, string targetId, string name)
        {
            string validated = RequireName(name);
            GameObject target = RequireTarget(scene, targetId);

            string previousName = target.name ?? string.Empty;
            if (string.Equals(previousName, validated, StringComparison.Ordinal))
            {
                // Contract §5.1: a no-op still succeeds, but records no undo step and
                // must not dirty a scene the caller has not actually changed.
                return BuildNodeResult(
                    "rename", false, scene, target,
                    extra => extra.Set("previous_name", previousName));
            }

            int group = BeginUndoGroup("Revvy rename object");
            Undo.RecordObject(target, "Revvy rename object");
            target.name = validated;
            EndUndoGroup(group, scene);

            return BuildNodeResult(
                "rename", true, scene, target,
                extra => extra.Set("previous_name", previousName));
        }

        private static RevvyJson Reparent(Scene scene, string targetId, string newParentId)
        {
            GameObject target = RequireTarget(scene, targetId);

            GameObject newParent = null;
            if (!string.IsNullOrEmpty(newParentId))
            {
                newParent = RevvyInspectSceneTool.ResolveEmittedId(newParentId, scene);
                if (newParent == null)
                {
                    throw InvalidParent("new_parent_id", newParentId);
                }

                if (ReferenceEquals(newParent, target))
                {
                    throw MutationFailure(
                        "invalid_reparent",
                        "new_parent_id identifies target_id itself; an object cannot be its own parent.");
                }

                if (IsDescendantOf(newParent.transform, target.transform))
                {
                    throw MutationFailure(
                        "invalid_reparent",
                        "new_parent_id identifies a descendant of target_id; reparenting there would " +
                        "detach a cycle from the scene.");
                }
            }

            Transform currentParent = target.transform.parent;
            string previousParentId =
                currentParent != null && currentParent.gameObject.scene.handle == scene.handle
                    ? RevvyInspectSceneTool.DescribeNodeId(currentParent.gameObject)
                    : string.Empty;

            Transform desiredParent = newParent != null ? newParent.transform : null;
            if (ReferenceEquals(currentParent, desiredParent))
            {
                return BuildNodeResult(
                    "reparent", false, scene, target,
                    extra => extra.Set("previous_parent_id", previousParentId));
            }

            int group = BeginUndoGroup("Revvy reparent object");
            Undo.SetTransformParent(target.transform, desiredParent, "Revvy reparent object");
            EndUndoGroup(group, scene);

            return BuildNodeResult(
                "reparent", true, scene, target,
                extra => extra.Set("previous_parent_id", previousParentId));
        }

        private static RevvyJson Delete(Scene scene, string targetId)
        {
            GameObject target = RequireTarget(scene, targetId);

            // Contract §5.2: the payload describes the object as it was, so everything
            // is captured before the editor call that makes it unreadable.
            RevvyJson deleted = DescribeNode(scene, target, RevvyInspectSceneTool.DescribeNodeId(target));

            int group = BeginUndoGroup("Revvy delete object");
            Undo.DestroyObjectImmediate(target);
            EndUndoGroup(group, scene);

            return BuildResultEnvelope("delete", true, scene)
                .Set("deleted", deleted)
                .Set("verify_with", RevvyJson.Object()
                    .Set("tool", "inspect_scene")
                    .Set("arguments", RevvyJson.Object()
                        .Set("root_id", string.Empty)
                        .Set("max_depth", 3)));
        }

        // ------------------------------------------------------------------ undo

        /// <summary>
        /// Opens a fresh undo group so the operations that follow collapse into exactly
        /// one editor undo step (contract §1).
        /// </summary>
        private static int BeginUndoGroup(string label)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            return group;
        }

        private static void EndUndoGroup(int group, Scene scene)
        {
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        // ------------------------------------------------------------------ validation

        /// <summary>
        /// Rejects arguments the chosen action does not consume. Contract §3: silently
        /// ignoring an argument that looks honoured is the worst failure mode for a
        /// model driving this tool, so it is an error instead.
        /// </summary>
        private static void RejectUnused(string action, string argumentName, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                throw UnusedArgument(action, argumentName);
            }
        }

        /// <summary>
        /// `node_type` defaults to "object", so an omitted value is indistinguishable
        /// from the default. Only a value that is neither absent nor the default counts
        /// as wrongly supplied.
        /// </summary>
        private static void RejectUnusedNodeType(string action, string nodeType)
        {
            if (!string.IsNullOrEmpty(nodeType) &&
                !string.Equals(nodeType, "object", StringComparison.Ordinal))
            {
                throw UnusedArgument(action, "node_type");
            }
        }

        private static RevvyToolException UnusedArgument(string action, string argumentName)
        {
            return MutationFailure(
                "invalid_argument",
                "Argument '" + argumentName + "' is not used by action '" + action + "'; omit it.");
        }

        /// <summary>
        /// Validates node_type against the curated vocabulary and returns the resolved
        /// value (tier-5 contract §3, §4).
        ///
        /// Matching is exact. Unlike `action`, which is trimmed and lower-cased,
        /// node_type is an advertised schema enum, so "Camera" and " camera" are
        /// invalid_argument — accepting near-misses would make the whitelist softer
        /// than it reads. Raw engine class names are never accepted here; arbitrary
        /// type construction belongs to execute_script.
        /// </summary>
        private static string RequireNodeType(string nodeType)
        {
            string value = string.IsNullOrEmpty(nodeType) ? "object" : nodeType;
            for (int i = 0; i < SupportedNodeTypes.Length; i++)
            {
                if (string.Equals(value, SupportedNodeTypes[i], StringComparison.Ordinal))
                {
                    return value;
                }
            }

            throw MutationFailure(
                "invalid_argument",
                "Unknown node_type '" + nodeType + "'. Expected one of: " +
                string.Join(", ", SupportedNodeTypes) + ".");
        }

        /// <summary>
        /// Tier-4 contract §3 asset_path rules. Returns the normalized path.
        ///
        /// Steps 1 through 4 are all invalid_argument because they describe a path that
        /// could never be valid whatever the project contains. Non-existence is a
        /// separate asset_not_found raised at the load site, because the caller's
        /// correct next move differs: search the project rather than rewrite the string.
        /// </summary>
        private static string RequireAssetPath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                throw MutationFailure(
                    "invalid_argument", "Argument 'asset_path' is required for action 'instantiate'.");
            }

            // Windows callers should not be punished for a path separator; nothing else
            // about the string is rewritten.
            string normalized = assetPath.Replace('\\', '/').Trim();

            if (normalized.Length == 0)
            {
                throw MutationFailure(
                    "invalid_argument", "Argument 'asset_path' is required for action 'instantiate'.");
            }

            if (normalized.Contains("..") ||
                normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length > 1 && normalized[1] == ':'))
            {
                throw MutationFailure(
                    "invalid_argument",
                    "asset_path '" + assetPath + "' must be project-relative; absolute paths and " +
                    "'..' are refused so it cannot address anything outside the project.");
            }

            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw MutationFailure(
                    "invalid_argument",
                    "asset_path '" + assetPath + "' must start with 'Assets/'. Package assets are " +
                    "immutable and out of scope for this slice.");
            }

            if (!normalized.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                throw MutationFailure(
                    "invalid_argument",
                    "asset_path '" + assetPath + "' must name a .prefab asset; Unity instantiation " +
                    "in this slice accepts nothing else.");
            }

            return normalized;
        }

        /// <summary>Contract §3 name rules. Returns the name verbatim; never trims.</summary>
        private static string RequireName(string name)
        {
            if (name == null || name.Length == 0)
            {
                throw MutationFailure(
                    "invalid_argument", "Argument 'name' is required for this action.");
            }

            if (name.Length < MinimumNameLength || name.Length > MaximumNameLength)
            {
                throw MutationFailure(
                    "invalid_argument",
                    "name must be between 1 and 128 characters; received " + name.Length + ".");
            }

            if (name.Trim().Length == 0)
            {
                throw MutationFailure("invalid_argument", "name must not be whitespace only.");
            }

            for (int i = 0; i < name.Length; i++)
            {
                if (char.IsControl(name[i]))
                {
                    throw MutationFailure(
                        "invalid_argument",
                        "name must not contain control characters (found one at index " + i + ").");
                }
            }

            return name;
        }

        private static GameObject RequireTarget(Scene scene, string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                throw MutationFailure(
                    "invalid_argument", "Argument 'target_id' is required for this action.");
            }

            GameObject target = RevvyInspectSceneTool.ResolveEmittedId(targetId, scene);
            if (target == null)
            {
                throw MutationFailure(
                    "invalid_target_id",
                    "target_id '" + targetId + "' does not identify a live GameObject previously " +
                    "returned for the currently edited scene. Re-run inspect_scene to reacquire it.");
            }

            return target;
        }

        private static RevvyToolException InvalidParent(string argumentName, string value)
        {
            return MutationFailure(
                "invalid_parent_id",
                argumentName + " '" + value + "' does not identify a live GameObject previously " +
                "returned for the currently edited scene. Re-run inspect_scene to reacquire it.");
        }

        private static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            Transform current = candidate;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        // ------------------------------------------------------------------ payloads

        private static RevvyJson BuildResultEnvelope(string action, bool changed, Scene scene)
        {
            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", action)
                .Set("changed", changed)
                .Set("scene", RevvyJson.Object()
                    .Set("name", scene.name ?? string.Empty)
                    .Set("path", scene.path ?? string.Empty)
                    .Set("saved", !string.IsNullOrEmpty(scene.path)));
        }

        private static RevvyJson BuildNodeResult(
            string action,
            bool changed,
            Scene scene,
            GameObject gameObject,
            Action<RevvyJson> decorate)
        {
            // Only the node this call actually returns becomes addressable; a described
            // parent does not (contract §4, matching inspect_scene).
            string nodeId = RevvyInspectSceneTool.EmitNodeId(gameObject);

            RevvyJson result = BuildResultEnvelope(action, changed, scene)
                .Set("node", DescribeNode(scene, gameObject, nodeId));

            if (decorate != null)
            {
                decorate(result);
            }

            return result.Set("verify_with", RevvyJson.Object()
                .Set("tool", "inspect_scene")
                .Set("arguments", RevvyJson.Object()
                    .Set("root_id", nodeId)
                    .Set("max_depth", 1)));
        }

        /// <summary>
        /// Same field set as an inspect_scene node minus `depth`, which is a
        /// traversal-relative concept with no meaning for a single edited object.
        /// </summary>
        private static RevvyJson DescribeNode(Scene scene, GameObject gameObject, string nodeId)
        {
            Transform parent = gameObject.transform.parent;
            string parentId = parent != null && parent.gameObject.scene.handle == scene.handle
                ? RevvyInspectSceneTool.DescribeNodeId(parent.gameObject)
                : string.Empty;

            return RevvyJson.Object()
                .Set("id", nodeId)
                .Set("parent_id", parentId)
                .Set("name", gameObject.name ?? string.Empty)
                .Set("path", RevvyInspectSceneTool.DescribeDisplayPath(gameObject, scene))
                .Set("type", "GameObject")
                .Set("child_count", gameObject.transform.childCount);
        }

        private static RevvyToolException MutationFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }
    }
}
