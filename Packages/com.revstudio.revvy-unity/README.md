# Revvy Unity Bridge

Editor-side MCP bridge that lets the Revvy proxy, CLI, and Reddy drive the
Unity Editor. Implements the Tier-1 surface of
[`docs/design/multi-engine-bridge-contract.md`](../../../docs/design/multi-engine-bridge-contract.md).

```
LLM client → project proxy:P+1 (/mcp | /mcp/safe) → unity bridge:P → Unity Editor
```

> **Status: 0.2.0 (signed hop).** Tier-1 tools are contract-complete; Tier-2
> `manage_play_mode` and read-only `inspect_scene`, Tier-3 `mutate_scene`
> (create/rename/reparent/delete), Tier-4 `mutate_scene action=instantiate`, and
> Tier-5 typed `create` (camera and lights), and Tier-6 `capture_viewport` are
> implemented, Tier-7 `simulate_input` covers keyboard and mouse, and Tier-8
> `manage_properties` covers curated property reads and writes, and Tier-9 adds
> play-mode pause/resume/step, and Tier-10 inline (base64) image results.
> Tier-11 prefab override management is implemented. Component attach/detach on
> existing objects remains. `execute_script` runs
> reflection and menu commands; free-form C# compilation is stubbed. Package
> compilation and direct MCP runtime E2E pass on Unity 6000.3.8f1; live proxy
> pairing remains unverified.

## Requirements

- Unity **6000.3 LTS or newer** (tested-baseline floor from contract §8.1).
- Windows, macOS, or Linux editor. No platform-native code.
- No package dependencies. The bridge uses only Unity built-ins and the BCL —
  in particular there is **no Newtonsoft.Json dependency**; JSON is handled by a
  small in-package DOM (`RevvyJson`).

## Install (git URL)

1. Unity → **Window ▸ Package Manager**.
2. **+ ▸ Install package from git URL…**
3. Paste:

   ```
   https://github.com/RevStudio/Revvy.git?path=Bridges/Unity/com.revstudio.revvy-unity
   ```

   Pin a tag or commit for reproducible installs:

   ```
   https://github.com/RevStudio/Revvy.git?path=Bridges/Unity/com.revstudio.revvy-unity#v0.2.0
   ```

Or add it to `Packages/manifest.json` directly:

```json
{
  "dependencies": {
    "com.revstudio.revvy-unity": "https://github.com/RevStudio/Revvy.git?path=Bridges/Unity/com.revstudio.revvy-unity#v0.2.0"
  }
}
```

Git must be on `PATH` for Package Manager to resolve a git URL.

For local development, use **+ ▸ Install package from disk…** and pick this
folder's `package.json`, or add a `file:` dependency.

## First run

On the next domain load the bridge starts automatically and:

1. reads the project's configured `ue_port` and binds its loopback MCP endpoint,
2. writes `revvy-proxy.json` and `tools-manifest.json` into the Revvy state
   directory (`<project>/.revvy` by default).

Add `.revvy/` to the project's `.gitignore`; runtime state does not belong in
source control.

Open **Revvy ▸ Status** to confirm. A quick out-of-editor check:

```powershell
$port = (Get-Content .revvy/revvy-proxy.json | ConvertFrom-Json).ue_port
Invoke-RestMethod "http://127.0.0.1:$port/health"
$body = @{jsonrpc='2.0'; id=1; method='tools/list'} | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$port/mcp" `
  -ContentType 'application/json' -Body $body
