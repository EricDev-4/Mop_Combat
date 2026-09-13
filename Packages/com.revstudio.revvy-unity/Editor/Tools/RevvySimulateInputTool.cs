using System;
using System.Collections.Generic;
using UnityEditor;

namespace RevStudio.Revvy.Editor
{
    /// <summary>
    /// Tier-7 simulated input (docs/design/tier7-simulated-input-contract.md).
    ///
    /// Always present in <c>tools/list</c> with an identical descriptor on every
    /// project, even where nothing can drive input. The descriptor is a shared
    /// artefact — it is written into <c>tools-manifest.json</c> and contract-checked —
    /// so it must not change shape based on which packages the host project happens
    /// to have (contract §1.3). What varies is the *backend*, resolved per call.
    /// </summary>
    public static class RevvySimulateInputTool
    {
        private const int MaximumCoordinate = 16384;

        /// <summary>Contract §9. Closed set; adding a value is a contract change.</summary>
        private static readonly string[] SupportedKeys =
        {
            "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
            "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
            "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
            "space", "enter", "escape", "tab", "backspace", "delete",
            "up", "down", "left", "right",
            "shift", "ctrl", "alt"
        };

        private static readonly string[] SupportedButtons = { "left", "right", "middle" };

        [RevvyTool(
            "simulate_input",
            "Send simulated keyboard and mouse input to the running game during play mode.",
            Title = "Simulate input",
            ReadOnlyActions = new[] { "status" },
            StrictSchema = true,
            DefaultErrorCode = "editor_api_failure")]
        public static RevvyJson SimulateInput(
            [RevvyToolParam(
                "Input event to deliver, or status to report backend availability without sending anything.",
                Enum = new[]
                {
                    "status", "key_down", "key_up", "mouse_move", "mouse_button_down", "mouse_button_up"
                })]
            string action,
            [RevvyToolParam(
                "For key_down and key_up: engine-neutral key name from the curated vocabulary.",
                Enum = new[]
                {
                    "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m",
                    "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z",
                    "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
                    "space", "enter", "escape", "tab", "backspace", "delete",
                    "up", "down", "left", "right",
                    "shift", "ctrl", "alt"
                })]
            string key = "",
            [RevvyToolParam(
                "For mouse_button_down and mouse_button_up: which mouse button.",
                Enum = new[] { "left", "right", "middle" })]
            string button = "",
            [RevvyToolParam(
                "For mouse_move: pointer X in game-view pixels, origin top-left.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":0,\"maximum\":16384}")]
            int x = 0,
            [RevvyToolParam(
                "For mouse_move: pointer Y in game-view pixels, origin top-left, increasing downward.",
                SchemaJson = "{\"type\":\"integer\",\"minimum\":0,\"maximum\":16384}")]
            int y = 0)
        {
            try
            {
                string normalized = (action ?? string.Empty).Trim().ToLowerInvariant();

                // Contract §8: arguments are validated before the guards, so a misspelled
                // key is reported whether or not the editor happens to be playing — that
                // bug survives entering play mode.
                switch (normalized)
                {
                    case "status":
                        RejectUnused(normalized, "key", key);
                        RejectUnused(normalized, "button", button);
                        return Status();

                    case "key_down":
                    case "key_up":
                        RejectUnused(normalized, "button", button);
                        return DeliverKey(normalized, RequireKey(key), normalized == "key_down");

                    case "mouse_move":
                        RejectUnused(normalized, "key", key);
                        RejectUnused(normalized, "button", button);
                        return DeliverMouseMove(x, y);

                    case "mouse_button_down":
                    case "mouse_button_up":
                        RejectUnused(normalized, "key", key);
                        return DeliverMouseButton(
                            normalized, RequireButton(button), normalized == "mouse_button_down");

                    default:
                        throw InputFailure(
                            "invalid_argument",
                            "Unknown action '" + action + "'. Expected one of: status, key_down, key_up, " +
                            "mouse_move, mouse_button_down, mouse_button_up.");
                }
            }
            catch (RevvyToolException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new RevvyToolException(
                    "Simulated input failed: " + RevvyLog.Describe(exception),
                    "editor_api_failure",
                    exception);
            }
        }

        // ------------------------------------------------------------------ actions

        /// <summary>Contract §5: the one action that answers a capability question, so it works in both modes.</summary>
        private static RevvyJson Status()
        {
            IRevvyInputBackend backend = RevvyInputBackends.Resolve();

            int width = 0;
            int height = 0;
            if (backend != null)
            {
                backend.GetScreenSize(out width, out height);
            }

            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", "status")
                .Set("play_mode", EditorApplication.isPlaying)
                .Set("backend", DescribeBackend(backend))
                .Set("screen", RevvyJson.Object()
                    .Set("width", width)
                    .Set("height", height));
        }

