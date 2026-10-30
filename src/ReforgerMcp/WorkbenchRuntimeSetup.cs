using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

internal static class RuntimeAdapter
{
    internal static readonly byte[] Source = ReadSource();
    internal static readonly string Build = Convert.ToHexString(SHA256.HashData(Source));
    internal static readonly byte[] InstalledSource = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Source).Replace("__RST_ADAPTER_BUILD__", Build, StringComparison.Ordinal));
    internal static readonly string InstalledHash = Convert.ToHexString(SHA256.HashData(InstalledSource));
    private static byte[] ReadSource()
    {
        using var output = new MemoryStream();
        foreach (var resource in new[] { "ReforgerMcp.RuntimeAdapter", "ReforgerMcp.MultiplayerAdapter", "ReforgerMcp.EditorChoicesAdapter" })
        {
            using var input = typeof(RuntimeAdapter).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("Server distribution is missing its embedded adapter: " + resource);
            input.CopyTo(output);
            output.WriteByte((byte)'\n');
        }
        return output.ToArray();
    }
    internal static string PathIn(string profile) => Path.Combine(profile, "scripts", "WorkbenchGame", "ReforgerMcp", "RST_Runtime.c");
    internal static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    internal static void CheckPath(string path)
    {
        for (var item = new DirectoryInfo(Path.GetDirectoryName(path)!); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Adapter installation does not traverse junctions/symlinks: " + item.FullName);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Adapter target is a link: " + path);
    }
    internal static bool Install(string profile)
    {
        var target = PathIn(profile); CheckPath(target);
        var metadata = Path.Combine(profile, "ReforgerMcp", "runtime-install.json"); CheckPath(metadata);
        var previous = File.Exists(target) ? Hash(target) : null;
        if (previous == InstalledHash)
        {
            WriteManifest(metadata);
            return false;
        }
        if (previous is not null)
        {
            if (!File.Exists(metadata)) throw new InvalidOperationException("Unmanaged adapter preserved: " + target);
            using var manifest = JsonDocument.Parse(File.ReadAllText(metadata));
            if (manifest.RootElement.GetProperty("installedSha256").GetString() != previous)
                throw new InvalidOperationException("Adapter has local edits; preserved without overwrite: " + target);
            var backup = Path.Combine(profile, "ReforgerMcp", "backups", previous + ".c"); CheckPath(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (!File.Exists(backup)) File.Copy(target, backup);
            else if (Hash(backup) != previous) throw new InvalidOperationException("Adapter backup conflict: " + backup);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, InstalledSource);
            if ((File.Exists(target) ? Hash(target) : null) != previous) throw new InvalidOperationException("Adapter changed during installation.");
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        WriteManifest(metadata);
        return true;
    }
    private static void WriteManifest(string metadata)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        var temporary = metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { adapterBuild = Build, installedSha256 = InstalledHash }, Configuration.Json));
            File.Move(temporary, metadata, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static JsonElement Handshake(int port, string profile)
    {
        var receipt = "r" + Guid.NewGuid().ToString("N");
        var response = RuntimeNet.Call(port, new { APIFunc = "RST_Runtime", protocol = 2, action = "status", token = receipt });
        if (!response.TryGetProperty("protocol", out var protocol) || protocol.GetInt32() != 2 ||
            !response.TryGetProperty("token", out var token) || token.GetString() != receipt ||
            !response.TryGetProperty("adapterBuild", out var build) || build.GetString() != Build ||
            !response.TryGetProperty("profileDirectory", out var path) || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.GetString()!)), profile, StringComparison.OrdinalIgnoreCase) ||
            !response.TryGetProperty("state", out var state) || state.GetString() is not ("playing" or "not_playing"))
            throw new InvalidOperationException("Runtime adapter identity/profile/protocol mismatch. Install this server's adapter and inspect reload diagnostics.");
        return response;
    }
}