```

## Configuration

Every knob is settable by environment variable or editor CLI argument
(CLI wins). With no explicit bridge-port override, Revvy adopts `ue_port` from
the resolved runtime config so each project can use its own editor/proxy pair.

| Setting | Env | CLI | Default |
|---|---|---|---|
| Bridge port | `REVVY_EDITOR_PORT` | `-revvyEditorPort` | Config `ue_port`, then `8088` |
| State directory | `REVVY_STATE_DIR` | `-revvyStateDir` | `<project>/.revvy` |
| Config file | `REVVY_CONFIG` | `-revvyConfig` | `<state dir>/revvy-proxy.json` |
| Autostart | `REVVY_UNITY_AUTOSTART=0` disables | — | on |
| Dynamic C# compile | `REVVY_UNITY_ALLOW_DYNAMIC_COMPILE=1` | — | off (stub) |
| Verbose transport logs | `REVVY_UNITY_VERBOSE=1` | — | off |

Bridge-port resolution is `-revvyEditorPort` → `REVVY_EDITOR_PORT` → the
resolved `revvy-proxy.json`'s `ue_port` → `8088`. Invalid, out-of-range, or
unreadable values fall through. The config fallback is trusted only when
`port_scope="project"`, `engine="unity"`, and one of `project_path`,
`installed_project.path`, or `active_project.path` canonically matches the open
Unity project. After binding, the bridge writes the selected port back without
replacing proxy-owned or unknown config fields.

Both MCP `ping` and `GET /health` publish `engine`, canonical `project_path`,
the domain-session `bridge_instance_id`, and `editor_port`. The same instance ID
is written to `revvy-proxy.json`, allowing the proxy to reject a stale or foreign
editor listener.

## Tools

| Tool | Actions | Annotations |
|---|---|---|
| `editor_status` | — | read-only, idempotent |
| `inspect_scene` | bounded hierarchy query | read-only, idempotent |
| `manage_play_mode` | `status`, `start`, `stop`, `pause`, `resume`, `step` | destructive, open-world; `status` read-only |
| `capture_viewport` | — | read-only, idempotent |
| `simulate_input` | `status`, `key_down`, `key_up`, `mouse_move`, `mouse_button_down`, `mouse_button_up` | `status` read-only |
| `manage_prefab` | `list_overrides`, `revert`, `apply` | destructive; `list_overrides` read-only |
| `manage_properties` | `list`, `get`, `set` | `list`/`get` read-only |
| `mutate_scene` | `create`, `instantiate`, `rename`, `reparent`, `delete` | destructive |
| `manage_asset` | `search`, `list`, `info`, `import`, `refresh`, `move`, `delete`, `create_folder` | destructive; `search`/`list`/`info` read-only |
| `read_logs` | `tail`, `errors`, `filter`, `stats`, `clear` | idempotent; all but `clear` read-only |
| `execute_script` | `invoke`, `menu_item`, `describe`, `compile` | destructive, open-world; `describe` read-only |

`read_logs` only sees messages logged **after** the bridge loaded — Unity
exposes no API for the console window's own backlog, and a domain reload resets
the buffer.

`manage_asset` paths are project-relative and must start with `Assets/` or
`Packages/`; absolute paths and `..` are refused.

`manage_play_mode` schedules start/stop on the next editor update so the MCP
response can leave the socket before a domain reload. Follow the returned
`verify_with` call until `state.transitioning` becomes false. A deferred
busy-state failure, editor API exception, or 30-second transition timeout is
reported as an error by the next `status` call.

`manage_play_mode` also does `pause`, `resume`, and `step` (1–60 frames, default
1). All three need play mode — Unity accepts them in edit mode and does nothing,
leaving a flag that makes the next play session start paused, so the bridge
refuses with `play_mode_required`. Stepping implies pausing, one step advances
exactly one frame, and the game stays paused afterwards. Steps are queued and
pumped one per editor tick, so poll `status` until `state.pending_steps` is `0`.
`status` also reports `state.paused` and a `capabilities` object. The useful
combination: **pause → `simulate_input` → `step`** gives the game exactly one
frame with that input held, which is what makes `wasPressedThisFrame` reliable.
See `docs/design/tier9-play-pause-step-contract.md`.

`inspect_scene` returns a bounded depth-first snapshot of the active edited
scene. It supports opaque `unity:<instance-id>` re-rooting plus depth, offset,
and limit bounds, and refuses calls while play mode is active or transitioning.
Successful proxy calls may use `_fields`/`_omit`; those controls are stripped
before the bridge sees the request.

`mutate_scene` makes exactly one structural edit per call and is the only tool
here that dirties the scene; it never saves. Each call collapses into a single
editor undo step, so one undo reverts one call. IDs come from `inspect_scene` or
from a previous `mutate_scene` response, and an ID stays usable across further
mutations until its object is deleted, the scene changes, or the domain reloads.
A same-name rename or same-parent reparent succeeds with `changed: false` and
records no undo step. Arguments the chosen action does not use are rejected
rather than ignored. See `docs/design/tier3-scene-mutation-contract.md`.

`mutate_scene action=create` takes a `node_type` from a curated, engine-neutral
vocabulary: `object` (empty GameObject), `camera` (+ `Camera`), `light_point`
and `light_directional` (+ `Light`, type set explicitly). Exactly one component
is attached — no `AudioListener` alongside a camera — and no property is set
beyond the light's type, so everything else keeps Unity's defaults. The value is
matched exactly: `Camera`, `Camera3D`, and `" camera"` are all
`invalid_argument`. Raw engine class names never work here; that is
`capture_viewport` renders the last active Scene view to a PNG under
`<project>/.revvy/captures/` and returns a project-relative path for you to read.
It never opens a Scene view — if none is open, or if the editor is running
without a graphics device, it fails with `viewport_unavailable` and a `renderer`
object saying which. **It does not return a placeholder image.** Unity's
`-nographics` null device silently produces a valid, uniformly grey PNG, so
succeeding there would hand back a picture that looks real and is not. Captures
are capped at 4 MB each and the directory keeps the 20 most recent
`viewport-*.png` files; add `.revvy/` to your `.gitignore`. Pass `inline: true`
to also get the PNG back as a base64 `image` content block, so a caller that
cannot read the project's files still sees the picture — the file is still
written, and the inline bytes are byte-identical to it. Inline has its own,
lower 1 MB limit because base64 inflates by a third and the bytes land in a
model's context; over it you get `capture_too_large` and no file. Omitting
`inline` gives exactly the previous single-text-block result. Unlike the scene
tools, capture works during play mode, and `play_mode` in the result tells you
which world you are looking at. See `docs/design/tier6-screenshot-contract.md`.

`manage_properties` reads and writes a curated, engine-neutral property set on
objects already in the edited scene: `transform.position/rotation/scale` (local,
Euler degrees), `light.color/intensity/range/shadows_enabled`, and
`camera.field_of_view/near_clip/far_clip/orthographic`. Start with
`action: "list"` — it returns only the properties that apply to that object,
with their current values, so you never write blind. Unity serialized paths like
`m_Intensity` are always `invalid_argument`; use `execute_script` for anything
outside the vocabulary. One `set` is one undo step, a set to the current value
reports `changed: false` and records nothing, and editing a prefab instance
produces a proper local override (`prefab_override: true`) with the link intact.
`light.range` on a **directional** light is refused rather than silently stored —
Unity accepts that write and ignores it. Adding or removing components is still
out of scope. See `docs/design/tier8-node-properties-contract.md`.

`manage_prefab` enumerates, reverts, and applies the overrides a prefab instance
carries against its source asset. `list_overrides` reports **every** override the
engine sees, each with both a curated `property` name and the raw
`engine_property` path; `revert` accepts only the curated vocabulary, so raw
paths like `m_LocalPosition` stay an `execute_script` job. It never enumerates
from raw `GetPropertyModifications`: a pristine instance reports eleven
modifications and all eleven are Unity default overrides, and moving an instance
**root** is likewise not an override. `revert` is one undo step; **`apply` writes
the asset file and is declared non-undoable**. Model and immutable prefabs are
refused with `prefab_not_applyable`, and nested instances always resolve to the
*nearest* asset, which the result reports. On `/mcp/safe` the tool is listed but
only `list_overrides` runs. See `docs/design/tier11-prefab-overrides-contract.md`.

`simulate_input` sends keyboard and mouse input to the **running game** and is
refused outside play mode with `play_mode_required`. Unity can only do this
through the Input System package: the legacy Input Manager exposes no keyboard or
mouse injection API at all. The integration ships as an optional assembly that
compiles only when `com.unity.inputsystem` is installed, so **this package still
has no dependencies** — but on a project without it, every input action returns
`input_backend_unavailable` with a `backend` object explaining what to install.
Call `action: "status"` first (it works in edit mode) to find out. Actions are
discrete state changes with no `tap` convenience, because a press and release in
one call is invisible to `wasPressedThisFrame`-style code — send `key_down`, let
frames elapse, then `key_up`. Key names are a closed engine-neutral vocabulary
(`a`–`z`, `0`–`9`, `space`, `enter`, arrows, `shift`/`ctrl`/`alt`, …); engine
identifiers like `KeyCode.A` are always `invalid_argument`. Mouse coordinates are
game-view pixels with a **top-left** origin. Set `REVVY_INPUT_BACKEND=none` to
disable input simulation entirely. See
`docs/design/tier7-simulated-input-contract.md`.

Note that `inspect_scene` still reports
`type: "GameObject"` for every Unity object, so the `node_type` echo in the
result is the portable way to confirm what was made. See
`docs/design/tier5-typed-creation-contract.md`.

`mutate_scene action=instantiate` places a prefab into the edited scene.
`asset_path` must be a project-relative `Assets/….prefab`; a wrong extension, an
absolute path, a `..` escape, or a `Packages/` path is `invalid_argument`, while
a well-formed path with nothing at it is `asset_not_found`. An optional `name`
renames the instance root inside the same undo group, so one undo reverts the
whole call. The instance keeps its prefab connection — applying, reverting, and
unpacking overrides are out of scope, as are components and transform values.
See `docs/design/tier4-instantiation-contract.md`.

## Adding a tool

Any public static method in an editor assembly can become a tool. The signature
*is* the schema.

```csharp
using RevStudio.Revvy.Editor;

