# Native Workbench adapter maintenance

`src/ReforgerMcp/NativeWorkbench.cs` is the sole native dispatcher for script reload, world opening, [Play sessions](workbench-play.md), accessible UI actions and [multiplayer plugins](workbench-multiplayer.md). Its private ABI profile is intentionally exact. MCP exposes named operations, not arbitrary addresses or memory writes. The world-specific call profile is documented in [world opening](workbench-worlds.md).

## Current profile

| Item | Identity / RVA |
| --- | --- |
| Workbench | ArmaReforgerWorkbenchSteamDiag.exe 1.8.0.13 |
| Workbench SHA-256 | `7dd36ca1f662b0c13669a4f2f7f10e3c461c2906b021e742272d64b3ab7e9798` |
| Qt6Core.dll | 6.8.3.0 |
| Qt SHA-256 | `0cc62e841959fb8f6d366ea116c6f6bbc273e30f0ef90d8aa2e4adeb81fd63f8` |
| App singleton pointer | `0x3DB6598` |
| Arma4WorkbenchApp vtable | `0x2B922C8` |
| Reload busy byte | `0x3DB65E5` |
| Full reload routine | `0x222C70`, low-byte bool `(app, parentWidget)` |
| Qt four-argument invokeMethodImpl | `0x20980`, `(QObject*, QSlotObjectBase*, ConnectionType, void*)` |

Addresses are relocated from actual loaded module bases. For reload/world opening the app's `+0x164E` state byte must be clear. Play operations allow this flag, which can be set during simulation, and use the full native Play action's checks. Before allocation, the server checks both file hashes, live app vtable, busy bytes, main-window thread and live entry-point bytes against the files. This is a compatibility guard, not proof of every engine invariant. Reload readiness permits Resource Manager, Script Editor and World Editor. The owning session first invokes the existing native Stop lifecycle when World Editor is present and requires a confirmed stopped state before the full reload. Both calls hold the same process-wide reentrant dispatch mutex; there is no caller-side stop/reload choreography. World opening additionally permits an idle, clean World Editor.

Ghidra found that the native Reload WB Scripts callback at RVA `0x2957B0` passes a null parent and jumps to the full routine. The routine validates scripts, honors editor vetoes, unloads/reloads modules, recreates the game and restores editor state. Calling only the inner compiler would skip necessary lifecycle work.

## Dispatch and lifetime

The C# adapter allocates a data page and an executable thunk page in the configured Workbench process. Code is written as RW, then changed to RX and flushed; the data page stays RW. It registers x64 unwind metadata. The main callback reserves 48 bytes (shadow space plus two stack arguments); helper and queue routines reserve 32 bytes, each with matching unwind records. A short remote thread calls Qt's thread-safe queued invocation API with the app singleton as receiver. The reload itself runs inside the Qt event loop, checks the actual thread ID against the main window's thread and calls the full native routine. It never runs the reload on the remote thread.

The Qt 6.8.3 slot layout has a 32-bit reference count at offset 0 and callback pointer at offset 8. Its callback operations are Destroy=0 and Call=1; a queued invocation transfers ownership to the event. Our Destroy callback restores its stack and tail-jumps through a memory operand into the system SetEvent function. Once the event is signaled, no instruction or return address points into the thunk. The server also waits for the queue thread to exit before deregistering unwind data and freeing memory. Merely setting a flag before a callback returns would not make freeing its code safe.

The same named event is an operation claim keyed by PID and process start time, shared by reload, world opening, Play and multiplayer operations. Its historical `NativeReload` name is retained so older server processes cannot bypass it. A duplicated handle in Workbench keeps it alive after server interruption. Uncertain execution retains allocations until Workbench exit; the server never kills a native thread or blindly replays an operation.

