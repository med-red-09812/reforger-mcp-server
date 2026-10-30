using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Reflection;
using ReforgerMcp;

if (args is ["--child"])
{
    Thread.Sleep(Timeout.Infinite);
    return;
}
if (args is ["--live-probe", var configurationPath, var editorPid])
{
    var configuration = Configuration.Load(configurationPath);
    using var editor = WorkbenchSession.SelectProcess(configuration, int.Parse(editorPid));
    Console.WriteLine(JsonSerializer.Serialize(NativeWorkbench.Execute(editor, CancellationToken.None, probe: true), Configuration.Json));
    return;
}
if (args is ["--live-open", var launchConfiguration, var installation, var project])
{
    using var workbench = new Workbench(Configuration.Load(launchConfiguration));
    Console.WriteLine(JsonSerializer.Serialize(workbench.OpenProject(project, CancellationToken.None, installation), Configuration.Json));
    return;
}

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
void Refuses(Action action, string message)
{
    try { action(); } catch (InvalidDataException) { checks++; return; }
    catch (ArgumentException) { checks++; return; }
    catch (InvalidOperationException) { checks++; return; }
    throw new Exception(message);
}

void Emission(NativeWorkbenchProfile profile, string method, params object[] arguments)
{
    var type = typeof(NativeWorkbench).GetNestedType("E", BindingFlags.NonPublic)!;
    var emitter = Activator.CreateInstance(type, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
        null, [profile], null)!;
    try
    {
        typeof(NativeWorkbench).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new[] { emitter }.Concat(arguments).ToArray());
        type.GetMethod("Label")!.Invoke(emitter, ["return"]);
        var code = (byte[])type.GetMethod("Done")!.Invoke(emitter, null)!;
        Check(code.Length is > 0 and < 7800, $"{profile.Id}/{method}: callback exceeds dispatch budget.");
    }
    catch (TargetInvocationException ex)
    {
        throw new Exception($"{profile.Id}/{method}: emission/relocation failed", ex.InnerException);
    }
}

