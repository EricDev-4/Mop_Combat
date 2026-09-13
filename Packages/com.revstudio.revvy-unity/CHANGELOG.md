# Changelog

All notable changes to the Revvy Unity Bridge are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and the package uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.0] - 2026-09-06

Protocol change (c7f40af, with the Godot bridge following in the same wave):
the hop between the Revvy proxy and the editor is now identified and signed.
A 0.1.0 listener neither publishes a `bridge_instance_id` nor verifies a
signature, so the proxy and Reddy refuse it - Reddy reports a bridge whose
runtime config carries no instance id as outdated regardless of the version
it was copied in under.

### Added

- `bridge_instance_id`, minted per domain reload and published in
  `revvy-proxy.json`, `ping`, and `GET /health`, so the proxy can reject a
  stale or foreign listener (contract §1, §2).
- Signed proxy-to-editor requests (`hmac-sha1-exact-v2`) verified by
  `RevvyBridgeAuth`, with clock-skew and replay windows; the bearer token is
  left empty on purpose because the signed hop needs no wire token (contract §3).
- Project-scoped listener ports: the bridge binds the port its own project
  claims instead of a fixed 8088, with `REVVY_EDITOR_PORT` / `-revvyEditorPort`
  as the explicit override.

### Changed

- The version is now guarded: `Scripts/CI/bridge_protocol_version.py` records
  a fingerprint of `Editor/Server/*.cs` and fails CI when those change without
  a bump to `package.json` and `RevvyMcpHandler.ServerVersion` together.

## [0.1.0] - Unreleased

Initial skeleton. Implements the Tier-1 surface of
`docs/design/multi-engine-bridge-contract.md`.

### Added

- Project-scoped loopback MCP bridge, with `REVVY_EDITOR_PORT` /
  `-revvyEditorPort` override and signed proxy-to-editor requests (contract §1, §3).
- JSON-RPC 2.0 method set: `initialize`, `ping`, `tools/list`, `tools/call`,
  plus `notifications/*` acknowledgement (contract §3).
- Main-thread dispatcher pumped from `EditorApplication.update` — every Unity
  API call is marshalled through it (contract §8.2).
- `[RevvyTool]` / `[RevvyToolParam]` attributes with reflection-driven JSON
  Schema generation (contract §8.2).
- Tier-1 tools: `execute_script`, `manage_asset`, `read_logs`, `editor_status`
  (contract §4).
- First Tier-2 tool: asynchronous `manage_play_mode` with `status`, `start`,
  and `stop` actions plus bounded transition reporting.
- Read-only Tier-2 `inspect_scene` with bounded pre-order traversal, pagination,
  opaque GameObject IDs, strict schema validation, and stable error codes.
- Tier-11 `manage_prefab`: `list_overrides`/`revert`/`apply` over prefab instance
  overrides. Enumeration comes from `GetObjectOverrides` plus default-override
  filtering rather than raw `GetPropertyModifications`, which reports eleven
  phantom entries on a pristine instance; a Vector3 collapses to one logical row;
  modifications are mapped from the prefab asset back to the scene instance so
  values and opaque IDs refer to the instance. `revert` is one undo step, `apply`
  writes the asset and is declared non-undoable, nested instances resolve to the
  nearest asset, and `/mcp/safe` exposes only `list_overrides`. See
  `docs/design/tier11-prefab-overrides-contract.md`.
- Tier-10 `capture_viewport action inline`: returns the PNG as a base64 `image`
  content block alongside the existing text block, with its own 1 MB limit
  (checked before the file is written) and a reserved `_content_blocks` payload
  key that the dispatcher lifts out so the data is never carried twice. Verified
  end to end through a real `revvy-proxy` process, including `_fields` shaping
  and `/mcp/safe`, by SHA-256 against the written file — no proxy change was
  needed. Defaults to off, so Tier-6 callers are unaffected. See
  `docs/design/tier10-inline-image-contract.md`.
- Tier-9 play-mode `pause`, `resume`, and `step` on `manage_play_mode`, with a
  bounded `frames` argument (1–60). Steps are queued and pumped one per editor
  tick, `status` gains `state.paused`, `state.pending_steps`, and a
  `capabilities` object, and all three actions require play mode because Unity
  accepts them in edit mode and silently does nothing. Simulated input delivered
  while paused lands on the next stepped frame, which makes Tier-7 deterministic.
  Additive to Tier-2: every existing action and result field is unchanged. See
  `docs/design/tier9-play-pause-step-contract.md`.
