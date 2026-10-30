# Reforger MCP

Fork preparation: see [Stable/Experimental profiles and validation state](docs/workbench-native-reload.md#fork-stable-and-experimental-profiles)
and the [enfusion-mcp migration comparison](docs/enfusion-migration.md). The
1.9 profile is a validation candidate; live acceptance and any client cutover
are separate steps.

Tool calls are logged automatically with bounded storage and session summaries;
see [tool activity logging](docs/tool-activity.md).

A standalone Windows 10 (build 19041+) x64 C# MCP server for Arma Reforger modding: launch Workbench, capture selected editor/game windows, search installed PAK assets, export and search script source, and inspect native engine code. It uses one C# PAK reader, **one Ghidra backend** for native code and **Windows Graphics Capture** for screenshots. No model API, Python service, provider selection, or fallback chain.

Current capability: directly launch Workbench, open worlds and reload scripts through version-checked native calls on the editor thread, inspect and operate accessible menus, buttons, dialog text fields and folder trees, and capture editor/game windows; catalogue PAKs, prepare a versioned text store, search paths and readable script/resource contents, read text and extract selected resources; prepare/search installed Windows native executables and retrieve function code/context. Standalone validation, arbitrary custom-widget manipulation, arbitrary standalone game control, arbitrary memory/function injection, resource conversion and mod repacking are not implemented.

Configure Workbench in your single `configuration.json`; see the [Workbench guide](docs/workbench.md). Workbench launch itself needs no Ghidra, Java or scripted bridge.

For the actual loaded project, working addon files and other available addons, use [project context](docs/project-context.md). `workbench_project_context` returns native addon identities and project locations; `pak_prepare(projectToken)` indexes both packed and unpacked files through the existing source tools. Offline configurations can also specify `unpackedDirectories`. Disk content and local module declarations are distinguished from unsaved buffers and effective compiler/resource precedence.

For Workbench and independent peer logs, configure `logDirectories` and use [console-log reading](docs/console-logs.md). `log_read` defaults to the latest script reload/compilation and subsequent runtime output, with bounded pages, severity/category filters and literal or regex search.

The same Workbench configuration enables [native script compilation/reload](docs/workbench-scripts.md). Reload uses a temporary in-process adapter, without menus, keyboard input or a foreground requirement; its native return value confirms completion. The exact supported binary profile and update procedure are [documented separately](docs/workbench-native-reload.md). No project bridge is installed.

[Opening a world](docs/workbench-worlds.md) uses that same native dispatcher. It creates World Editor if needed, opens a supplied `.ent` resource, and reports the native loaded world path. It requires no focus or keyboard input and refuses unsaved/play state. Script reload supports an open World Editor and stops embedded Play first.

[Entity and prefab tools](docs/workbench-worlds.md#entity-creation-and-prefabs) create entities, export named entities as new prefabs, and place GUID-qualified prefab instances through owning native functions. They work with the loaded project's addons, require no bridge scripts, and leave world changes unsaved. Export waits for resource-database acknowledgement before reporting completion; asset export itself is outside world undo.

[Authoring tools](docs/workbench-targeting.md) inspect effective values and their supplying ancestors, edit nested properties and supported arrays, edit/save existing prefab definitions, create inherited child prefabs, and save worlds or undo/redo through native editor owners. Explicit scope and persistence limits are documented in the guide.

[Live component calls](docs/workbench-runtime.md) during embedded Play use a development adapter installed in the Workbench profile and a configured local NET API port. `workbench_runtime_setup` installs it and can enable NET API while it is off; the packaged `scripts/setup-workbench.ps1` uses the same implementation. Prepare a public call with supported typed arguments and a return type, then invoke its single-use token and verify receipt-correlated logs/game state. Live inspection reads supported reflected component fields. Gameplay components need no MCP annotations or base classes. This version does not control standalone game/server script VMs.

For screenshots of Workbench and separate Reforger clients, set `captureExecutables` in the same configuration; see the [window capture guide](docs/window-capture.md). Images are returned directly through MCP.


Start with the [PAK modding guide](docs/pak-modding.md) for source examples, sound discovery and updates.

## Setup

1. Copy `configuration.example.json` to `configuration.json` once. This is the only configuration file the server needs; every capability uses it. Keep your private `configuration.json` out of releases and Git.
2. Replace the example paths with your installed locations. Keep only entries you use: `pakDirectories` indexes archives, `unpackedDirectories` indexes loose addon files, `workbench` enables editor operations, `captureExecutables` enables screenshots, and `logDirectories` enables log reading. Remove `workbench` or set it to `null` if unused; use `{}` for unused directory/executable maps. For live runtime access, first run `workbench_runtime_setup` with `action="status"`, set `workbench.runtimePort` to the observed editor port, restart the MCP connection, then run `action="install"`; see [profile setup](docs/workbench-runtime.md#profile-installation-and-net-api-enablement). Leave the port unset if runtime access is unused. Native `targets` require [Ghidra 12.1.4](https://github.com/NationalSecurityAgency/ghidra/releases/tag/Ghidra_12.1.4_build) and JDK 21, such as [Temurin](https://adoptium.net/temurin/releases/?version=21); when native analysis is unused, set `targets` to `{}` and `ghidraHome`/`javaHome` to empty strings. Paths may be absolute, configuration-relative, or use `%VARIABLE%`; `${Documents}` resolves Windows Documents. The example uses `%LOCALAPPDATA%/ReforgerMcp` for generated data. Native database paths cannot contain dot-prefixed components.
3. Build with .NET 10: `dotnet build src/ReforgerMcp -c Release`. Or run the executable from the self-contained Windows package produced by `scripts/package.ps1`.
4. Run `ReforgerMcp.exe --check-config "path/to/configuration.json"` to inspect resolved paths and missing configured inputs. This read-only check exits 0 when configured inputs exist, 1 for missing/invalid settings; it does not prepare data or verify runtime compatibility/write permissions.
5. Configure your MCP client to launch the server through stdio, using the actual executable and configuration locations:

```json
{
  "mcpServers": {
    "reforger": {
      "command": "C:/Tools/ReforgerMcp/ReforgerMcp.exe",
      "args": ["--config", "C:/Tools/ReforgerMcp/configuration.json"]
    }
  }
}
```

This is the common command/arguments shape; client settings formats differ. A source build can instead use `dotnet` as the command with the server DLL followed by the same arguments. The server is client-independent. A hosted connector or marketplace plugin is not supplied here.

Configuration-relative paths are resolved against the JSON file's folder, never the MCP client's working directory. Variables resolve in the server process's environment. An unset variable fails with its field name; no alternate Steam path is substituted. Moving the configuration file changes relative paths, so recheck it after a move. Each user supplies their own configuration; the release contains the example only. See the [PAK guide](docs/pak-modding.md) for a minimal setup.

Only `workbench_open_project` launches Workbench, which loads the selected project's scripts and uses its normal profile and online services. The server does not modify installed game files. PAK preparation reads the installed archives and saves derived catalogues/scripts separately. Native analysis uses a private executable copy and can take tens of minutes per binary. Native/PAK operations report busy explicitly. Keep only one server process attached to a given data directory.

### One shared local server

For multiple chats, agents or clients, use the explicit shared HTTP mode in the
[shared server guide](docs/shared-server.md). Start one server and configure every
client with its loopback URL and bearer token. Clients then connect without
launching extra processes. Stdio remains available for one owning client; it is
never used as a fallback. Both transports expose the same tools and services.

## Tools

Start with `workbench_status`, then use `pak_search_text` for API declarations, call sites, prefab components and layout properties, `pak_search` for resources, and `pak_read` for bounded context. Follow base classes, implementations, and prefab references before drawing conclusions. See the [source research workflow](skills/reforger/references/workflows.md).

| Tool | Purpose |
|---|---|
| `capture_windows` | List eligible editor/client windows with process IDs, titles and temporary selection IDs. |
| `capture_screenshot` | Return a selected window as a PNG image plus metadata, without activating it. |
| `capture_sequence` | Return a short sequence of window frames with native timestamps for motion inspection. |
| `workbench_status` | Combined `workbench` process/window observations and `pak` catalogue freshness, source coverage and revision; project readiness remains unverified. |
| `workbench_project_context` | Observe the actual current project and loaded/available addons, mounted paths, on-disk dependencies and local module declarations; return a token for packed/unpacked project preparation. |
| `workbench_script_status` | Inspect native editor windows/dialogs and current compiler output/diagnostics, which may be historical. |
| `workbench_native_probe` | Verify the exact-build main-thread dispatcher and allocation cleanup without reload, world editing, Play or assertion suppression. |
| `workbench_reload_scripts` | Full native validation/reload of Game and Workbench modules; takes only `processId`. |
| `workbench_open_project` | Directly launch an absolute `.gproj` path with configured dependency directories; refuse if Workbench is running. |
| `workbench_open_world` | Open one `.ent` world through the native hook; return completion or a pending operation ID. |
| `workbench_resource_leak_assertion` | Observe or activate [process-local suppression](docs/workbench-native-reload.md#resource-leak-assertion-suppression) of the specific resource-leak popup. Automatically activated when each Workbench session becomes ready while MCP is running, and before native lifecycle/UI mutations; leak logs remain enabled. |
| `workbench_entity` | Observe a named entity and the active layer in the expected world. |
| `workbench_query_entities` | Bounded native searches across loaded editor sources, including locked objects. |
| `workbench_inspect_entity` | Inspect components, nested objects, arrays and effective authoring values with override provenance; obtain an edit token. |
| `workbench_edit_entity` | Set/clear properties, edit supported arrays and add/remove components using the inspected scope. See [authoring limits and validation](docs/workbench-targeting.md). |
| `workbench_open_prefab` | Open an existing GUID-qualified prefab in definition-editing mode. |
| `workbench_inspect_prefab` | Inspect the opened definition and obtain explicitly scoped edit tokens. |
| `workbench_save_prefab_definition` | Save pending changes in the current prefab document through its native owner. |
| `workbench_create_child_prefab` | Create a detached child with an explicit ancestor and verified new resource identity. |
| `workbench_save_world` | Save current world/layer changes and report completion/modified state. |
| `workbench_history_status` / `workbench_history` | Inspect native undo/redo availability or apply one history action. |
| `workbench_create_entity` | Create a root entity from a class or GUID-qualified prefab through the owning native API. |
| `workbench_save_prefab` | Export a named entity to a new `.et` prefab and return its GUID-qualified resource name. |
| `workbench_multiplayer_status` | Inspect live dedicated/Peer Tool settings, processes and candidate logs. |
| `workbench_multiplayer` | [Start/stop dedicated servers and peers](docs/workbench-multiplayer.md) through their existing plugin functions. |
| `workbench_multiplayer_setting` | Read or deliberately change one live setting, preserving other options. |
| `workbench_play_status` | Observe whether the World Editor is playing on its main thread. |
| `workbench_play` | [Start or stop Play](docs/workbench-play.md), guarded against toggling an already matching state. |
| `workbench_runtime_setup` | Inspect setup, install the bridge into the mounted Workbench profile, or enable and persist NET API when off; verify live readiness. |
| `workbench_runtime_status` | Observe the explicitly installed adapter and embedded Play state. |
| `workbench_runtime_inspect` | Read a named live entity and supported reflected component fields during Play. |
| `workbench_runtime_call` | Validate and invoke once; return its full value compactly, with receipt, timings and operationId. Read original diagnostics with `workbench_operation(details=true)`. |
| `workbench_runtime_prepare` | Resolve a live target and compile-check typed arguments and return type; return a short-lived single-use token. |
| `workbench_runtime_invoke` | Invoke through the game module and return supported values; correlate the receipt with logs and gameplay assertions. |
| `workbench_operation` | Poll pending native world, entity, prefab, reload, Play, multiplayer or runtime setup operations. |
| `workbench_windows` | Read visible Workbench windows, popup text and actionable buttons. |
| `workbench_controls` | Discover accessible menus, buttons, text fields and tree rows with their current state. |
| `workbench_window_action` | Invoke controls, set text, select/expand tree rows, toggle menus/checkboxes or close windows through native actions. |
| `workbench_window_action_status` | Poll a queued UI action while inspecting and resolving any dialogs it opens. |
| `pak_prepare` | Prepare configured or explicitly selected project sources, including packed and loose files, into a new revision. |
| `pak_search` | Bounded path/type searches, including all sounds, scripts, prefabs and layouts. |
| `pak_search_text` | Find declarations, examples, usages and resource literals. Use `query` for one search or `queries` for up to eight searches sharing one scan and bounded previews. |
| `pak_read` | Read source/configuration text with line numbers, hash and packed/loose source identity. |
| `pak_extract` | Export one selected resource with provenance, keeping its original bytes. |
| `engine_targets` | Current executable identities and ready, stale, unavailable or missing-project state. |
| `engine_prepare` | Start explicit preparation; return a job ID without waiting for analysis. Reuse current analysis. |
| `engine_job` | Read job state, latest log activity, error and evidence directory. |
| `engine_cancel` | Cancel a queued/running preparation. Poll for its terminal state. |
| `engine_diagnostics` | Read paged global analysis warnings/errors without loading the binary again. |
| `engine_search` | Case-insensitive all-term search of defined strings or symbols, with bounded results. |
| `engine_references` | Follow a string/symbol address to resolved inbound references and containing functions. |
| `engine_function` | Read `overview`, `code`, `context`, `callers`, `callees`, `instructions` or inferred `types`. |

Start with `engine_targets`, then `engine_prepare` for a missing/stale target. Poll `engine_job` until `ready`. Keep the MCP server running while a job is active; shutdown cancels its work. To prepare without a client, run `ReforgerMcp --config <path> --prepare server` (or `all`). Failed jobs retain logs and require an explicit new preparation request; they never become usable analysis.

To replace a damaged database, call `engine_prepare` with `rebuild: true`. This retires the current manifest before starting; cancellation or failure leaves the target unprepared. The retired manifest and old project remain as evidence, never as an automatic fallback.

Search examples: `replication`, `prefab`, `resource`, or an error message from a log. Choose `kind: "symbols"` to search a class name. String results include up to eight references; `engine_references` provides the full paginated resolved-reference list. Follow `functionAddress` into `engine_function`.

Native-tool pages use zero-based `offset` and `limit` (1–100). `nextOffset: null` means the selected collection is exhausted. Pass `identity.analysisKey` as `analysisKey` on subsequent native requests to reject changed analysis. Native text entries longer than 1,500 characters are explicitly marked truncated. Addresses are hexadecimal virtual addresses returned by the same binary's results, not interchangeable between versions or executables. Instruction pages start at the selected address, so a string-reference address can take you directly to the relevant instructions. PAK tools use their separate `revision` token and 2,000-character line limit; see the PAK guide.

## What the evidence means

Pseudocode is reconstructed C-like evidence, **not original C++ or an Enforce Script API**. Names/types may be generated or inferred. `context` retains strings, imported symbols and computed call sites whose targets remain unknown. `overview` and `code` include decompiler warnings, including warnings embedded in the generated code. Global analysis warning/error counts accompany queries; full logs remain in the preparation evidence directory. A completed analysis can still have imperfect coverage.

For mod work, use engine findings to investigate behavior and generate hypotheses. Verify callable APIs and resource authoring against official documentation, script declarations and Workbench. See [modding investigation workflows](docs/modding-workflows.md).

## Data and maintenance

For native analysis, `dataDirectory` holds one manifest per configured target, preparation job directories with binary snapshots/projects/logs, and query evidence directories. Hashes of installed executable bytes are checked on every native query, and again before returning a result. Changed bytes or analysis settings require explicit preparation; an old build is never silently substituted. A missing saved project is reported and can be explicitly rebuilt. PAK data is under `dataDirectory/paks`: full archive hashes are recorded at preparation, while queries check archive set/length/modification time and text searches verify stored content hashes. See the PAK guide for refresh and retention.

Completed databases survive restarts. Interrupted jobs are reported as interrupted, with no automatic replay. Clients cancel an active query using the standard MCP `notifications/cancelled` message; cancelling only a client's local wait may leave server work running until its configured deadline. Preparation uses the explicit `engine_cancel` tool. Evidence and old builds are retained for diagnosis; disk usage grows. Stop the server before manually removing unneeded job/query directories, and retain every project referenced by a current target manifest. This version has no automatic retention or cleanup scheduler.

The Ghidra version and analysis profile (CPU count and analysis deadline) identify a prepared database. The public `analysisKey` also identifies the saved revision: rebuilding the same executable invalidates earlier page tokens. The adapter is Java glue; MCP/configuration/lifecycle ownership stays in C#. Distribution includes the server and adapter, not proprietary game binaries, recovered game code, Ghidra or the JDK.

## Development and validation

The Rust Workbench reconstruction effort was discontinued on 2026-10-20. This project retains native-code inspection through the C# MCP server and its Ghidra adapter. Editor reconstruction, automatic translation to Rust, and mod compilation/packing orchestration are outside the current scope.

Use the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk), pinned through NuGet lock files. `scripts/package.ps1` builds a self-contained Windows x64 ZIP. The public solution builds only the server. Optional maintainer tests, evidence, research and old packages live in ignored `.temp/`; deleting that entire directory does not remove required source or prevent a fresh build/package. Historical harnesses and evidence are disposable, not regenerated by a build. See [workspace layout and rebuilding](docs/workspace.md) and [historical validation](docs/system-validation.md). See [release preparation](docs/releasing.md) for source/package checks and remaining release decisions.

See [Workbench controls and dialogs](docs/workbench-windows.md) for native menu/button actions, popup inspection and screenshots during pending operations.