var root = Path.GetFullPath(Path.Combine(".temp", "workbench-profile-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var children = new List<Process>();
string Helper(string label)
{
    var directory = Path.Combine(root, label);
    Directory.CreateDirectory(directory);
    foreach (var file in Directory.GetFiles(AppContext.BaseDirectory))
        File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    var executable = Path.Combine(directory, "ArmaReforgerWorkbenchSteamDiag.exe");
    File.Copy(Environment.ProcessPath!, executable);
    return executable;
}
Configuration Reload(Configuration config)
{
    var path = Path.Combine(root, "config.json");
    File.WriteAllText(path, JsonSerializer.Serialize(config, Configuration.Json));
    return Configuration.Load(path);
}
try
{
    foreach (var profile in new[] { NativeWorkbenchProfile.Stable, NativeWorkbenchProfile.Experimental19 })
    {
        var image = new nint(0x140000000L);
        Emission(profile, "EmitProjectContext", image);
        Emission(profile, "EmitChildPrefab", image);
        Emission(profile, "EmitSuppressResourceLeakAssertion", image);
        Emission(profile, "EmitReadResourceLeakAssertion", image);
        Emission(profile, "EmitRuntimeSetup", image, false, null!);
        Emission(profile, "EmitRuntimeSetup", image, true, null!);
        Emission(profile, "EmitRuntimeSetup", image, true, 5776);
        Emission(profile, "EmitOpenWorld", image, false);
        Emission(profile, "EmitOpenWorld", image, true);
        foreach (var action in new[] { "status", "start", "stop" }) Emission(profile, "EmitPlay", image, action);
        foreach (var action in new[] { "inspect", "create", "export", "save_world", "history", "undo", "redo" })
            Emission(profile, "EmitEntity", image, new NativeWorkbench.EntityCommand(action, "fixture:world.ent", "fixture", "GenericEntity", [0, 0, 0], [0, 0, 0]));
        foreach (var action in new[] { "query", "properties", "components", "objects", "set", "clear", "insert_object", "remove_object", "add_component", "remove_component", "save_definition", "insert_value", "remove_value" })
            Emission(profile, "EmitEntity", image, new NativeWorkbench.EntityCommand(action, "fixture:world.ent", "fixture", "GenericEntity", [0, 0, 0], [0, 0, 0]) {
                Target = new(action, Id: 1), PrefabDefinition = action == "save_definition" });
        foreach (var action in new[] { "status", "run", "configure", "stop", "start_peers" })
            Emission(profile, "EmitPlugin", image, new NativeWorkbench.PluginCommand("peers", action));
        foreach (var field in new[] { "executable", "launchDelay", "peer.0.enabled", "peer.0.process" })
            Emission(profile, "EmitPlugin", image, new NativeWorkbench.PluginCommand("peers", "get", field));
        foreach (var action in new[] { "close", "invoke", "menu", "toggle", "set_text", "select", "expand", "collapse", "open_choices", "choose", "add_item" })
            Emission(profile, "EmitUi", new NativeWorkbench.UiCommand(action, 1, 1, "fixture"), new nint(0x180000000L), new nint(0x190000000L), new nint(0x1a0000000L), image);
        Emission(profile, "EmitChoices", new NativeWorkbench.ChoicesCommand(1), image, new nint(0x190000000L), new nint(0x1a0000000L));
        Emission(profile, "EmitChoices", new NativeWorkbench.ChoicesCommand(1, 1, "fixture", "value", [new("label", "value")]), image, new nint(0x190000000L), new nint(0x1a0000000L));
    }
    Check(NativeWorkbenchProfile.Select(NativeWorkbenchProfile.Stable.Sha256.ToUpperInvariant()) == NativeWorkbenchProfile.Stable, "Stable exact hash selection failed.");
    Check(NativeWorkbenchProfile.Select(NativeWorkbenchProfile.Experimental19.Sha256).Experimental, "Experimental exact hash selection failed.");
    Refuses(() => NativeWorkbenchProfile.Select(new string('0', 64)), "Unknown binary accepted by native dispatcher.");
    Check(NativeWorkbenchProfile.Stable.Rva(0x222c70) == 0x222c70, "Stable native address changed.");
    Check(NativeWorkbenchProfile.Experimental19.Rva(0x222c70) == 0x22caf0, "Wrong Experimental reload owner.");
    Check(NativeWorkbenchProfile.Experimental19.Rva(0x27f050) == 0x28a340, "Experimental bypasses full Play owner.");
    Check(NativeWorkbenchProfile.Experimental19.WorldApiSlot(0x318) == 0x320 &&
        NativeWorkbenchProfile.Experimental19.WorldApiSlot(0x320) == 0x330 &&
        NativeWorkbenchProfile.Experimental19.WorldApiSlot(0x478) == 0x498, "Inserted API slots not distinguished.");
    Refuses(() => NativeWorkbenchProfile.Experimental19.WorldApiSlot(0x499), "Unverified native slot accepted.");
    Refuses(() => NativeWorkbenchProfile.Experimental19.Rva(0x123456), "Unmapped address fell back to Stable.");
    var guardFile = Path.Combine(root, "identity-control.bin");
    File.WriteAllBytes(guardFile, [1, 2, 3, 4]);
    using (var verification = new NativeWorkbench.VerificationScope())
    {
        verification.Verify(guardFile, Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])));
        Refuses(() => verification.Verify(guardFile, new string('0', 64)), "Deliberately wrong file hash passed native verification.");
    }
    var logRoot = Path.Combine(root, "logs");
    Directory.CreateDirectory(logRoot);
    var nested = Path.Combine(logRoot, "logs_2026-10-30_12-00-00");
    Directory.CreateDirectory(nested);
    const string lines = "header\nengine identity\n12:00:00.000 SCRIPT : Reloading game scripts\n12:00:01.000 SCRIPT (E): deliberate compile fault\n12:00:02.000 PROFILING : Reloading game scripts took: 2ms\n";
    File.WriteAllText(Path.Combine(nested, "console.log"), lines);
    File.WriteAllText(Path.Combine(logRoot, "console.log"), lines);
    var logReader = new ConsoleLogs(new Configuration { DataDirectory = root, LogDirectories = new() { ["fixture"] = logRoot } });
    var sessions = JsonSerializer.SerializeToElement(logReader.Sessions("fixture", 20), Configuration.Json);
    Check(sessions.GetProperty("sessions").GetArrayLength() == 2, "Direct and dated logs were not both discovered.");
    foreach (var session in new[] { "configured", "logs_2026-10-30_12-00-00" })
    {
        var result = logReader.Read("fixture", session, "latest", 0, "error", null, null, false, 1, 20, null, CancellationToken.None);
        Check(result.TotalLines == 5 && result.Session == session, "Log session layout or line scope changed.");
    }
    Refuses(() => logReader.Read("fixture", "../console.log", "session", 0, null, null, null, false, 1, 20, null, CancellationToken.None), "Log session traversal accepted.");
    var stable = new WorkbenchConfiguration { Executable = Helper("stable"), RuntimePort = 5775 };
    var experimental = new WorkbenchConfiguration { Executable = Helper("experimental"), RuntimePort = 5776 };
    var config = Reload(new Configuration { DataDirectory = Path.Combine(Path.GetTempPath(), "ReforgerMcpProfileTests", Guid.NewGuid().ToString("N")),
        Workbenches = new() { ["stable"] = stable, ["experimental"] = experimental },
        Targets = new() { ["workbench-stable"] = stable.Executable, ["workbench-experimental"] = experimental.Executable },
        GhidraHome = root, JavaHome = root });
    Check(config.SelectWorkbench("stable").RuntimePort == 5775, "Stable port mixed with Experimental.");
    Check(config.SelectWorkbench("experimental").RuntimePort == 5776, "Experimental port mixed with Stable.");
    Refuses(() => config.SelectWorkbench(), "Ambiguous installation silently selected.");
    Refuses(() => config.SelectWorkbench("missing"), "Unknown installation silently selected.");
    Refuses(() => Reload(config with { Workbenches = new() { ["stable"] = stable, ["experimental"] = stable } }), "Duplicate executable accepted.");
    Refuses(() => Reload(config with { Workbenches = new() { ["workbench"] = stable } }), "Reserved installation label accepted.");
    Refuses(() => Reload(config with { Workbenches = new() { ["stable"] = stable with { ProfileDirectory = root },
        ["experimental"] = experimental with { ProfileDirectory = root } } }), "Shared installation profile accepted.");
    Refuses(() => Reload(config with { CaptureExecutables = new() { ["workbench-stable"] = Path.Combine(root, "other.exe") } }), "Conflicting capture label accepted.");
    Refuses(() => Reload(config with { Targets = new() { ["../escape"] = stable.Executable } }), "Unsafe native target accepted.");
    var legacy = Reload(config with { Workbench = stable, Workbenches = [] });
    Check(legacy.SelectWorkbench().Executable == stable.Executable, "Legacy configuration changed.");
    using var lifecycle = new Workbench(config);
    var status = JsonSerializer.SerializeToElement(lifecycle.Status(), Configuration.Json);
    Check(status.GetProperty("installations").GetArrayLength() == 2, "Both installation identities were not reported.");
    foreach (var entry in config.Workbenches.Values)
    {
        var start = new ProcessStartInfo(entry.Executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--child");
        children.Add(Process.Start(start)!);
    }
    foreach (var process in children)
        Check(!process.WaitForExit(250), "Test helper failed to stay alive.");
    using var selectedStable = WorkbenchSession.SelectProcess(config, children[0].Id);
    using var selectedExperimental = WorkbenchSession.SelectProcess(config, children[1].Id);
    Check(config.WorkbenchFor(selectedStable).RuntimePort == 5775, "Stable process resolved Experimental settings.");
    Check(config.WorkbenchFor(selectedExperimental).RuntimePort == 5776, "Experimental process resolved Stable settings.");
    Refuses(() => { using var selected = WorkbenchSession.SelectProcess(config); }, "Multiple editors silently selected.");
    Refuses(() => { using var selected = WorkbenchSession.SelectProcess(legacy, children[1].Id); }, "Same-named foreign executable selected.");
    using var current = Process.GetCurrentProcess();
    Refuses(() => config.WorkbenchFor(current), "Unconfigured process accepted.");
    var pinned = config with { Workbenches = new() { ["stable"] = stable with {
        ProcessId = children[0].Id, ProcessStartedUtc = children[0].StartTime.ToUniversalTime().AddSeconds(-1) } } };
    Refuses(() => { using var selected = WorkbenchSession.SelectProcess(pinned, children[0].Id); }, "Reused PID accepted despite start-time mismatch.");
    Console.WriteLine($"PASS: {checks} native-profile/installation/configuration/process-identity checks. No Workbench was launched or injected.");
}
finally
{
    foreach (var child in children)
    {
        if (!child.HasExited) child.Kill();
        child.WaitForExit();
        child.Dispose();
    }
}