        private static RevvyJson DeliverKey(string action, string key, bool pressed)
        {
            IRevvyInputBackend backend = RequirePlayModeBackend();
            backend.SetKey(key, pressed);
            return BuildDelivered(action, backend, RevvyJson.Object().Set("key", key));
        }

        private static RevvyJson DeliverMouseButton(string action, string button, bool pressed)
        {
            IRevvyInputBackend backend = RequirePlayModeBackend();
            backend.SetMouseButton(button, pressed);
            return BuildDelivered(action, backend, RevvyJson.Object().Set("button", button));
        }

        private static RevvyJson DeliverMouseMove(int x, int y)
        {
            if (x < 0 || x > MaximumCoordinate || y < 0 || y > MaximumCoordinate)
            {
                throw InputFailure(
                    "invalid_argument",
                    "x and y must be between 0 and " + MaximumCoordinate + ".");
            }

            IRevvyInputBackend backend = RequirePlayModeBackend();
            backend.MoveMouse(x, y);
            return BuildDelivered(
                "mouse_move", backend, RevvyJson.Object().Set("x", x).Set("y", y));
        }

        // ------------------------------------------------------------------ guards

        /// <summary>
        /// Contract §5 then §8: play mode first, then the backend. Input delivered in edit
        /// mode has nowhere to go — no game loop is consuming it — so accepting it would
        /// report success for an action that did nothing.
        /// </summary>
        private static IRevvyInputBackend RequirePlayModeBackend()
        {
            if (!EditorApplication.isPlaying)
            {
                throw InputFailure(
                    "play_mode_required",
                    "Simulated input requires play mode; nothing is consuming input in edit mode. " +
                    "Start play mode with manage_play_mode and retry.");
            }

            IRevvyInputBackend backend = RevvyInputBackends.Resolve();
            if (backend == null)
            {
                throw new RevvyToolException(
                    "No input backend is available. Unity can only simulate input through the Input " +
                    "System package; install com.unity.inputsystem and set Active Input Handling to " +
                    "\"Input System Package (New)\" or \"Both\".",
                    "input_backend_unavailable",
                    RevvyJson.Object().Set("backend", DescribeBackend(null)));
            }

            return backend;
        }

        // ------------------------------------------------------------------ validation

        private static void RejectUnused(string action, string argumentName, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                throw InputFailure(
                    "invalid_argument",
                    "Argument '" + argumentName + "' is not used by action '" + action + "'; omit it.");
            }
        }

        /// <summary>
        /// Contract §4: matched exactly, byte for byte. "A" and " space" are errors —
        /// accepting near-misses would make the closed vocabulary softer than it reads.
        /// </summary>
        private static string RequireKey(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw InputFailure("invalid_argument", "Argument 'key' is required for this action.");
            }

            if (Array.IndexOf(SupportedKeys, key) < 0)
            {
                throw InputFailure(
                    "invalid_argument",
                    "Unknown key '" + key + "'. Engine key identifiers are never accepted; expected one " +
                    "of the curated names, e.g. a-z, 0-9, space, enter, escape, tab, backspace, delete, " +
                    "up, down, left, right, shift, ctrl, alt.");
            }

            return key;
        }

        private static string RequireButton(string button)
        {
            if (string.IsNullOrEmpty(button))
            {
                throw InputFailure("invalid_argument", "Argument 'button' is required for this action.");
            }

            if (Array.IndexOf(SupportedButtons, button) < 0)
            {
                throw InputFailure(
                    "invalid_argument",
                    "Unknown button '" + button + "'. Expected one of: left, right, middle.");
            }

            return button;
        }

        // ------------------------------------------------------------------ payloads

        private static RevvyJson BuildDelivered(string action, IRevvyInputBackend backend, RevvyJson delivered)
        {
            return RevvyJson.Object()
                .Set("success", true)
                .Set("engine", RevvyEnv.EngineId)
                .Set("action", action)
                .Set("play_mode", EditorApplication.isPlaying)
                .Set("backend", DescribeBackend(backend))
                .Set("delivered", delivered);
        }

        private static RevvyJson DescribeBackend(IRevvyInputBackend backend)
        {
            if (backend == null)
            {
                return RevvyJson.Object()
                    .Set("available", false)
                    .Set("name", string.Empty)
                    .Set("detail", RevvyInputBackends.DescribeAbsence());
            }

            RevvyJson devices = RevvyJson.Array();
            IReadOnlyList<string> names = backend.Devices;
            if (names != null)
            {
                foreach (string device in names)
                {
                    devices.Add(RevvyJson.String(device));
                }
            }

            return RevvyJson.Object()
                .Set("available", true)
                .Set("name", backend.Name ?? string.Empty)
                .Set("detail", backend.Detail ?? string.Empty)
                .Set("devices", devices);
        }

        private static RevvyToolException InputFailure(string code, string message)
        {
            return new RevvyToolException(message, code);
        }
    }
}
