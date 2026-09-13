using System;
using System.Collections.Generic;
using RevStudio.Revvy.Editor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace RevStudio.Revvy.Editor.InputSystemBackend
{
    /// <summary>
    /// Input System driver for <c>simulate_input</c>
    /// (docs/design/tier7-simulated-input-contract.md §10).
    ///
    /// This whole assembly exists only when <c>com.unity.inputsystem</c> is installed —
    /// see the asmdef's <c>versionDefines</c> plus matching <c>defineConstraints</c>.
    /// That keeps the bridge package dependency-free while still supporting projects
    /// that do have the package. <see cref="RevvyInputBackends"/> finds this type by
    /// reflection, because the main assembly cannot reference an assembly that may not
    /// exist.
    /// </summary>
    public sealed class RevvyInputSystemBackend : IRevvyInputBackend
    {
        /// <summary>
        /// Keys this bridge is currently holding down.
        ///
        /// Load-bearing: Unity keyboard state events carry the **whole device state**, so
        /// queueing only the newest key would silently release every other one. Godot's
        /// per-event model needs no equivalent, and the contract hides the difference.
        /// </summary>
        private readonly HashSet<Key> _heldKeys = new HashSet<Key>();

        private readonly HashSet<string> _heldButtons = new HashSet<string>(StringComparer.Ordinal);
        private Vector2 _pointer;
        private bool _pointerInitialised;

        /// <summary>Contract §9 neutral name to Unity key. Closed set.</summary>
        private static readonly Dictionary<string, Key> KeyMap = BuildKeyMap();

        public string Name
        {
            get { return "unity-input-system"; }
        }

        public string Detail
        {
            get { return "com.unity.inputsystem via InputSystem.QueueStateEvent"; }
        }

        public IReadOnlyList<string> Devices
        {
            get
            {
                List<string> devices = new List<string>();
                if (Keyboard.current != null)
                {
                    devices.Add("Keyboard");
                }

                if (Mouse.current != null)
                {
                    devices.Add("Mouse");
                }

                return devices;
            }
        }

        public void GetScreenSize(out int width, out int height)
        {
            width = Screen.width;
            height = Screen.height;
        }

        public void SetKey(string key, bool pressed)
        {
            Key mapped;
            if (!KeyMap.TryGetValue(key, out mapped))
            {
                // The tool validates the vocabulary first, so reaching here means the map
                // and the contract have drifted apart.
                throw new RevvyToolException(
                    "Key '" + key + "' has no Unity Input System mapping.", "editor_api_failure");
            }

            if (pressed)
            {
                _heldKeys.Add(mapped);
            }
            else
            {
                _heldKeys.Remove(mapped);
            }

            Keyboard keyboard = EnsureKeyboard();
            Key[] held = new Key[_heldKeys.Count];
            _heldKeys.CopyTo(held);

            // Whole-device state: every key still held is re-asserted on every event.
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(held));
        }

        public void SetMouseButton(string button, bool pressed)
        {
            if (pressed)
            {
                _heldButtons.Add(button);
            }
            else
            {
                _heldButtons.Remove(button);
            }

            QueueMouseState();
        }

        public void MoveMouse(int x, int y)
        {
            // Contract space is top-left origin; Unity's Mouse.position is bottom-left.
            int height = Screen.height;
            float flipped = height > 0 ? height - y : y;
            _pointer = new Vector2(x, flipped);
            _pointerInitialised = true;
            QueueMouseState();
        }

        // ------------------------------------------------------------------ internals

        private void QueueMouseState()
        {
            Mouse mouse = EnsureMouse();

            if (!_pointerInitialised)
            {
                _pointer = mouse.position.ReadValue();
                _pointerInitialised = true;
            }

            MouseState state = new MouseState { position = _pointer };
            if (_heldButtons.Contains("left"))
            {
                state = state.WithButton(MouseButton.Left, true);
            }

            if (_heldButtons.Contains("right"))
            {
                state = state.WithButton(MouseButton.Right, true);
            }

            if (_heldButtons.Contains("middle"))
            {
                state = state.WithButton(MouseButton.Middle, true);
            }

            InputSystem.QueueStateEvent(mouse, state);
        }

        /// <summary>
        /// Headless and batch-mode editors have no real devices, so the virtual path is
        /// the normal one under automation rather than a fallback (contract §10).
        /// </summary>
        private static Keyboard EnsureKeyboard()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                keyboard = InputSystem.AddDevice<Keyboard>();
            }

            if (keyboard == null)
            {
                throw new RevvyToolException(
                    "Unity provided no keyboard device to simulate on.", "editor_api_failure");
            }

            return keyboard;
        }

        private static Mouse EnsureMouse()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null)
            {
                mouse = InputSystem.AddDevice<Mouse>();
            }

            if (mouse == null)
            {
                throw new RevvyToolException(
                    "Unity provided no mouse device to simulate on.", "editor_api_failure");
            }

            return mouse;
        }

        private static Dictionary<string, Key> BuildKeyMap()
        {
            Dictionary<string, Key> map = new Dictionary<string, Key>(StringComparer.Ordinal);

            for (int i = 0; i < 26; i++)
            {
                map[((char)('a' + i)).ToString()] = (Key)((int)Key.A + i);
            }

            for (int i = 0; i < 10; i++)
            {
                map[i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    (Key)((int)Key.Digit0 + i);
            }

            map["space"] = Key.Space;
            map["enter"] = Key.Enter;
            map["escape"] = Key.Escape;
            map["tab"] = Key.Tab;
            map["backspace"] = Key.Backspace;
            map["delete"] = Key.Delete;
            map["up"] = Key.UpArrow;
            map["down"] = Key.DownArrow;
            map["left"] = Key.LeftArrow;
            map["right"] = Key.RightArrow;

            // Contract §9: modifier names are unsided and resolve to the left key.
            map["shift"] = Key.LeftShift;
            map["ctrl"] = Key.LeftCtrl;
            map["alt"] = Key.LeftAlt;

            return map;
        }
    }
}