A process-specific named mutex serializes dispatch and cleanup. If an earlier caller timed out, the existing event must be signaled by Qt's slot destructor before another dispatch can proceed. The destructor tailcalls `SetEvent` only after releasing the callback; dialog closure, elapsed time and a missing caller are insufficient evidence. A released event is reset only at the next queue boundary, and restored if queue-thread creation fails before dispatch. Old retained memory is not freed or reported as recovered. The old operation's result remains unknown: inspect resulting state before deciding the next action. An unsignaled event still blocks native operations, while independent UI actions can answer its dialog.

Primary ABI references: [Qt slot implementation, tag v6.8.3](https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/kernel/qobjectdefs_impl.h), [queued invocation ownership](https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/kernel/qmetaobject.cpp), [four-argument compatibility wrapper](https://github.com/qt/qtbase/blob/v6.8.3/src/corelib/compat/removed_api.cpp), and [Microsoft x64 prolog/epilog rules](https://learn.microsoft.com/en-us/cpp/build/prolog-and-epilog).

## Runtime profile setup

`NativeWorkbench.RuntimeSetup.cs` extends the same dispatcher for runtime setup. On the pinned Workbench binary it resolves `$profile:` through the filesystem owner (`0x1E90630`, virtual slot `0x140`). It reads the Workbench settings object at `app+0x1600`, checks its vtable (`0x3221A60`) and NET API field IDs (8 enabled, 9 port), and uses its ordinary value accessors (`0x128` read, `0x120` mutable). Enablement sets only field 8 and calls the named NET API server owner `0x22A320(app,"NetAPI")`; this routine is also reached from the Options apply lifecycle. It does not invoke the Options dialog.

Persistence uses `0x22C230` with an engine-allocated one-element field-ID array; its `0x22C310` serializer saves only the selected setting through Workbench's QSettings. Both array allocations are consumed by the save lifecycle. The port and other settings are preserved. Native return alone does not establish readiness: C# verifies the PID-owned TCP listener, built-in compilation status, current installed bytes and the adapter's build/profile handshake. Existing binary/thread/allocation-lifetime guards apply. Revalidate these private interfaces after engine updates; no registry/UI fallback is provided.

Research and validation are retained in `.temp/local/evidence/profile-setup-20261022/`: full Ghidra decompilations, first profile deployment, actual disabled-to-enabled transition, registry value comparison, restart persistence, repeat installation and failure tests. These artifacts are local evidence, not a runtime dependency. An initial research query failed because Ghidra rejects dot-prefixed project paths; a temporary filesystem alias was used for read-only inspection of the existing analysis, without introducing another analysis backend.

## Resource-leak assertion suppression

The native dispatcher sets Workbench's own per-site suppression latch before script reload, world/prefab opening, Play start/stop and native UI actions. Only `Resources are leaking! Check log!` (`GameApp.cpp:1287`) is suppressed. The resource checks and log output remain active; this does not repair a leak or suppress other assertions. The latch persists for the Workbench process, including manual actions and MCP reconnects, and resets when Workbench exits. While the MCP server is running, a background monitor checks every two seconds and activates this same latch once each configured Workbench process is ready, including processes launched manually or restarted. Assertions before readiness/activation are outside this coverage; an already-open dialog still needs a response.

`workbench_resource_leak_assertion(action="suppress", processId=...)` activates the same latch without another editor action. `action="status"` reads it without enabling suppression. Both use the existing main-thread dispatcher, binary guards, shared operation claim and pending-operation contract. The automatic monitor uses this same implementation. It defers known pre-dispatch busy/modal refusals, polls its own pending operation, and stops automatic attempts for that process after an incompatible or uncertain result. `workbench_script_status.automaticResourceLeakSuppression` and server stderr expose its last activation result; this is historical activation evidence, while the assertion tool provides live readback. The monitor runs only in MCP mode, not preparation/setup commands, and requires the MCP server to remain running to cover future Workbench processes. They refuse an existing modal dialog; setting the latch cannot dismiss a dialog already open. Separate clients and servers are unaffected.

On the pinned binary, Ghidra identifies the sole defined string at RVA `0x32164E8`, referenced by game teardown `0x1A6A690`. The branch at `0x1A6A88E` tests the byte at `0x3EDF9F0`; handler `0x1E32180` returning zero sets that byte to one at `0x1A6A8BC`. The recovered references to the byte are that read and write. The adapter verifies the exact executable/Qt hashes and live bytes across the assertion branch, accepts only a Boolean latch, then writes one byte on the main thread. No installed file or native instruction is patched; no persistent injected callback is needed. An incompatible profile is rejected.

The assertion wrapper calls `0x1E31B60`, whose `MessageBoxA` result mapping returns zero for Abort (3), two for Ignore (5), and one for Retry (4) with an attached debugger. Thus the latch reproduces this callsite's Abort behavior, continuing teardown without future popups; Ignore only continues the current occurrence.

Research evidence: `.temp/local/evidence/profile-setup-20261022/leak-search/`, `leak-owner-02/`, `leak-owner-instructions/`, `leak-flag-refs/`, `leak-handler/` and `leak-dialog-handler/`. The first owner query exceeded the bridge's page bound; the corrected query and complete decompilation are retained. The instruction query's display helper failed after successfully saving the response; the retained response is valid.

Live evidence on 2026-10-23 is retained in that directory's `leak-candidate-flag-03/`, `leak-candidate-activate-03/`, `leak-live-session-05/` and `leak-package-live-06/`. Initial readback was false; explicit activation returned true on the expected editor thread, released its allocations, and survived MCP reconnects. Stop Play and normal World Editor close completed. A concurrent client then performed a reload: the native log recorded ThermalProfile and BlockListApi leaks, game destruction and successful initialization without waiting for an assertion response. The packaged server read back true and captured the normal Resource Manager afterward. An unrelated Peer Tool authorization prompt remained functional. These checks establish the pinned build's process-local suppression, not a leak repair or suppression before activation. The earlier access-denied session and deployment of an older server are retained as failures, not passing tests.

## Updating after Workbench changes

1. Keep the unsupported-binary rejection in place. Record hashes and loaded module versions for the new installation.
2. Use the existing Ghidra backend to retrace the native menu callback, full reload lifecycle, singleton/vtable and busy fields. Reconfirm the exact installed Qt ABI against its source tag and exports.
3. Update this isolated profile and any affected thunk/lifetime contract. Do not carry old addresses forward based on version strings or scan-and-call a guessed match. There is no fallback backend.
4. Verify a harmless main-thread callback before a real reload, then run the real script suite: changed Game and WorkbenchGame CRCs, initialization, invalid-source failure, recovery, repeat calls, foreground observations and cleanup. Review cancellation/uncertain ownership separately; never induce a hung operation in a user's unsaved project.
5. Obtain independent review and package the server. Reconfirm the assertion latch and branch as well as lifecycle entry points. No persistent engine patch or project bridge needs reapplying.

Local development evidence is retained outside distribution: `.temp/local/evidence/native-hook-*` and Ghidra queries under `.temp/artifacts/native-index/queries/`. Ghidra's full reload decompilation is query `4b7bb9d698054b28994777e58a192c6d`; the singleton getter is `a29b67b519fb441ab8d8ffb3a4c3aeca`. Research artifacts contain installation-specific data and are not shipped.

## Fork: Stable and Experimental profiles

The fork retains the Stable identity above and stages **Workbench 1.9.0.10** with
SHA-256 `aeaad3d40483c96697ca7d9618979abd8c0b1422ec95fbb14437050546aef657`.
Its Qt Core, Gui and Widgets hashes match the Stable modules. Installation labels
do not select native addresses: the observed executable hash selects
`NativeWorkbenchProfile`, and all embedded image addresses pass through that
profile. An absent address or unreviewed API slot fails closed. The legacy
configuration still works; [dual installation setup](enfusion-migration.md#stable-and-experimental-in-one-server)
keeps per-installation executable, dependencies, runtime port, profile and logs.

Important verified static changes in the 1.9 candidate:

- Full reload owner `0x22caf0`; application singleton/global busy byte
  `0x3e98de8` / `0x3e98e35`; app busy/settings members `+0x1656` / `+0x1608`.
- Full Play owner `0x28a340`, retaining its additional lifecycle branch. The
  helper `0x28a420` is not a substitute for that owner.
- World/prefab opener `0x28e6d0`. World Editor document/API members remain
  `+0x120` / `+0x128`, and document path remains `+0x50`.
- Explicit WorldEditorAPI slot correspondences account for inserted methods:
  `0x1c8 -> 0x1d0`, `0x1d8 -> 0x1e0`, `0x238 -> 0x240`,
  `0x318 -> 0x320`, `0x320 -> 0x330`, `0x328 -> 0x338`, and
  `0x478 -> 0x498`. Other interface layouts must not receive these shifts.
- NET API owner `0x234450`, enabled/port field globals `0x3be65c0` / `0x3be65c4`,
  settings persistence `0x2364b0`. Live field-ID checks still require 8 and 9.
- Resource-leak latch `0x3fc2bf0`, checked against the relocated teardown branch.

Evidence includes exact PE hashes, instruction/caller correspondence, named
import entries, MSVC RTTI, method registration names, focused Ghidra
disassembly/decompilation and an independent static ABI review. Similar prefixes
alone were not sufficient: identical wrapper shapes were disambiguated through
registrations/callers, and virtual slots through their owning interface. Ghidra
pseudocode remains inferred evidence. Raw game bytes, derived code and local
analysis databases are not distributed.

The full-program Ghidra preparation was interrupted when host free memory fell
to about 1.1 GiB; its incomplete data is not a prepared index or acceptance.
Focused passes used separately hash-checked snapshots, a smaller heap and no
whole-program analysis. Their evidence is local and is not a substitute for
preparing an `engine_*` database when that capability is needed.

### Validation state

The server builds, and offline checks cover both profiles' emitted callbacks,
address/slot coverage, callback-size bounds, exact-hash refusal, installation
ambiguity, runtime-port separation, foreign same-named processes and stale
process identities. Existing PAK-search and tool-activity suites also pass.

**Experimental live acceptance is pending.** Stable 1.8.0.13 has passed the
harmless owning-thread probe, valid/invalid/recovered reloads (including stopping
Play), world opening and missing/dirty/wrong-world refusals, entity/component
creation, typed property editing, Undo/Redo, prefab export/placement and
save/reopen readback. The profile adapter compiled and matched its build/profile/
port handshake; live `Add(6,7)` returned 13 and `SetValue(42)`/`GetValue()` read
back 42. Maximized-window screenshot capture also passed after initial frame
geometry reconciliation. A format-conversion dialog timed out the first world
open; its uncertain result and retained allocations were preserved, the dialog
was resolved through MCP, and the callback release was verified before later
operations. Subsequent open completed with allocation cleanup. One failed
reload's inline log correlation exceeded its budget and correctly reported
unavailable; native failure and the fixture's syntax error were independently
observed. Recovery compiled successfully; its resource errors include stale
Workbench plugin references to addons absent from the isolated project.

Before calling 1.9 ready, repeat the applicable checks with its owning profile,
including prefab definition/child creation, array editing and Play/runtime
transitions. Multiplayer settings/launch lifecycle remains a separate live
check; compilation alone does not establish dedicated-server or peer readiness.
Preserve uncertain operations and failed evidence.

No active MCP registration has been replaced by this work.

For controlled candidate verification, `automaticResourceLeakSuppression: false`
disables the background suppression monitor. Explicit native operations retain
their existing per-operation suppression behavior. The default remains true for
legacy configurations. The verification configuration disables the monitor so
MCP discovery/status testing cannot automatically write to an editor opened by
someone else, and the harmless thread probe can precede lifecycle mutations.