public static class MyTools
{
    [RevvyTool("count_prefabs", "Count prefabs under a folder.", ReadOnly = true, Idempotent = true)]
    public static RevvyJson CountPrefabs(
        [RevvyToolParam("Project-relative folder.")] string path = "Assets")
    {
        var guids = UnityEditor.AssetDatabase.FindAssets("t:Prefab", new[] { path });
        return RevvyJson.Object().Set("success", true).Set("count", guids.Length);
    }
}
```

Parameters without a C# default become `required`. The method body always runs
on the Unity main thread — never spawn your own thread and touch the Unity API
from it.

Restart the bridge (or press **Rewrite config** in **Revvy ▸ Status**) after
adding a tool so `tools-manifest.json` picks it up.

## Headless / CI

```bash
Unity -batchmode -nographics \
      -projectPath /path/to/Project \
      -executeMethod RevStudio.Revvy.Editor.RevvyBridgeCli.StartFromCommandLine
```

Do not pass `-quit` — the editor process must stay alive to serve the socket.
`RevvyBridgeCli.PublishConfigAndExit` writes the runtime files and exits, which
is enough to validate schema generation in CI without holding a port.

The repository also includes a licensed real-editor E2E. It creates a saved
scene with duplicate sibling names, exercises MCP handshake/schema, bounded
inspection/paging/re-rooting, starts and stops play mode across domain reloads,
and verifies the play-mode inspection guard:

```powershell
Tests\Unity\Run-BridgeE2E.ps1
```

Pass `-UnityPath` or `-Port` when the default Unity 6000.3.8f1 installation or
port `18088` is unavailable. The generated scene, state, and logs are ignored.

## Security posture

Same as the Unreal MCP server: loopback only. The listener binds
`127.0.0.1`, non-loopback peers are dropped, and a request carrying a
non-loopback `Origin` is rejected (DNS-rebinding guard). There is **no
authentication on this hop** — the proxy owns bearer-token enforcement, and
anything that can reach the configured `127.0.0.1:<ue_port>` can drive the
editor. Do not expose the port.

`execute_script` can do anything the editor can do, including delete assets.
Route untrusted prompts through the proxy's `/mcp/safe` endpoint, which filters
on the `readOnlyHint` / `destructiveHint` annotations this bridge publishes.

## Meta files

See [META-POLICY.md](META-POLICY.md). Short version: `.meta` files are **not**
committed in this skeleton and must be generated by a real editor before the
package is tagged for distribution.

## Layout

```
Editor/
  RevvyBridge.cs                 lifecycle: InitializeOnLoad, domain reload, CLI entry points
  Core/
    RevvyJson.cs                 dependency-free JSON DOM (parse + serialize)
    RevvyEnv.cs                  env/CLI resolution, state + config paths
    RevvyEditorFacts.cs          cached main-thread Unity facts
    RevvyMainThread.cs           EditorApplication.update dispatch queue
    RevvyLog.cs                  prefixed logging
  Server/
    RevvyEditorServer.cs         loopback TCP listener, routing, SSE, CORS
    RevvyHttpMessage.cs          HTTP/1.1 read/write + SSE framing
    RevvyMcpHandler.cs           JSON-RPC 2.0 / MCP method dispatch
  Registration/
    RevvyToolAttribute.cs        [RevvyTool] / [RevvyToolParam]
    RevvyToolRegistry.cs         reflection scan, JSON Schema, argument binding
  Tools/
    RevvyEditorStatusTool.cs     editor_status, manage_play_mode, inspect_scene
    RevvyMutateSceneTool.cs      mutate_scene
    RevvyManageAssetTool.cs      manage_asset
    RevvyReadLogsTool.cs         read_logs
    RevvyLogBuffer.cs            console ring buffer
    RevvyExecuteScriptTool.cs    execute_script
  Config/
    RevvyProxyConfigWriter.cs    revvy-proxy.json + tools-manifest.json (atomic RMW)
  UI/
    RevvyStatusWindow.cs         Revvy/Status panel
```

## Troubleshooting

**"Port … is already in use."** Confirm that this project has its own
`port_scope: "project"` pair in `.revvy/revvy-proxy.json`. Stop the stale owner,
or set `REVVY_EDITOR_PORT` and keep `ue_port` in that file aligned. Different
projects may run together when their persisted pairs differ.

**Proxy reports zero tools.** The proxy serves `tools/list` from
`tools-manifest.json`. Press **Rewrite config** in **Revvy ▸ Status**, and check
that `REVVY_STATE_DIR` resolves the same way for both processes.

**Tool calls time out.** The main-thread queue stalls while Unity compiles,
imports, or reloads the domain. Calls fail with a timeout after 30 s (120 s for
`manage_asset` and `execute_script`) rather than hanging the connection.

**Nothing happens after a recompile.** The bridge stops on
`beforeAssemblyReload` and restarts from the next `[InitializeOnLoad]`. If it
does not come back, check the console for a bind failure.
