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
    /// <summary>
    /// Tier-11 prefab override management
    /// (docs/design/tier11-prefab-overrides-contract.md).
    ///
    /// Enumeration deliberately does **not** trust raw
    /// <c>PrefabUtility.GetPropertyModifications</c>: a pristine instance reports eleven
    /// modifications and all eleven are default overrides (contract §2.1), so reporting
    /// them would tell a caller an untouched object has eleven overrides. The object-level
    /// truth comes from <c>GetObjectOverrides(instance, false)</c> and
    /// <c>HasPrefabInstanceAnyOverrides(instance, false)</c>; per-property rows are
    /// modifications filtered through <c>IsDefaultOverride</c>, which is the same
    /// exclusion applied one level down.
    ///
    /// Every mutating call uses <see cref="InteractionMode.UserAction"/>:
    /// <c>AutomatedAction</c> registers no undo entry at all (measured, contract §2.3).
    /// </summary>
    public static class RevvyManagePrefabTool
    {
        /// <summary>
        /// Curated property name to the component it lives on and its serialized path.
        /// The curated vocabulary is Tier-8's; the serialized paths are Unity-internal and
        /// never travel inbound on the wire (contract §5.1).
        /// </summary>
        private sealed class PropertyBinding
        {
            public readonly string Owner;
            public readonly string SerializedPath;

            public PropertyBinding(string owner, string serializedPath)
            {
                Owner = owner;
                SerializedPath = serializedPath;
            }
        }

        private static readonly Dictionary<string, PropertyBinding> Bindings = BuildBindings();

        [RevvyTool(
            "manage_prefab",
            "List, revert, or apply the overrides a prefab instance carries relative to its " +
            "source asset.",
            Title = "Manage prefab overrides",
            Destructive = true,
            ReadOnlyActions = new[] { "list_overrides" },
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson ManagePrefab(
            [RevvyToolParam(
                "Operation to perform.",
                Enum = new[] { "list_overrides", "revert", "apply" })]
            string action,
            [RevvyToolParam(
                "Opaque ID of the prefab instance root, or of a node inside it, as returned " +
                "by inspect_scene or mutate_scene.")]
            string target_id = "",
            [RevvyToolParam(
                "For revert: engine-neutral property name from the curated vocabulary. Omit " +
                "to revert every override on the instance.",
                Enum = new[]
                {
                    "transform.position", "transform.rotation", "transform.scale",
                    "light.color", "light.intensity", "light.range", "light.shadows_enabled",
                    "camera.field_of_view", "camera.near_clip", "camera.far_clip", "camera.orthographic"
                })]
            string property = "")
        {
            try
            {
                string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

                // Contract §10: arguments and vocabulary before the guards — a misspelled
                // property is a caller bug that survives leaving play mode.
                switch (normalized)
                {
                    case "list_overrides":
                    case "apply":
                        RejectUnused(normalized, "property", property);
                        break;
                    case "revert":
                        if (!string.IsNullOrEmpty(property))
                        {
                            RequireProperty(property);
                        }

                        break;
                    default:
                        throw PrefabFailure(
                            "invalid_argument",
                            "Unknown action '" + action +
                            "'. Expected one of: list_overrides, revert, apply.");
                }

                Scene scene = RequireEditedScene();
                GameObject node = RequireTarget(scene, target_id);
                GameObject root = RequireInstanceRoot(node);
                string assetPath = RequireAssetPath(node);

                switch (normalized)
                {
                    case "list_overrides":
                        return ListOverrides(target_id, root, assetPath);
                    case "revert":
                        return Revert(scene, target_id, node, root, property);
                    default:
                        RequireApplyable(root, assetPath);
                        return Apply(scene, target_id, root, assetPath);
                }
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Prefab override access failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        // ------------------------------------------------------------------ list

        private static RevvyJson ListOverrides(string targetId, GameObject root, string assetPath)
        {
            RevvyJson propertyOverrides = RevvyJson.Array();

            // One logical override per row. A Vector3 arrives as three modifications
            // (m_LocalPosition.x/.y/.z) that all mean one curated property, and emitting
            // three rows would tell a caller it has three overrides to revert.
            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<RevvyObjectId, UnityEngine.Object> sourceToInstance = BuildSourceToInstanceMap(root);

            foreach (PropertyModification modification in PrefabUtility.GetPropertyModifications(root))
            {
                // Contract §2.1: the eleven modifications a pristine instance reports are
                // all default overrides, and an instance root's own transform stays a
                // default override however far it is moved.
                if (modification == null || PrefabUtility.IsDefaultOverride(modification))
                {
                    continue;
                }

                RevvyJson row = DescribeModification(modification, emitted, sourceToInstance);
                if (row != null)
                {
                    propertyOverrides.Add(row);
                }
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "list_overrides")
                .Set("target_id", targetId)
                .Set("instance", DescribeInstance(root, assetPath))
                .Set("has_overrides", PrefabUtility.HasPrefabInstanceAnyOverrides(root, false))
                .Set("property_overrides", propertyOverrides)
                .Set("added_components", DescribeAddedComponents(root))
                .Set("added_objects", DescribeAddedObjects(root))
                .Set("removed_components", DescribeRemovedComponents(root))
                .Set("removed_objects", DescribeRemovedObjects(root))
                // Contract §11: Unity has no editable-instance condition.
                .Set("editable_enabled", false);
        }

        /// <summary>
        /// Describes one override row.
        ///
        /// <c>PropertyModification.target</c> is the object in the **prefab asset**, not
        /// the scene instance — a positive instance ID, where scene objects are negative.
        /// Reading values off it would report the source as if it were the instance, and
        /// registering its ID would hand the caller an opaque ID that resolves outside the
        /// edited scene. So the asset object is used for the source value and mapped back
        /// through <see cref="BuildSourceToInstanceMap"/> for everything instance-side.
        /// </summary>
        private static RevvyJson DescribeModification(
            PropertyModification modification,
            HashSet<string> emitted,
            Dictionary<RevvyObjectId, UnityEngine.Object> sourceToInstance)
        {
            if (modification.target == null)
            {
                return null;
            }

            GameObject sourceOwner = OwnerGameObject(modification.target);
            if (sourceOwner == null)
            {
                return null;
            }

            string curated = CuratedNameFor(modification.target, modification.propertyPath);

            // Curated rows collapse per (object, curated name); non-curated rows keep their
            // exact engine path, which is already unique.
            string key = RevvyObjectIdentity.GetId(modification.target).ToString() + "|" +
                         (curated ?? modification.propertyPath ?? string.Empty);
            if (!emitted.Add(key))
            {
                return null;
            }

            UnityEngine.Object instanceObject;
            GameObject instanceOwner = sourceToInstance.TryGetValue(
                RevvyObjectIdentity.GetId(modification.target), out instanceObject)
                ? OwnerGameObject(instanceObject)
                : null;

            RevvyJson row = RevvyJson.Object()
                .Set("node_id", instanceOwner != null
                    ? RevvyJson.String(RevvyInspectSceneTool.EmitNodeId(instanceOwner))
                    : RevvyJson.Null())
                .Set("node_path", instanceOwner != null
                    ? RevvyJson.String(
                        RevvyInspectSceneTool.DescribeDisplayPath(instanceOwner, instanceOwner.scene))
                    : RevvyJson.Null())
                .Set("property", curated == null ? RevvyJson.Null() : RevvyJson.String(curated))
                .Set("engine_property", modification.propertyPath ?? string.Empty);

            if (curated != null && instanceOwner != null)
            {
                row.Set("instance_value", RevvyManagePropertiesTool.Read(instanceOwner, curated));
                row.Set("source_value", RevvyManagePropertiesTool.Read(sourceOwner, curated));
            }
            else
            {
                // Non-curated overrides are reported so the listing is complete, but their
                // values stay in the engine's serialized form (contract §5.1).
                row.Set("instance_value", RevvyJson.String(modification.value ?? string.Empty));
                row.Set("source_value", RevvyJson.Null());
            }

            // An override whose instance object cannot be located is reported for
            // completeness but cannot be addressed, so it is not revertable.
            return row.Set("revertable", curated != null && instanceOwner != null);
        }

        /// <summary>
        /// Maps every asset object the instance corresponds to back to the instance object,
        /// so a modification (which names the asset side) can be reported and reverted
        /// against the scene side.
        /// </summary>
        private static Dictionary<RevvyObjectId, UnityEngine.Object> BuildSourceToInstanceMap(GameObject root)
        {
            Dictionary<RevvyObjectId, UnityEngine.Object> map = new Dictionary<RevvyObjectId, UnityEngine.Object>();
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
            {
                GameObject gameObject = transform.gameObject;
                AddCorrespondence(map, gameObject);
                foreach (Component component in gameObject.GetComponents<Component>())
                {
                    if (component != null)
                    {
                        AddCorrespondence(map, component);
                    }
                }
            }

            return map;
        }

        private static void AddCorrespondence(
            Dictionary<RevvyObjectId, UnityEngine.Object> map, UnityEngine.Object instanceObject)
        {
            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(instanceObject);
            if (source != null)
            {
                map[RevvyObjectIdentity.GetId(source)] = instanceObject;
            }
        }

        private static RevvyJson DescribeAddedComponents(GameObject root)
        {
            RevvyJson list = RevvyJson.Array();
            foreach (var added in PrefabUtility.GetAddedComponents(root))
            {
                if (added == null || added.instanceComponent == null)
                {
                    continue;
                }

                list.Add(RevvyJson.Object()
                    .Set("node_id", RevvyInspectSceneTool.EmitNodeId(added.instanceComponent.gameObject))
                    .Set("node_path", RevvyInspectSceneTool.DescribeDisplayPath(
                        added.instanceComponent.gameObject, added.instanceComponent.gameObject.scene))
                    .Set("component", added.instanceComponent.GetType().Name));
            }

            return list;
        }

        private static RevvyJson DescribeAddedObjects(GameObject root)
        {
            RevvyJson list = RevvyJson.Array();
            foreach (var added in PrefabUtility.GetAddedGameObjects(root))
            {
                if (added == null || added.instanceGameObject == null)
                {
                    continue;
                }

                list.Add(RevvyJson.Object()
                    .Set("node_id", RevvyInspectSceneTool.EmitNodeId(added.instanceGameObject))
                    .Set("node_path", RevvyInspectSceneTool.DescribeDisplayPath(
                        added.instanceGameObject, added.instanceGameObject.scene))
                    .Set("name", added.instanceGameObject.name ?? string.Empty));
            }

            return list;
        }

        private static RevvyJson DescribeRemovedComponents(GameObject root)
        {
            RevvyJson list = RevvyJson.Array();
            foreach (var removed in PrefabUtility.GetRemovedComponents(root))
            {
                if (removed == null || removed.assetComponent == null)
                {
                    continue;
                }

                list.Add(RevvyJson.Object()
                    .Set("component", removed.assetComponent.GetType().Name));
            }

            return list;
        }

        private static RevvyJson DescribeRemovedObjects(GameObject root)
        {
            RevvyJson list = RevvyJson.Array();
            foreach (var removed in PrefabUtility.GetRemovedGameObjects(root))
            {
                if (removed == null || removed.assetGameObject == null)
                {
                    continue;
                }

                list.Add(RevvyJson.Object().Set("name", removed.assetGameObject.name ?? string.Empty));
            }

            return list;
        }

        // ------------------------------------------------------------------ revert

        private static RevvyJson Revert(
            Scene scene, string targetId, GameObject node, GameObject root, string property)
        {
            if (string.IsNullOrEmpty(property))
            {
                return RevertInstance(scene, targetId, root);
            }

            UnityEngine.Object owner = RevvyManagePropertiesTool.OwnerOf(node, property);
            SerializedProperty serialized = owner != null ? FindSerialized(owner, property) : null;

            // Contract §9.2: no such override is a success with changed=false, and records
            // no undo step — the same shape as a same-value set in Tier-8.
            if (serialized == null || !HasOverrideFor(node, property))
            {
                return BuildRevertResult(
                    targetId, "property", property, 0, false,
                    owner != null ? RevvyManagePropertiesTool.Read(node, property) : RevvyJson.Null(),
                    owner != null ? RevvyManagePropertiesTool.Read(node, property) : RevvyJson.Null());
            }

            RevvyJson previous = RevvyManagePropertiesTool.Read(node, property);

            int group = BeginUndoGroup("Revvy revert prefab override");
            PrefabUtility.RevertPropertyOverride(serialized, InteractionMode.UserAction);
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            return BuildRevertResult(
                targetId, "property", property, 1, true,
                previous, RevvyManagePropertiesTool.Read(node, property));
        }

        private static RevvyJson RevertInstance(Scene scene, string targetId, GameObject root)
        {
            int count = CountOverrides(root);
            if (count == 0)
            {
                return BuildRevertResult(
                    targetId, "instance", null, 0, false, RevvyJson.Null(), RevvyJson.Null());
            }

            int group = BeginUndoGroup("Revvy revert prefab instance");
            PrefabUtility.RevertPrefabInstance(root, InteractionMode.UserAction);
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            return BuildRevertResult(
                targetId, "instance", null, count, true, RevvyJson.Null(), RevvyJson.Null());
        }

        private static RevvyJson BuildRevertResult(
            string targetId,
            string scope,
            string property,
            int revertedCount,
            bool changed,
            RevvyJson previous,
            RevvyJson value)
        {
            RevvyJson result = RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "revert")
                .Set("target_id", targetId)
                .Set("property", property == null ? RevvyJson.Null() : RevvyJson.String(property))
                .Set("scope", scope)
                .Set("reverted_count", revertedCount)
                .Set("previous_value", previous)
                .Set("value", value)
                .Set("changed", changed);

            // Contract §7: revert is undoable in one step on both engines.
            return result.Set("undoable", true).Set("editable_enabled", false);
        }

        // ------------------------------------------------------------------ apply

        private static RevvyJson Apply(Scene scene, string targetId, GameObject root, string assetPath)
        {
            int count = CountOverrides(root);

            int group = BeginUndoGroup("Revvy apply prefab overrides");
            PrefabUtility.ApplyPrefabInstance(root, InteractionMode.UserAction);
            Undo.CollapseUndoOperations(group);

            // The asset is a file: flush it and let the database re-read, so a caller that
            // inspects the asset next sees the applied state (gate 6).
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            EditorSceneManager.MarkSceneDirty(scene);

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "apply")
                .Set("target_id", targetId)
                .Set("instance", DescribeInstance(root, assetPath))
                .Set("applied_count", count)
                // Contract §7: declared non-undoable. Unity's UserAction does register an
                // undo entry, but promising it would dress an engine-specific,
                // session-lifetime behaviour up as a shared guarantee about a file.
                .Set("undoable", false)
                .Set("editable_enabled", false);
        }

        // ------------------------------------------------------------------ guards

        private static Scene RequireEditedScene()
        {
            if (RevvyEditorStatusTool.IsPlayModeActiveOrTransitioning())
            {
                throw PrefabFailure(
                    "play_mode_active",
                    "Prefab override management is unavailable while play mode is active or " +
                    "transitioning.");
            }

            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw PrefabFailure(
                    "no_edited_scene",
                    "Prefab override management is unavailable while editing a Prefab Stage.");
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
            {
                throw PrefabFailure("no_edited_scene", "There is no currently edited scene.");
            }

            RevvyInspectSceneTool.BindEmissionScene(scene);
            return scene;
        }

        private static GameObject RequireTarget(Scene scene, string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                throw PrefabFailure("invalid_argument", "Argument 'target_id' is required.");
            }

            GameObject target = RevvyInspectSceneTool.ResolveEmittedId(targetId, scene);
            if (target == null)
            {
                throw PrefabFailure(
                    "invalid_target_id",
                    "target_id '" + targetId + "' does not identify a live GameObject previously " +
                    "returned for the currently edited scene. Re-run inspect_scene to reacquire it.");
            }

            return target;
        }

        /// <summary>Contract §6: always the nearest instance root, never an outer one.</summary>
        private static GameObject RequireInstanceRoot(GameObject node)
        {
            if (!PrefabUtility.IsPartOfPrefabInstance(node))
            {
                throw PrefabFailure(
                    "not_a_prefab_instance",
                    "'" + node.name + "' is not part of a prefab instance, so it carries no overrides.");
            }

            GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(node);
            if (root == null)
            {
                throw PrefabFailure(
                    "ambiguous_prefab_target",
                    "Unity reports no nearest prefab instance root for '" + node.name +
                    "', so there is no unambiguous asset to target.");
            }

            return root;
        }

        private static string RequireAssetPath(GameObject node)
        {
            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(node);
            if (string.IsNullOrEmpty(assetPath))
            {
                throw PrefabFailure(
                    "ambiguous_prefab_target",
                    "The nearest prefab instance root for '" + node.name + "' has no asset path.");
            }

            return assetPath;
        }

        private static void RequireApplyable(GameObject root, string assetPath)
        {
            if (PrefabUtility.IsPartOfModelPrefab(root))
            {
                throw PrefabFailure(
                    "prefab_not_applyable",
                    "'" + assetPath + "' is an imported model prefab; overrides cannot be applied to it.");
            }

            if (PrefabUtility.IsPartOfImmutablePrefab(root))
            {
                throw PrefabFailure(
                    "prefab_not_applyable",
                    "'" + assetPath + "' is immutable; overrides cannot be applied to it.");
            }

            if (!PrefabUtility.IsPartOfPrefabThatCanBeAppliedTo(root))
            {
                throw PrefabFailure(
                    "prefab_not_applyable",
                    "Unity refuses applies to '" + assetPath + "'.");
            }
        }

        // ------------------------------------------------------------------ helpers

        private static RevvyJson DescribeInstance(GameObject root, string assetPath)
        {
            return RevvyJson.Object()
                .Set("root_id", RevvyInspectSceneTool.EmitNodeId(root))
                .Set("asset_path", assetPath)
                .Set("status", PrefabUtility.GetPrefabInstanceStatus(root).ToString().ToLowerInvariant())
                .Set("applyable",
                    !PrefabUtility.IsPartOfModelPrefab(root) &&
                    !PrefabUtility.IsPartOfImmutablePrefab(root) &&
                    PrefabUtility.IsPartOfPrefabThatCanBeAppliedTo(root));
        }

        /// <summary>
        /// Counts the same logical overrides <c>list_overrides</c> reports, so
        /// <c>reverted_count</c> and <c>applied_count</c> agree with the listing rather
        /// than tripling a Vector3.
        /// </summary>
        private static int CountOverrides(GameObject root)
        {
            HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyModification modification in PrefabUtility.GetPropertyModifications(root))
            {
                if (modification == null || PrefabUtility.IsDefaultOverride(modification) ||
                    modification.target == null)
                {
                    continue;
                }

                string curated = CuratedNameFor(modification.target, modification.propertyPath);
                emitted.Add(RevvyObjectIdentity.GetId(modification.target).ToString() + "|" +
                            (curated ?? modification.propertyPath ?? string.Empty));
            }

            return emitted.Count
                + PrefabUtility.GetAddedComponents(root).Count
                + PrefabUtility.GetAddedGameObjects(root).Count
                + PrefabUtility.GetRemovedComponents(root).Count
                + PrefabUtility.GetRemovedGameObjects(root).Count;
        }

        /// <summary>
        /// Whether the engine reports <paramref name="property"/> on
        /// <paramref name="node"/> as a non-default override of its prefab instance.
        ///
        /// Internal because Tier-15 §4 requires `manage_properties` to report
        /// `override_created` from this exact predicate. Two tools deriving
        /// override-ness separately would eventually disagree, and nothing in either
        /// would notice.
        /// </summary>
        internal static bool HasOverrideFor(GameObject node, string property)
        {
            PropertyBinding binding;
            if (!Bindings.TryGetValue(property, out binding))
            {
                return false;
            }

            UnityEngine.Object owner = RevvyManagePropertiesTool.OwnerOf(node, property);
            if (owner == null)
            {
                return false;
            }

            // Modifications name the asset object, so the comparison is against this
            // instance component's source, not the component itself.
            UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(owner);
            if (source == null)
            {
                return false;
            }

            foreach (PropertyModification modification in PrefabUtility.GetPropertyModifications(node))
            {
                if (modification == null || PrefabUtility.IsDefaultOverride(modification))
                {
                    continue;
                }

                if (modification.target == source &&
                    MatchesPath(modification.propertyPath, binding.SerializedPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static SerializedProperty FindSerialized(UnityEngine.Object owner, string property)
        {
            PropertyBinding binding;
            if (!Bindings.TryGetValue(property, out binding))
            {
                return null;
            }

            return new SerializedObject(owner).FindProperty(binding.SerializedPath);
        }

        /// <summary>
        /// A Vector3 override arrives as three modifications (<c>m_LocalPosition.x</c> and
        /// friends), so matching strips the component suffix.
        /// </summary>
        private static bool MatchesPath(string modificationPath, string serializedPath)
        {
            if (string.IsNullOrEmpty(modificationPath))
            {
                return false;
            }

            return modificationPath == serializedPath ||
                   modificationPath.StartsWith(serializedPath + ".", StringComparison.Ordinal);
        }

        private static string CuratedNameFor(UnityEngine.Object target, string modificationPath)
        {
            GameObject owner = OwnerGameObject(target);
            if (owner == null || string.IsNullOrEmpty(modificationPath))
            {
                return null;
            }

            foreach (KeyValuePair<string, PropertyBinding> entry in Bindings)
            {
                if (!MatchesPath(modificationPath, entry.Value.SerializedPath))
                {
                    continue;
                }

                // The same serialized path can exist on more than one component type, so
                // the owning object has to agree as well.
                if (RevvyManagePropertiesTool.OwnerOf(owner, entry.Key) == target)
                {
                    return entry.Key;
                }
            }

            return null;
        }

        private static GameObject OwnerGameObject(UnityEngine.Object target)
        {
            GameObject gameObject = target as GameObject;
            if (gameObject != null)
            {
                return gameObject;
            }

            Component component = target as Component;
            return component != null ? component.gameObject : null;
        }

        private static Dictionary<string, PropertyBinding> BuildBindings()
        {
            return new Dictionary<string, PropertyBinding>(StringComparer.Ordinal)
            {
                { "transform.position", new PropertyBinding("transform", "m_LocalPosition") },
                { "transform.rotation", new PropertyBinding("transform", "m_LocalRotation") },
                { "transform.scale", new PropertyBinding("transform", "m_LocalScale") },
                { "light.color", new PropertyBinding("light", "m_Color") },
                { "light.intensity", new PropertyBinding("light", "m_Intensity") },
                { "light.range", new PropertyBinding("light", "m_Range") },
                { "light.shadows_enabled", new PropertyBinding("light", "m_Shadows.m_Type") },
                { "camera.field_of_view", new PropertyBinding("camera", "field of view") },
                { "camera.near_clip", new PropertyBinding("camera", "near clip plane") },
                { "camera.far_clip", new PropertyBinding("camera", "far clip plane") },
                { "camera.orthographic", new PropertyBinding("camera", "orthographic") }
            };
        }

        private static void RejectUnused(string action, string argumentName, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                throw PrefabFailure(
                    "invalid_argument",
                    "Argument '" + argumentName + "' is not used by action '" + action + "'; omit it.");
            }
        }

        private static string RequireProperty(string property)
        {
            if (!Bindings.ContainsKey(property))
            {
                throw PrefabFailure(
                    "invalid_argument",
                    "Unknown property '" + property + "'. Engine property paths are never accepted; " +
                    "revert is limited to the curated vocabulary. Use execute_script for an " +
                    "override outside it.");
            }

            return property;
        }

        private static int BeginUndoGroup(string label)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            return group;
        }

        private static RevvyToolException PrefabFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }
    }
}
