using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-8 property reading and writing (docs/design/tier8-node-properties-contract.md).
    ///
    /// The vocabulary is curated and engine-neutral: Unity's serialized paths
    /// (<c>m_LocalPosition</c>, <c>m_Intensity</c>) are internal storage names that are
    /// neither portable to Godot nor guessable by a caller, so they never travel on the
    /// wire — the same rule Tier-5 set for <c>node_type</c> and Tier-7 for key names.
    ///
    /// Addressing needs no component index: Unity natively refuses a second Camera or
    /// Light on one GameObject (measured, contract §1.2), so a property name identifies
    /// its component unambiguously.
    /// </summary>
    public static class RevvyManagePropertiesTool
    {
        private const string TypeFloat = "float";
        private const string TypeBool = "bool";
        private const string TypeVector3 = "vector3";
        private const string TypeColor = "color";

        private static readonly string[] SupportedProperties =
        {
            "transform.position",
            "transform.rotation",
            "transform.scale",
            "light.color",
            "light.intensity",
            "light.range",
            "light.shadows_enabled",
            "camera.field_of_view",
            "camera.near_clip",
            "camera.far_clip",
            "camera.orthographic"
        };

        [RevvyTool(
            "manage_properties",
            "Read and write curated properties of an object in the currently edited scene.",
            Title = "Manage properties",
            ReadOnlyActions = new[] { "list", "get" },
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson ManageProperties(
            [RevvyToolParam("Operation to perform.", Enum = new[] { "list", "get", "set" })]
            string action,
            [RevvyToolParam(
                "Opaque ID of the object, as returned by inspect_scene or mutate_scene.")]
            string target_id = "",
            [RevvyToolParam(
                "For get and set: engine-neutral property name from the curated vocabulary.",
                Enum = new[]
                {
                    "transform.position", "transform.rotation", "transform.scale",
                    "light.color", "light.intensity", "light.range", "light.shadows_enabled",
                    "camera.field_of_view", "camera.near_clip", "camera.far_clip", "camera.orthographic"
                })]
            string property = "",
            [RevvyToolParam(
                "For set: the new value, encoded per the property's type (contract section 10).",
                SchemaJson = "{}")]
            RevvyJson value = null)
        {
            try
            {
                string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

                // Contract §8: arguments and vocabulary before the guards — a misspelled
                // property is a caller bug that survives leaving play mode.
                string resolvedProperty = null;
                switch (normalized)
                {
                    case "list":
                        RejectUnused(normalized, "property", property);
                        RejectUnusedValue(normalized, value);
                        break;
                    case "get":
                        RejectUnusedValue(normalized, value);
                        resolvedProperty = RequireProperty(property);
                        break;
                    case "set":
                        resolvedProperty = RequireProperty(property);
                        if (value == null || value.IsNull)
                        {
                            throw PropertyFailure(
                                "invalid_argument", "Argument 'value' is required for action 'set'.");
                        }

                        break;
                    default:
                        throw PropertyFailure(
                            "invalid_argument",
                            "Unknown action '" + action + "'. Expected one of: list, get, set.");
                }

                Scene scene = RequireEditedScene();
                GameObject target = RequireTarget(scene, target_id);

                switch (normalized)
                {
                    case "list":
                        return List(target_id, target);
                    case "get":
                        return Get(target_id, target, resolvedProperty);
                    default:
                        return Set(scene, target_id, target, resolvedProperty, value);
                }
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Property access failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        // ------------------------------------------------------------------ actions

        private static RevvyJson List(string targetId, GameObject target)
        {
            RevvyJson properties = RevvyJson.Array();
            int count = 0;

            foreach (string name in SupportedProperties)
            {
                if (!IsApplicable(target, name))
                {
                    continue;
                }

                properties.Add(RevvyJson.Object()
                    .Set("property", name)
                    .Set("type", TypeOf(name))
                    .Set("value", Read(target, name)));
                count++;
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "list")
                .Set("target_id", targetId)
                .Set("properties", properties)
                .Set("count", count);
        }

        private static RevvyJson Get(string targetId, GameObject target, string property)
        {
            RequireApplicable(target, property);

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "get")
                .Set("target_id", targetId)
                .Set("property", property)
                .Set("type", TypeOf(property))
                .Set("value", Read(target, property));
        }

        private static RevvyJson Set(
            Scene scene, string targetId, GameObject target, string property, RevvyJson value)
        {
            RequireApplicable(target, property);

            RevvyJson previous = Read(target, property);
            UnityEngine.Object owner = OwnerOf(target, property);

            // Contract §7.3: a no-op still succeeds but records no undo step and does not
            // dirty a scene the caller has not actually changed. Nothing was written, so
            // nothing was overridden by this call (Tier-15 §2.1).
            if (ValuesEqual(property, previous, value))
            {
                return BuildSetResult(targetId, target, property, previous, false, false);
            }

            // Tier-15 §2.1: "created" is a before/after question. Sampling only after the
            // write would report true for a property that was already overridden.
            bool overrodeBefore = ReportsOverride(target, property, owner);

            int group = BeginUndoGroup("Revvy set " + property);
            Undo.RecordObject(owner, "Revvy set " + property);
            Write(target, property, value);

            // Editing a prefab instance produces a local override, exactly as the
            // inspector would (contract §1.4). The instance stays connected.
            bool prefabOverride = PrefabUtility.IsPartOfPrefabInstance(owner);
            if (prefabOverride)
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(owner);
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            bool overrodeAfter = ReportsOverride(target, property, owner);
            return BuildSetResult(
                targetId, target, property, previous, true, !overrodeBefore && overrodeAfter);
        }

        /// <summary>
        /// Tier-15 §4: override-ness comes from the same predicate `manage_prefab` uses to
        /// decide what it lists and reverts, so the two tools cannot disagree.
        ///
        /// Unity classifies an instance root's position and rotation as permanent default
        /// overrides but its **scale** as a real one, so this is asked per property rather
        /// than inferred from the node being an instance root (Tier-15 §1.1).
        /// </summary>
        private static bool ReportsOverride(
            GameObject target, string property, UnityEngine.Object owner)
        {
            if (owner == null || !PrefabUtility.IsPartOfPrefabInstance(owner))
            {
                return false;
            }

            return RevvyManagePrefabTool.HasOverrideFor(target, property);
        }

        private static RevvyJson BuildSetResult(
            string targetId,
            GameObject target,
            string property,
            RevvyJson previous,
            bool changed,
            bool overrideCreated)
        {
            UnityEngine.Object owner = OwnerOf(target, property);

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "set")
                .Set("target_id", targetId)
                .Set("property", property)
                .Set("type", TypeOf(property))
                .Set("previous_value", previous)
                // Read back from the engine, not echoed from the request: engines
                // quantise floats and the caller must see what was stored (contract §10).
                .Set("value", Read(target, property))
                .Set("changed", changed)
                // Tier-15 §2: membership, not effect. Kept unchanged because callers
                // already read it; `override_created` is the field that answers what the
                // name looks like it answers.
                .Set("prefab_override", PrefabUtility.IsPartOfPrefabInstance(owner))
                .Set("override_created", overrideCreated);
        }

        // ------------------------------------------------------------------ guards

        private static Scene RequireEditedScene()
        {
            if (RevvyEditorStatusTool.IsPlayModeActiveOrTransitioning())
            {
                throw PropertyFailure(
                    "play_mode_active",
                    "Property access is unavailable while play mode is active or transitioning. " +
                    "Opaque IDs mean different things in the play world, and edits there are " +
                    "discarded when play stops.");
            }

            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                throw PropertyFailure(
                    "no_edited_scene", "Property access is unavailable while editing a Prefab Stage.");
            }

            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
            {
                throw PropertyFailure("no_edited_scene", "There is no currently edited scene.");
            }

            RevvyInspectSceneTool.BindEmissionScene(scene);
            return scene;
        }

        private static GameObject RequireTarget(Scene scene, string targetId)
        {
            if (string.IsNullOrEmpty(targetId))
            {
                throw PropertyFailure("invalid_argument", "Argument 'target_id' is required.");
            }

            GameObject target = RevvyInspectSceneTool.ResolveEmittedId(targetId, scene);
            if (target == null)
            {
                throw PropertyFailure(
                    "invalid_target_id",
                    "target_id '" + targetId + "' does not identify a live GameObject previously " +
                    "returned for the currently edited scene. Re-run inspect_scene to reacquire it.");
            }

            return target;
        }

        // ------------------------------------------------------------------ vocabulary

        private static string RequireProperty(string property)
        {
            if (string.IsNullOrEmpty(property))
            {
                throw PropertyFailure("invalid_argument", "Argument 'property' is required for this action.");
            }

            if (Array.IndexOf(SupportedProperties, property) < 0)
            {
                throw PropertyFailure(
                    "invalid_argument",
                    "Unknown property '" + property + "'. Engine property paths are never accepted; " +
                    "expected one of: " + string.Join(", ", SupportedProperties) + ".");
            }

            return property;
        }

        private static void RejectUnused(string action, string argumentName, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                throw PropertyFailure(
                    "invalid_argument",
                    "Argument '" + argumentName + "' is not used by action '" + action + "'; omit it.");
            }
        }

        private static void RejectUnusedValue(string action, RevvyJson value)
        {
            if (value != null && !value.IsNull)
            {
                throw PropertyFailure(
                    "invalid_argument",
                    "Argument 'value' is not used by action '" + action + "'; omit it.");
            }
        }

        internal static string TypeOf(string property)
        {
            switch (property)
            {
                case "transform.position":
                case "transform.rotation":
                case "transform.scale":
                    return TypeVector3;
                case "light.color":
                    return TypeColor;
                case "light.shadows_enabled":
                case "camera.orthographic":
                    return TypeBool;
                default:
                    return TypeFloat;
            }
        }

        // ------------------------------------------------------------------ applicability

        private static bool IsApplicable(GameObject target, string property)
        {
            if (property.StartsWith("transform.", StringComparison.Ordinal))
            {
                return true;
            }

            if (property.StartsWith("camera.", StringComparison.Ordinal))
            {
                return target.GetComponent<Camera>() != null;
            }

            Light light = target.GetComponent<Light>();
            if (light == null)
            {
                return false;
            }

            // Contract §1.3: Unity accepts range on a directional light, reads it back,
            // and does nothing with it. Refuse rather than report a successful no-op.
            if (property == "light.range")
            {
                return light.type == LightType.Point || light.type == LightType.Spot;
            }

            return true;
        }

        private static void RequireApplicable(GameObject target, string property)
        {
            if (IsApplicable(target, property))
            {
                return;
            }

            if (property == "light.range")
            {
                throw PropertyFailure(
                    "property_not_applicable",
                    "light.range has no effect on a " + target.GetComponent<Light>().type +
                    " light; Unity would store the value and ignore it.");
            }

            string component = property.StartsWith("camera.", StringComparison.Ordinal) ? "Camera" : "Light";
            throw PropertyFailure(
                "property_not_applicable",
                "'" + property + "' needs a " + component + " on '" + target.name +
                "', which has none. Use manage_properties action=list to see what applies.");
        }

        internal static UnityEngine.Object OwnerOf(GameObject target, string property)
        {
            if (property.StartsWith("transform.", StringComparison.Ordinal))
            {
                return target.transform;
            }

            if (property.StartsWith("camera.", StringComparison.Ordinal))
            {
                return target.GetComponent<Camera>();
            }

            return target.GetComponent<Light>();
        }

        // ------------------------------------------------------------------ read / write

        internal static RevvyJson Read(GameObject target, string property)
        {
            switch (property)
            {
                case "transform.position":
                    return Vector3Json(target.transform.localPosition);
                case "transform.rotation":
                    return Vector3Json(target.transform.localEulerAngles);
                case "transform.scale":
                    return Vector3Json(target.transform.localScale);
                case "light.color":
                    return ColorJson(target.GetComponent<Light>().color);
                case "light.intensity":
                    return RevvyJson.Number(target.GetComponent<Light>().intensity);
                case "light.range":
                    return RevvyJson.Number(target.GetComponent<Light>().range);
                case "light.shadows_enabled":
                    return RevvyJson.Bool(target.GetComponent<Light>().shadows != LightShadows.None);
                case "camera.field_of_view":
                    return RevvyJson.Number(target.GetComponent<Camera>().fieldOfView);
                case "camera.near_clip":
                    return RevvyJson.Number(target.GetComponent<Camera>().nearClipPlane);
                case "camera.far_clip":
                    return RevvyJson.Number(target.GetComponent<Camera>().farClipPlane);
                case "camera.orthographic":
                    return RevvyJson.Bool(target.GetComponent<Camera>().orthographic);
                default:
                    throw PropertyFailure(
                        "editor_api_failure", "Property '" + property + "' has no Unity read mapping.");
            }
        }

        private static void Write(GameObject target, string property, RevvyJson value)
        {
            switch (property)
            {
                case "transform.position":
                    target.transform.localPosition = RequireVector3(property, value);
                    return;
                case "transform.rotation":
                    target.transform.localEulerAngles = RequireVector3(property, value);
                    return;
                case "transform.scale":
                    target.transform.localScale = RequireVector3(property, value);
                    return;
                case "light.color":
                    target.GetComponent<Light>().color = RequireColor(property, value);
                    return;
                case "light.intensity":
                    target.GetComponent<Light>().intensity =
                        RequireFloat(property, value, 0f, float.MaxValue, ">= 0");
                    return;
                case "light.range":
                    target.GetComponent<Light>().range =
                        RequireFloat(property, value, float.Epsilon, float.MaxValue, "> 0");
                    return;
                case "light.shadows_enabled":
                    target.GetComponent<Light>().shadows =
                        RequireBool(property, value) ? LightShadows.Soft : LightShadows.None;
                    return;
                case "camera.field_of_view":
                    target.GetComponent<Camera>().fieldOfView =
                        RequireFloat(property, value, 1f, 179f, "between 1 and 179");
                    return;
                case "camera.near_clip":
                    target.GetComponent<Camera>().nearClipPlane =
                        RequireFloat(property, value, float.Epsilon, float.MaxValue, "> 0");
                    return;
                case "camera.far_clip":
                    target.GetComponent<Camera>().farClipPlane =
                        RequireFloat(property, value, float.Epsilon, float.MaxValue, "> 0");
                    return;
                case "camera.orthographic":
                    target.GetComponent<Camera>().orthographic = RequireBool(property, value);
                    return;
                default:
                    throw PropertyFailure(
                        "editor_api_failure", "Property '" + property + "' has no Unity write mapping.");
            }
        }

        // ------------------------------------------------------------------ value model

        private static float RequireFloat(
            string property, RevvyJson value, float minimum, float maximum, string bound)
        {
            if (value.Kind != RevvyJsonKind.Number)
            {
                throw TypeMismatch(property, TypeFloat, "a JSON number");
            }

            double raw = value.NumberValue;
            if (raw < minimum || raw > maximum)
            {
                throw PropertyFailure(
                    "value_out_of_range",
                    "'" + property + "' must be " + bound + "; received " +
                    raw.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            return (float)raw;
        }

        private static bool RequireBool(string property, RevvyJson value)
        {
            // Strict: 0/1 and "true" are not booleans (contract §10).
            if (value.Kind != RevvyJsonKind.Bool)
            {
                throw TypeMismatch(property, TypeBool, "a JSON boolean");
            }

            return value.BoolValue;
        }

        private static Vector3 RequireVector3(string property, RevvyJson value)
        {
            if (!value.IsObject)
            {
                throw TypeMismatch(property, TypeVector3, "an object with x, y and z");
            }

            return new Vector3(
                RequireComponent(property, value, "x", TypeVector3),
                RequireComponent(property, value, "y", TypeVector3),
                RequireComponent(property, value, "z", TypeVector3));
        }

        private static Color RequireColor(string property, RevvyJson value)
        {
            if (!value.IsObject)
            {
                throw TypeMismatch(property, TypeColor, "an object with r, g, b and a");
            }

            return new Color(
                RequireColorComponent(property, value, "r"),
                RequireColorComponent(property, value, "g"),
                RequireColorComponent(property, value, "b"),
                RequireColorComponent(property, value, "a"));
        }

        /// <summary>
        /// A missing field is never defaulted: a caller that sent {"x":1,"y":2} for a
        /// vector3 meant something it did not say, and guessing z is exactly the
        /// unannounced substitution this contract refuses (§10).
        /// </summary>
        private static float RequireComponent(
            string property, RevvyJson value, string field, string type)
        {
            RevvyJson component = value.Get(field);
            if (component == null || component.Kind != RevvyJsonKind.Number)
            {
                throw TypeMismatch(
                    property, type, "a number for '" + field + "'");
            }

            return (float)component.NumberValue;
        }

        private static float RequireColorComponent(string property, RevvyJson value, string field)
        {
            float component = RequireComponent(property, value, field, TypeColor);
            if (component < 0f || component > 1f)
            {
                throw PropertyFailure(
                    "value_out_of_range",
                    "Colour component '" + field + "' of '" + property + "' must be between 0 and 1; " +
                    "received " + component.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
            }

            return component;
        }

        private static bool ValuesEqual(string property, RevvyJson current, RevvyJson requested)
        {
            // Compared through the serialized form so float quantisation is handled the
            // same way on both sides of the comparison.
            try
            {
                switch (TypeOf(property))
                {
                    case TypeBool:
                        return requested.Kind == RevvyJsonKind.Bool &&
                               current.AsBool() == requested.AsBool();
                    case TypeFloat:
                        return requested.Kind == RevvyJsonKind.Number &&
                               Mathf.Approximately((float)current.AsDouble(), (float)requested.AsDouble());
                    case TypeVector3:
                        return requested.IsObject &&
                               current.ToJson(false) == Vector3Json(RequireVector3(property, requested)).ToJson(false);
                    default:
                        return requested.IsObject &&
                               current.ToJson(false) == ColorJson(RequireColor(property, requested)).ToJson(false);
                }
            }
            catch (RevvyToolException)
            {
                // A malformed value is not "equal"; the write path reports it properly.
                return false;
            }
        }

        private static RevvyJson Vector3Json(Vector3 vector)
        {
            return RevvyJson.Object()
                .Set("x", vector.x)
                .Set("y", vector.y)
                .Set("z", vector.z);
        }

        private static RevvyJson ColorJson(Color color)
        {
            return RevvyJson.Object()
                .Set("r", color.r)
                .Set("g", color.g)
                .Set("b", color.b)
                .Set("a", color.a);
        }

        // ------------------------------------------------------------------ undo

        private static int BeginUndoGroup(string label)
        {
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(label);
            return group;
        }

        private static RevvyToolException TypeMismatch(string property, string type, string expected)
        {
            return PropertyFailure(
                "value_type_mismatch",
                "'" + property + "' is of type " + type + " and needs " + expected + ".");
        }

        private static RevvyToolException PropertyFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }
    }
}