public sealed partial class WorkbenchSession
{
    internal Task<object> RuntimeSetup(string action, int processId, CancellationToken ct, int? port = null)
    {
        if (action is not ("status" or "install" or "enable" or "configure")) throw new ArgumentException("action must be status, install, enable or configure.");
        if (action == "configure" ? port is null or < 1 or > 65535 : port is not null)
            throw new ArgumentException("Only configure accepts a required port from 1 to 65535.");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed.");
            var observedWindows = Windows(process.Id);
            RequiredWindow(observedWindows, ResourceManager);
            if (observedWindows.Any(IsModal) || observedWindows.Any(w => !w.Current.IsEnabled))
                throw Blocked(process, "Resolve Workbench dialogs before runtime setup.");
            if (action == "install" && (observedWindows.Any(w => w.Current.AutomationId is not (ResourceManager or ScriptEditor or "Arma4WorkbenchApp.worldEditorMain" or "Arma4WorkbenchApp.NotificationPopup")) ||
                observedWindows.Any(w => w.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.TabItem))
                    .Cast<System.Windows.Automation.AutomationElement>().Any(t => t.Current.Name.Contains('*')))))
                throw Blocked(process, "Close unsupported editors and save script tabs before installing the profile bridge. World Editor may remain open; setup uses the normal reload operation.");
            var native = (NativeRuntimeSetup)NativeWorkbench.Execute(process, token, runtimeSetup: false);
            var target = RuntimeAdapter.PathIn(native.ProfileDirectory);
            RuntimeAdapter.CheckPath(target);
            var installed = File.Exists(target) && RuntimeAdapter.Hash(target) == RuntimeAdapter.InstalledHash;
            var configuredPort = configuration.WorkbenchFor(process).RuntimePort;
            if (action == "configure" && configuredPort != port)
                throw new InvalidOperationException("Set workbench.runtimePort to the deliberately requested port before configuring Workbench. Nothing changed.");
            if (action == "configure") RuntimeNet.VerifyPortAvailableOrOwned(port!.Value, process.Id);
            if (action is not ("status" or "configure") && configuredPort != native.Port)
                throw new InvalidOperationException($"Set workbench.runtimePort to Workbench's current NET API port {native.Port}. No port/settings/files were changed.");
            var changed = false;
            var wasEnabled = native.Enabled;
            var previousPort = native.Port;
            if (action == "install") changed = RuntimeAdapter.Install(native.ProfileDirectory);
            if (action != "status") native = (NativeRuntimeSetup)NativeWorkbench.Execute(process, token, runtimeSetup: true, runtimePort: port);
            var needsReload = action == "install";
            if (needsReload && !changed)
            {
                try
                {
                    RuntimeNet.VerifyListener(native.Port, process.Id);
                    RuntimeAdapter.Handshake(native.Port, native.ProfileDirectory);
                    needsReload = false;
                }
                catch (InvalidOperationException) { /* Current source is present but not confirmed loaded; explicit install reloads it. */ }
            }
            if (needsReload)
            {
                runtimePrepared = null;
                var reload = JsonSerializer.SerializeToElement(ExecuteCore(processId, token), Configuration.Json);
                if (!reload.TryGetProperty("state", out var reloadState) || reloadState.GetString() != "reloaded")
                    return new { state = "reload_failed", profileDirectory = native.ProfileDirectory, adapterPath = target, changed, reload,
                        message = "Profile adapter remains installed; readiness is unverified. Resolve diagnostics before another explicit operation." };
                installed = File.Exists(target) && RuntimeAdapter.Hash(target) == RuntimeAdapter.InstalledHash;
            }
            var listener = false; JsonElement? builtin = null, adapter = null; string? problem = null;
            try
            {
                var until = DateTime.UtcNow.AddSeconds(action == "status" ? 0 : 5);
                while (true)
                {
                    try { RuntimeNet.VerifyListener(native.Port, process.Id); listener = true; break; }
                    catch (InvalidOperationException) when (DateTime.UtcNow < until) { Thread.Sleep(100); }
                }
                builtin = RuntimeNet.Call(native.Port, new { APIFunc = "IsWorkbenchRunning" });
                if (!builtin.Value.GetProperty("IsRunning").GetBoolean() || !builtin.Value.GetProperty("ScriptsCompiled").GetBoolean())
                    throw new InvalidOperationException("Workbench is not running with successfully compiled scripts.");
                if (!installed) throw new InvalidOperationException("Current profile adapter is missing or differs from this server. Run install.");
                adapter = RuntimeAdapter.Handshake(native.Port, native.ProfileDirectory);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { problem = ex.Message; }
            var ready = native.Enabled && installed && configuredPort == native.Port && adapter is not null;
            return new { state = ready ? "ready" : "not_ready", processId, profileDirectory = native.ProfileDirectory,
                adapterPath = target, adapterBuild = RuntimeAdapter.Build, installedSha256 = RuntimeAdapter.InstalledHash,
                changed, settingsChanged = wasEnabled != native.Enabled || previousPort != native.Port, previousPort, reloaded = needsReload, installed, wasEnabled, enabled = native.Enabled, port = native.Port, configuredPort, listener, builtin, adapter, problem,
                scope = "Readiness requires the current profile file, enabled engine setting, selected-process listener, successful compilation and matching live adapter handshake. NET API may listen on all interfaces; client requests use loopback. No alternate port or executor." };
        }, ct);
    }
}

[McpServerToolType]
public sealed class WorkbenchRuntimeSetupTools(WorkbenchSession session)
{
    [McpServerTool(Name = "workbench_runtime_setup", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("action=status|enable|configure|install for profile runtime adapter. port applies only to configure and matches server config. install deploys adapter and reloads scripts when needed; stopped Play/saved tabs only, modified files preserved. enable/install accept matching runtimePort. enable/configure/install persist NET API settings; ready indicates a matching handshake.")]
    public async Task<object> Setup(string action, int processId, int? port = null, CancellationToken cancellationToken = default)
    {
        try { return await session.RuntimeSetup(action, processId, cancellationToken, port); }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException)) { throw new McpException(ex.Message); }
    }
}