- Tier-8 `manage_properties`: `list`/`get`/`set` over a curated engine-neutral
  property vocabulary (transform, light, camera) on objects in the edited scene.
  One `set` is one undo step, no-op sets report `changed: false`, prefab
  instances get a recorded local override, and `light.range` on a directional
  light is refused because Unity would store it and ignore it. Component
  attach/detach and prefab override management stay out of scope. See
  `docs/design/tier8-node-properties-contract.md`.
- Tier-7 `simulate_input`: keyboard and mouse simulation into the running game
  during play mode, with a closed engine-neutral key vocabulary and discrete
  state-change actions. Unity's legacy Input Manager cannot be driven at all, so
  the integration ships as an **optional** assembly gated on
  `com.unity.inputsystem` via `versionDefines` — the package keeps zero
  dependencies, and projects without it get `input_backend_unavailable` plus a
  `backend` object naming what to install. `REVVY_INPUT_BACKEND=none` disables
  simulation. See `docs/design/tier7-simulated-input-contract.md`.
- Tier-6 `capture_viewport`: renders the last active Scene view to a PNG under
  `<project>/.revvy/captures/` (outside the asset database, size- and
  count-bounded) and returns a project-relative path. Refuses with
  `viewport_unavailable` rather than returning the null device's uniformly grey
  placeholder, and reports a `renderer` object on both success and that failure.
  See `docs/design/tier6-screenshot-contract.md`.
- Tier-5 typed creation: `mutate_scene action=create` accepts `node_type` values
  `object`, `camera`, `light_point`, and `light_directional`, attaching exactly
  one mapped component inside the existing single undo group and echoing the
  resolved `node_type` in the result. No new error code and no new argument. See
  `docs/design/tier5-typed-creation-contract.md`.
- Tier-4 `mutate_scene action=instantiate`: places an `Assets/….prefab` into the
  edited scene via `PrefabUtility.InstantiatePrefab`, optionally under a
  `parent_id` and with a `name` override applied inside the same undo group.
  Adds the `asset_path` argument and the `asset_not_found` error code. See
  `docs/design/tier4-instantiation-contract.md`.
- Tier-3 `mutate_scene` with `create`, `rename`, `reparent`, and `delete`
  actions, one collapsed undo group per call, `changed: false` no-op semantics,
  rejection of arguments an action does not use, and the stable error codes in
  `docs/design/tier3-scene-mutation-contract.md` §6.
- Startup writer for `revvy-proxy.json` and `tools-manifest.json`
  (atomic read-modify-write, unknown fields preserved) (contract §2).
- `Revvy/Status` editor window.
- Headless entry point `RevStudio.Revvy.Editor.RevvyBridgeCli.StartFromCommandLine`
  for `-batchmode -nographics -executeMethod` (contract §8.2).
- Licensed Unity 6000.3.8f1 direct-bridge E2E covering package compilation,
  MCP handshake/descriptors, duplicate-name hierarchy inspection and paging,
  real start/status/stop transitions across domain reloads, and the full
  create → inspect → rename → reparent → delete → inspect mutation sequence
  with undo/redo sanity and every Tier-3 error code, and the Tier-4
  instantiation sequence against a prefab the fixture builds for itself.

- `RevvyToolException` can now carry a `Details` payload, merged into the MCP
  error result as sibling fields. Reserved envelope keys are never overwritten,
  so a tool cannot forge `success` or a different `error_code`. Used by
  `viewport_unavailable` to report *why* the viewport is unusable.

### Changed

- The emitted opaque-ID registry now prunes dead IDs on `hierarchyChanged`
  instead of wiping every ID. A bridge-authored edit no longer invalidates the
  IDs of objects that survived it, which is what makes `create` → `rename`
  possible; Unity raises `hierarchyChanged` after the mutating call has already
  returned its ID, so a wipe was an unwinnable race. Resolution still validates
  liveness and scene membership on every call. See
  `docs/design/tier3-scene-mutation-contract.md` §4.1.

### Fixed

- Expected worker-thread cancellation during play-mode domain reload no longer
  appears as duplicate tool and MCP dispatch errors in the Unity log.

### Known gaps

- Remaining Tier-2 tools (scene/GameObject mutation and screenshot capture) are
  not implemented — see contract §4.
- `execute_script` supports reflection-based invocation only; Roslyn dynamic
  compilation is stubbed behind `REVVY_UNITY_ALLOW_DYNAMIC_COMPILE`.
