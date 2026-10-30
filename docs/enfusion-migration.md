# Preparing a migration from enfusion-mcp

This fork is being prepared alongside the existing server. Building or running a
test instance does not change an MCP client registration or authorize a cutover.
The comparison below targets the installed npm **enfusion-mcp 0.6.1**; a separate
0.10.0 source checkout is not evidence that its additional tools are active.

## Capabilities to retain or replace deliberately

- **API and wiki lookup:** `api_search`, its class/group resources, and the
  pre-downloaded `wiki_search` index have no equivalent structured index here.
  PAK/loose-file searches provide installed declarations, implementations and
  usage examples. Official documentation research still needs a separate source.
- **Scaffolding and templates:** `mod_create`, `script_create`, `prefab_create`,
  `layout_create`, `config_create`, `server_config`, mod patterns and guided
  prompts are not supplied here. An agent can author these files with ordinary
  development tools, but these MCP conveniences would disappear on replacement.
- **Build and validation wrappers:** `mod_build`, `mod_validate` and dedicated
  material/texture `wb_validate` calls are not equivalent to script reload.
  Keep the project's actual build, lint and game-test pipeline.
- **Project files and editor text:** `project_browse/read/write` and
  `wb_script_editor` do not have matching write tools here. Source inspection is
  available through the PAK/loose source tools; normal file editing remains a
  development-tool responsibility.
- **Dedicated editor utilities:** the old terrain, layer, clipboard, selection,
  localization and generic menu-path tools do not have one-for-one counterparts.
  Native entity/prefab/history tools cover some outcomes; accessible controls
  cover only their advertised actions. Do not claim general parity from a menu
  being visible.
- **Game-file research:** packed/loose path search, text search, reading and
  extraction cover the old asset discovery use cases with source and revision
  identity. They do not establish effective inheritance or resource precedence.
- **Added here:** native authoring inspection, supplying-ancestor provenance,
  background window capture/sequences, bounded console logs, embedded runtime
  component calls, native binary investigation and shared local HTTP transport.

The project's separate MRS_UI knowledge server is an independent registration;
replacing the Reforger server does not require removing it. Knowledge-base tools
from a different enfusion-mcp version must be inventoried before a future cutover.

Runtime component calls and editor actions are not player-input evidence and do
not replace multiplayer, persistence, join-in-progress or manual verification.

## Stable and Experimental in one server

Use `workbenches` with named configurations instead of the legacy single
`workbench` object. Each entry has its own executable, addon directories,
optional `profileDirectory` / `logDirectory`, runtime port and optional pinned
process identity. The legacy configuration remains supported.

```json
"workbenches": {
  "stable": {
    "executable": "D:/SteamLibrary/steamapps/common/Arma Reforger Tools/Workbench/ArmaReforgerWorkbenchSteamDiag.exe",
    "addonDirectories": ["D:/SteamLibrary/steamapps/common/Arma Reforger/addons"],
    "profileDirectory": "${Documents}/My Games/ReforgerMcpStable",
    "logDirectory": "${Documents}/My Games/ReforgerMcpStable/logs",
    "runtimePort": 5775
  },
  "experimental": {
    "executable": "D:/SteamLibrary/steamapps/common/Arma Reforger Experimental Tools/Workbench/ArmaReforgerWorkbenchSteamDiag.exe",
    "addonDirectories": ["D:/SteamLibrary/steamapps/common/Arma Reforger Experimental Tools/Game/addons"],
    "profileDirectory": "${Documents}/My Games/ReforgerMcpExperimental",
    "logDirectory": "${Documents}/My Games/ReforgerMcpExperimental/logs",
    "runtimePort": 5776
  }
}
```

Paths are examples; configure the actual installed dependencies. A runtime port
is an intended connection, not proof that the selected editor is listening on it.
Inspect setup and explicitly configure/install through that editor as necessary.

`workbench_status` inventories both installations. Launch with
`workbench_open_project(projectPath, installation="experimental")`; launch still
refuses while any Workbench is running. Live semantic operations already take
`processId`; `workbench_script_status` and `workbench_windows` accept an optional
`processId` for unambiguous discovery. Tokens retain their originating process.
No mutable global channel switch routes another chat's work.

Label packed/loose roots, capture executables and logs by installation. Native
analysis target IDs may also be labels such as `workbench-stable` and
`workbench-experimental`; their stores and binary identities remain separate.
Selecting an editor does not silently re-prepare or switch the PAK catalogue.
Use explicit source labels or prepare its project context and retain the returned
revision. Concurrent native operations remain serialized.

Channel names do not authorize an unfamiliar native build: executable and Qt
hashes determine the compatibility profile. An engine update must receive its
own reviewed profile before native dispatch is enabled.
