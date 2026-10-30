using System.Diagnostics;
using System.Text.Json;

namespace ReforgerMcp;

public sealed partial class WorkbenchSession
{
    private sealed record TrackedProcess(int ProcessId, DateTime StartedUtc);
    private static readonly string[] PeerSettings = ["title", "enabled", "profile", "parameters", "autoJoin", "autoReconnect", "disableTimeout", "windowed", "forceUpdate", "noFocus", "maxFPS", "width", "height", "x", "y"];

    public Task<object> Multiplayer(string tool, string action, int processId, string? field, string? value, string? reason, CancellationToken ct)
    {
        if (tool is not ("dedicated" or "peers")) throw new ArgumentException("tool must be dedicated or peers.");
        if (action is not ("status" or "start" or "stop" or "configure" or "get" or "set")) throw new ArgumentException("Unsupported multiplayer action.");
        if (processId <= 0) throw new ArgumentException("processId must come from workbench_script_status.");
        if (action == "set" && string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Explain the reason for changing the user's current setting.");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
            using var verification = new NativeWorkbench.VerificationScope();
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager);
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled)) throw Blocked(process, "Resolve Workbench dialogs before using multiplayer tools.");
            JsonElement Read(string name)
            {
                var result = JsonSerializer.SerializeToElement(NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "get", name)), Configuration.Json);
                if (result.GetProperty("state").GetString() != "observed") throw new InvalidOperationException("Live plugin setting unavailable: " + name + ". The loaded plugin or configuration may be incompatible.");
                return result.GetProperty("value").Clone();
            }
            if (action == "get") return NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "get", field));
            if (tool == "dedicated" && action != "status")
                NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "clear_closed_server", "serverProcess"));
            var count = Read("peerCount").GetInt32();
            if (count is < 0 or > 32) throw new InvalidOperationException("Unsupported peer count; the explicit limit is 32 configured entries.");
            var owned = new Dictionary<string, JsonElement>();
            if (tool == "dedicated") owned.Add("server", Read("serverProcess"));
            for (var i = 0; i < count; i++) owned.Add("peer." + i, Read($"peer.{i}.process"));
            var running = owned.Values.Any(p => p.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True);
            var uncertain = owned.Values.Any(p => p.GetProperty("state").GetString() is "unavailable" or "handle_unavailable");
            var claimPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReforgerMcp", "MultiplayerClaims", $"{process.Id}-{process.StartTime.ToUniversalTime().Ticks}-{tool}.json");
            bool Unsettled() => File.Exists(claimPath);
            var tracked = Unsettled() ? JsonSerializer.Deserialize<TrackedProcess[]>(File.ReadAllText(claimPath), Configuration.Json)
                ?? throw new InvalidDataException("Invalid multiplayer launch claim; inspect before retrying.") : [];
            bool StillAlive(TrackedProcess identity)
            {
                try { using var p = Process.GetProcessById(identity.ProcessId); return p.StartTime.ToUniversalTime() == identity.StartedUtc && !p.HasExited; }
                catch (ArgumentException) { return false; }
                // Access failures propagate: uncertainty must not become 'stopped'.
            }
            void SaveClaim()
            {
                Directory.CreateDirectory(Path.GetDirectoryName(claimPath)!);
                File.WriteAllText(claimPath, JsonSerializer.Serialize(tracked, Configuration.Json));
            }
            tracked = tracked.Concat(owned.Values.Where(p => p.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True)
                .Select(p => new TrackedProcess(p.GetProperty("processId").GetInt32(), p.GetProperty("startedUtc").GetDateTime()))).Distinct().ToArray();
            if (tracked.Length > 0) SaveClaim();
            running |= tracked.Any(StillAlive);
            if (action == "status")
            {
                var settings = new Dictionary<string, JsonElement>();
                foreach (var name in tool == "dedicated" ? new[] { "executable", "profile", "parameters", "runPeers", "maxFPS", "peerExecutable" } : new[] { "executable", "launchDelay" }) settings.Add(name, Read(name));
                // world exists only on the direct-world variant; JSON-config variants
                // remain usable through their current settings and Configure dialog.
                var world = tool == "dedicated" ? JsonSerializer.SerializeToElement(NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "get", "world")), Configuration.Json) : (JsonElement?)null;
                var peers = Enumerable.Range(0, count).Select(i => new { index = i,
                    settings = PeerSettings.ToDictionary(name => name, name => Read($"peer.{i}.{name}")), process = owned["peer." + i] }).ToArray();
                return new { state = uncertain ? "observation_incomplete" : running ? "processes_running" : Unsettled() ? "launch_unsettled" : "stopped",
                    processId, tool, settings, world, peers, server = tool == "dedicated" ? owned["server"] : (JsonElement?)null,
                    trackedProcesses = tracked.Select(p => new { p.ProcessId, p.StartedUtc, running = StillAlive(p) }).ToArray(),
                    scope = "Live loaded plugin settings and observations from its own OS process handles. Running is not proof of connection or scenario readiness. Check candidate console.log startup and connection events. Peer indices are zero-based; profile suffixes use the order of enabled peers starting at 1. Settings changes are live for this Workbench instance; no registry writes are performed." };
            }
            if (uncertain) throw new InvalidOperationException("A plugin process handle is unavailable. Inspect state before changing settings or launching; do not replay an uncertain operation.");
            if (action is "set" or "configure")
            {
                if (running || Unsettled()) throw new InvalidOperationException("Stop this plugin's session before changing its configuration.");
                if (action == "set")
                {
                    ValidateSetting(field, value);
                    var before = Read(field!);
                    var result = NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "set", field, value));
                    return new { state = JsonSerializer.SerializeToElement(result, Configuration.Json).GetProperty("state").GetString(), reason, previousValue = before, result, persistence = "Loaded plugin instance only. Other settings are preserved. Workbench UI may subsequently save the instance; the MCP adapter does not write the registry." };
                }
                if (tool == "dedicated") throw new ArgumentException("Dedicated Server Tool configures inside its start dialog. Use individual live settings or start and cancel the dialog; configure is supported for Peer Tool.");
                return NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "configure"));
            }
            if (action == "start")
            {
                if (tool == "dedicated")
                {
                    var port = configuration.WorkbenchFor(process).RuntimePort
                        ?? throw new InvalidOperationException("Dedicated launch requires the managed adapter. Configure workbench.runtimePort and run workbench_runtime_setup install.");
                    RuntimeNet.VerifyListener(port, process.Id);
                    var profile = (NativeRuntimeSetup)NativeWorkbench.Execute(process, token, runtimeSetup: false);
                    RuntimeAdapter.Handshake(port, profile.ProfileDirectory);
                    var paths = RuntimeNet.Call(port, new { APIFunc = "RST_Runtime", protocol = 2, action = "multiplayer_paths", token = "r" + Guid.NewGuid().ToString("N") });
                    if (paths.GetProperty("state").GetString() != "paths_ready")
                        throw new InvalidOperationException("Dedicated launch paths unavailable. Resolve mounted project paths and addonsDir before launching. No launch dispatched.");
                }
                if (running) return new { state = "already_running", processId, tool, processes = owned, scope = "No launch invoked. Inspect all processes; a partially running group is not automatically repaired." };
                if (Unsettled()) throw new InvalidOperationException("Previous launch has not been explicitly stopped. Inspect status/logs, then stop to settle its lifecycle before another start.");
                SaveClaim(); // survives MCP reconnect; retain on failure/uncertainty
                return NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, tool == "dedicated" ? "run" : "start_peers"));
            }
            if (!running && !Unsettled() && tool != "peers") return new { state = "already_stopped", processId, tool };
            tracked = tracked.Concat(owned.Values.Where(p => p.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.True)
                .Select(p => new TrackedProcess(p.GetProperty("processId").GetInt32(), p.GetProperty("startedUtc").GetDateTime()))).Distinct().ToArray();
            SaveClaim();
            var stopped = NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "stop"));
            if (tool == "dedicated") NativeWorkbench.Execute(process, token, verification: verification, plugin: new(tool, "clear_closed_server", "serverProcess"));
            // The native plugin may present authorization dialogs or fail to kill.
            // Keep the launch unsettled until all owned handles independently say stopped.
            var remaining = Enumerable.Range(0, count).Select(i => Read($"peer.{i}.process")).ToList();
            if (tool == "dedicated") remaining.Add(Read("serverProcess"));
            var survivors = tracked.Where(StillAlive).ToArray();
            if (survivors.Length == 0 && remaining.All(p => p.GetProperty("state").GetString() is "not_started" or "exited" or "closed_handle")) File.Delete(claimPath);
            return new { result = stopped, state = Unsettled() ? "stop_unsettled" : "stopped", processes = remaining, survivingProcesses = survivors,
                scope = "Stop is verified against original process identities as well as plugin handles. If authorization was denied, the built-in plugin may clear a still-running peer handle; survivingProcesses reports that loss of ownership. No alternate process-kill path is used." };
        }, ct);
    }

    private static void ValidateSetting(string? field, string? value)
    {
        if (field is null || value is null) throw new ArgumentException("field and value are required.");
        if (field == "launchDelay" && (!int.TryParse(value, out var delay) || delay is < 0 or > 1000)) throw new ArgumentException("launchDelay must be 0..1000 seconds.");
        if (field.EndsWith("maxFPS", StringComparison.Ordinal) && (!int.TryParse(value, out var fps) || fps < -1 || fps > 10000)) throw new ArgumentException("maxFPS must be -1..10000.");
        if (field == "world" && value.Length != 0 && (!value.EndsWith(".ent", StringComparison.OrdinalIgnoreCase) || value.Any(char.IsControl))) throw new ArgumentException("world must be empty (current world) or a .ent resource name/path.");
    }
}


