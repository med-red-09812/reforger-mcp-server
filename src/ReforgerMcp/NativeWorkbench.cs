using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ReforgerMcp;
// Private, exact-build ABI profiles + Qt 6.8.3. See docs/workbench-native-reload.md.
internal static partial class NativeWorkbench
{
    internal sealed record UiCommand(string Action, uint AccessibleId = 0, long WindowHandle = 0, string? Text = null);
    internal sealed record PluginCommand(string Tool, string Action, string? Field = null, string? Value = null);
    private sealed record PluginField(string Kind, params string[] Path) { public int? PeerIndex { get; init; } }
    private static PluginField Field(PluginCommand c)
    {
        if (c.Field?.Split('.') is ["peer", var indexText, var member] && int.TryParse(indexText, out var index) && index is >= 0 and < 32)
        {
            var (kind, name) = member switch
            {
                "profile" => ("string", "Profile"), "parameters" => ("string", "Params"),
                "title" => ("string", "Title"), "enabled" => ("bool", "Enabled"),
                "autoJoin" => ("bool", "RplAutoJoin"), "autoReconnect" => ("bool", "RplAutoReconnect"),
                "disableTimeout" => ("bool", "RplDisableTimeout"), "windowed" => ("bool", "Windowed"),
                "forceUpdate" => ("bool", "ForceUpdate"), "noFocus" => ("bool", "NoFocus"),
                "maxFPS" => ("int", "MaxFPS"), "width" => ("int", "Width"), "height" => ("int", "Height"),
                "x" => ("int", "X"), "y" => ("int", "Y"), "process" => ("process", "Handle"),
                _ => throw new ArgumentException("Unsupported peer setting.")
            };
            return new(kind, c.Tool == "dedicated" ? "m_aPeersConfig" : "PeersWindows", name) { PeerIndex = index };
        }
        return (c.Tool, c.Field) switch
    {
        ("dedicated", "world") => new("string", "m_Config", "m_World"),
        ("dedicated", "executable") => new("string", "m_sExecutable"),
        ("dedicated", "profile") => new("string", "m_sProfile"),
        ("dedicated", "parameters") => new("string", "m_sParams"),
        ("dedicated", "runPeers") => new("bool", "m_bRunPeers"),
        ("dedicated", "maxFPS") => new("int", "m_iMaxFPS"),
        ("dedicated", "peerExecutable") => new("string", "m_sPeerExecutable"),
        ("dedicated", "peerCount") => new("array", "m_aPeersConfig"),
        ("dedicated", "serverProcess") => new("process", "m_ServerHandle"),
        ("peers", "executable") => new("string", "Executable"),
        ("peers", "launchDelay") => new("int", "launchDelay"),
        ("peers", "peerCount") => new("array", "PeersWindows"),
        _ => throw new ArgumentException("Unsupported plugin setting.")
        };
    }
    // One operation can read many settings. Keep verified files open without write
    // or delete sharing, so each exact binary needs hashing only once in that scope.
    internal sealed class VerificationScope : IDisposable
    {
        private readonly Dictionary<string, (FileStream Stream, string Hash)> files = new(StringComparer.OrdinalIgnoreCase);
        internal void Verify(string path, string hash)
        {
            if (!Identify(path).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported native Workbench binary; reanalyze the isolated profile: " + path);
        }
        internal string Identify(string path)
        {
            if (files.TryGetValue(path, out var existing)) return existing.Hash;
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(file));
                files.Add(path, (file, hash));
                return hash;
            }
            catch { file.Dispose(); throw; }
        }
        public void Dispose() { foreach (var file in files.Values) file.Stream.Dispose(); files.Clear(); }
    }
    public static object Execute(Process process, CancellationToken ct, string? world = null, UiCommand? ui = null, string? play = null, PluginCommand? plugin = null, VerificationScope? verification = null, EntityCommand? entity = null, ChildPrefabCommand? childPrefab = null, bool projectContext = false, bool? runtimeSetup = null, bool? resourceLeakAssertion = null, ChoicesCommand? choices = null, int? runtimePort = null, bool probe = false)
    {
        ct.ThrowIfCancellationRequested();
        using var dispatchLease = ui is null ? new DispatchLease(process) : null;
        if (runtimePort is not null && (runtimeSetup != true || runtimePort is < 1 or > 65535)) throw new ArgumentException("Runtime port requires explicit setup and a port from 1 to 65535.");
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("Native Workbench operations require an x64 server.");
        if (ui?.Action is "set_text" or "choose" && (ui.Text is null || ui.Text.Length > 1024 || ui.Text.Contains('\0'))) throw new ArgumentException("set_text requires at most 1024 UTF-16 code units without NUL.");
        if (play is not (null or "status" or "start" or "stop")) throw new ArgumentException("Invalid Play action.");
        if (plugin is not null && (plugin.Tool is not ("dedicated" or "peers") || plugin.Action is not ("status" or "run" or "configure" or "stop" or "start_peers" or "get" or "set" or "clear_closed_server") || plugin.Action == "start_peers" && plugin.Tool != "peers")) throw new ArgumentException("Invalid plugin command.");
        var field = plugin?.Action is "get" or "set" or "clear_closed_server" ? Field(plugin) : null;
        if (plugin?.Action == "set")
        {
            if (plugin.Value is null || plugin.Value.Contains('\0') || System.Text.Encoding.UTF8.GetByteCount(plugin.Value) > 1023) throw new ArgumentException("Setting requires a value of at most 1023 UTF-8 bytes without NUL.");
            if (field!.Kind == "int" && !int.TryParse(plugin.Value, out _)) throw new ArgumentException("Setting requires an integer.");
            if (field.Kind == "bool" && plugin.Value is not ("true" or "false")) throw new ArgumentException("Setting requires true or false.");
            if (field.Kind is "array" or "process") throw new ArgumentException("Peer count and process observations are read-only.");
        }
        var runReload = !probe && world is null && ui is null && play is null && plugin is null && entity is null && childPrefab is null && !projectContext && runtimeSetup is null && resourceLeakAssertion is null && choices is null;
        var suppressResourceLeakAssertion = resourceLeakAssertion == true || runReload || world is not null || ui is not null || play is "start" or "stop";
        using var ownVerification = verification is null ? new VerificationScope() : null;
        void CheckHash(string path, string hash) => (verification ?? ownVerification!).Verify(path, hash);
        var exe = process.MainModule!;
        var profile = NativeWorkbenchProfile.Select((verification ?? ownVerification!).Identify(exe.FileName));
        var qt = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "Qt6Core.dll");
        CheckHash(qt.FileName, "0cc62e841959fb8f6d366ea116c6f6bbc273e30f0ef90d8aa2e4adeb81fd63f8");
        var hp = N.OpenProcess(0x10047a, false, process.Id);
        Check(hp != 0);
        nint released = 0, remoteEvent = 0, data = 0, code = 0;
        bool pending = false, uncertain = false, registered = false, cleaned = false, dispatched = false;
        bool retainedRelease = false, releaseReset = false;
        Action? cleanup = null;
        try
        {
            try
            {
                byte[] Read(nint p, int n) { var b = new byte[n]; Check(N.ReadProcessMemory(hp, p, b, (nuint)n, out var got) && got == (nuint)n); return b; }
                void Write(nint p, byte[] b) => Check(N.WriteProcessMemory(hp, p, b, (nuint)b.Length, out var got) && got == (nuint)b.Length);
                nint Ptr(nint p) => (nint)BitConverter.ToInt64(Read(p, 8));
                var app = Ptr((nint)profile.Address(exe.BaseAddress, 0x3db6598));
                if (Ptr(app) != profile.Address(exe.BaseAddress, 0x2b922c8)) throw new Exception("Application vtable mismatch");
                // app+164e can be set during Play: it blocks reload/world opening,
                // but must not prevent Play observation or its normal stop lifecycle.
                if (ui is null && (Read((nint)profile.Address(exe.BaseAddress, 0x3db65e5), 1)[0] != 0 || (play is null && plugin is null && resourceLeakAssertion is null && Read(app + profile.ApplicationBusyOffset, 1)[0] != 0))) throw new WorkbenchBusyException("Application busy");
                CheckCode(exe, 0x222c70); CheckCode(qt, 0x20980);
                if (suppressResourceLeakAssertion || resourceLeakAssertion is not null)
                {
                    // Validate the complete assertion branch, not just its owning function entry.
                    CheckCode(exe, 0x1a6a881); CheckCode(exe, 0x1a6a8a1); CheckCode(exe, 0x1a6a8bc);
                    if (Read((nint)profile.Address(exe.BaseAddress, ResourceLeakSuppressionRva), 1)[0] > 1)
                        throw new InvalidOperationException("Unsupported resource-leak assertion flag state.");
                }
                if (runtimeSetup is not null)
                {
                    foreach (var rva in new[] { 0x1e90630, 0x57d160, 0x57d1a0, 0x22a320, 0x22c230, 0x22c310, 0x1e32460 }) CheckCode(exe, rva);
                    var settings = Ptr(app + profile.ApplicationSettingsOffset);
                    if (settings == 0 || Ptr(settings) != profile.Address(exe.BaseAddress, 0x3221a60) ||
                        BitConverter.ToInt32(Read((nint)profile.Address(exe.BaseAddress, 0x3b0dbb0), 4)) != 8 ||
                        BitConverter.ToInt32(Read((nint)profile.Address(exe.BaseAddress, 0x3b0dbb4), 4)) != 9)
                        throw new InvalidOperationException("Unsupported Workbench settings layout.");
                }
                if (projectContext)
                    foreach (var rva in new[] { 0x1ea3f00, 0x1ea3e70, 0x2061610, 0x2060e60, 0x2060c80, 0x2060d70, 0x1e90630, 0x3a9a10 }) CheckCode(exe, rva);
                if (world is not null)
                    foreach (var rva in new[] { 0x20ea70, 0x20fec0, 0x2832b0, 0x1e35d50, 0x269dc0, 0x1e90630 }) CheckCode(exe, rva);
                if (childPrefab is not null)
                    foreach (var rva in new[] { 0x1e6ac0, 0x1e35d50, 0x269dc0, 0x1e90630, 0x1f6600, 0x31a780 }) CheckCode(exe, rva);
                if (entity is not null)
                    foreach (var rva in new[] { 0x20ea70, 0x1a861c0, 0x1a86890, 0x1a85560, 0x1a87d70, 0x1a88080, 0x1a88ab0, 0x1a74310, 0x1e90630, 0x1f6600, 0x31a780 }) CheckCode(exe, rva);
                if (entity?.Target is not null)
                    foreach (var rva in new[] { 0x1a88df0, 0x1a88d90, 0x1e6a060, 0x1e69fe0, 0x1e370e0, 0x1e39810, 0x1e32f10, 0x1a8f8e0, 0x1ad60d0, 0x1ad7760, 0x273cd0, 0x1a93450 }) CheckCode(exe, rva);
                if (play is not null)
                    foreach (var rva in new[] { 0x20ea70, 0x27f050, 0x27f070 }) CheckCode(exe, rva);
                if (plugin is not null)
                    foreach (var rva in new[] { 0x20ea70, 0x2eae90, 0x2e9060, 0x2e9510, 0x2e9770, 0x1e9d100, 0x1f6f0b0, 0x1f6e490, 0x1f6ea40 }) CheckCode(exe, rva);
                // Automatic choices already observed their owning World Editor
                // window. Process.MainWindowHandle can transiently be zero when
                // Qt opens/closes a popup; use the exact owner for this operation.
                var threadWindow = choices is null ? process.MainWindowHandle : (nint)choices.Window;
                var uiThread = N.GetWindowThreadProcessId(threadWindow, out var windowPid);
                if (windowPid != process.Id || uiThread == 0)
                {
                    if (choices is not null) throw new WorkbenchBusyException("World Editor window changed before choice refresh.");
                    throw new Exception("No main window");
                }
                nint Export(string name)
                {
                    var local = N.GetProcAddress(N.GetModuleHandle("kernel32.dll"), name);
                    Check(local != 0); Check(N.GetModuleHandleEx(6, local, out var module));
                    var path = new System.Text.StringBuilder(1024); N.GetModuleFileName(module, path, 1024);
                    var remote = process.Modules.Cast<ProcessModule>().Single(m => string.Equals(m.FileName, path.ToString(), StringComparison.OrdinalIgnoreCase));
                    return remote.BaseAddress + (local - module);
                }
                // UI commands may run in a modal loop inside a pending native
                // operation. Each has its own lifetime claim, not the reload lock.
                released = N.CreateEvent(0, true, false, ui is null ? EventName(process) : EventName(process) + ".UI." + Guid.NewGuid().ToString("N"));
                var eventError = Marshal.GetLastWin32Error(); Check(released != 0);
                if (eventError == 183)
                {
                    // The slot destructor signals only after Qt has released the callback.
                    // A timed-out caller may have retained its allocations, but a signaled
                    // event proves that callback can no longer execute. Never infer this
                    // from dialog closure or elapsed time. Retained memory is not freed here.
                    if (N.WaitForSingleObject(released, 0) != 0)
                        throw new InvalidOperationException("Native operation is still active or its release is unverified. Resolve its dialog and inspect the original operation. No call dispatched.");
                    retainedRelease = true;
                }
                Check(N.DuplicateHandle(N.GetCurrentProcess(), released, hp, out remoteEvent, 0, false, 2));
                data = N.VirtualAllocEx(hp, 0, choices is not null ? ChoicesBufferSize : projectContext ? (nuint)ProjectBufferSize : entity?.Target is null ? 4096u : (nuint)(TargetInputOffset + 4096), 0x3000, 4); Check(data != 0);
                code = N.VirtualAllocEx(hp, 0, 16384, 0x3000, 4); Check(code != 0);
                // Data: Qt slot ref+impl; app; reload; event; SetEvent; GetCurrentThreadId;
                // expected thread; actual thread; reload flag; bool result; completion; queue result.
                var bytes = new byte[512];
                void Q(int o, long v) => BitConverter.GetBytes(v).CopyTo(bytes, o);
                Q(0, 1); Q(8, code + 128); Q(16, app); Q(24, profile.Address(exe.BaseAddress, 0x222c70));
                Q(32, remoteEvent); Q(40, Export("SetEvent")); Q(48, Export("GetCurrentThreadId"));
                BitConverter.GetBytes(uiThread).CopyTo(bytes, 56); Q(64, runReload ? 1 : 0);
                Write(data, bytes);
                if (choices is not null) Write(data + ChoicesInputOffset, ChoicesInput(choices));
                if (runtimeSetup is not null)
                {
                    Write(data + 512, System.Text.Encoding.UTF8.GetBytes("$profile:\0"));
                    Write(data + 544, System.Text.Encoding.UTF8.GetBytes("NetAPI\0"));
                }
                if (world is not null) Write(data + 512, System.Text.Encoding.UTF8.GetBytes(world + "\0"));
                if (childPrefab is not null)
                {
                    Write(data + 512, System.Text.Encoding.UTF8.GetBytes(childPrefab.Ancestor + "\0"));
                    Write(data + 1024, System.Text.Encoding.UTF8.GetBytes(childPrefab.Destination + "\0"));
                    Write(data + 2048, System.Text.Encoding.UTF8.GetBytes(childPrefab.Destination + ".meta\0"));
                }
                if (entity is not null)
                {
                    Write(data + 512, System.Text.Encoding.UTF8.GetBytes(entity.World + "\0"));
                    Write(data + 768, System.Text.Encoding.UTF8.GetBytes(entity.Name + "\0"));
                    Write(data + 1024, System.Text.Encoding.UTF8.GetBytes(entity.Asset + "\0"));
                    if (entity.Target?.ExpectedValue is { } expectedValue)
                        Write(data + 4096, System.Text.Encoding.UTF8.GetBytes(expectedValue + "\0"));
                    if (entity.Target?.Path is { } path)
                        for (var i = 0; i < path.Length; i++)
                            Write(data + TargetInputOffset + i * 256, System.Text.Encoding.UTF8.GetBytes(path[i].Property + "\0"));
                    for (var i = 0; i < 3; i++)
                    {
                        Write(data + 1536 + i * 4, BitConverter.GetBytes(entity.Position[i]));
                        Write(data + 1552 + i * 4, BitConverter.GetBytes(entity.Angles[i]));
                    }
                }
                if (plugin?.Action == "start_peers")
                {
                    Write(data + 264, BitConverter.GetBytes((long)(data + 512)));
                    Write(data + 512, System.Text.Encoding.UTF8.GetBytes("Server localhost + PeerTool\0"));
                }
                if (plugin?.Action == "clear_closed_server")
                {
                    if (plugin.Tool != "dedicated" || plugin.Field != "serverProcess") throw new ArgumentException("Invalid closed-handle cleanup.");
                    Write(data + 384, BitConverter.GetBytes((long)Export("GetProcessId")));
                    Write(data + 392, BitConverter.GetBytes((long)Export("GetLastError")));
                }
                if (field is not null)
                {
                    for (var i = 0; i < field.Path.Length; i++) Write(data + 512 + i * 128, System.Text.Encoding.UTF8.GetBytes(field.Path[i] + "\0"));
                    if (plugin!.Action == "set") Write(data + 1024, System.Text.Encoding.UTF8.GetBytes(plugin.Value + "\0"));
                }

                if (ui?.Action is "set_text" or "choose") Write(data + 512, System.Text.Encoding.Unicode.GetBytes(ui.Text! + "\0"));

                // Preserve RBX and shadow space; entity creation needs three stack arguments.
                var helper = new E(); helper.B(0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x89, 0xcb); // rbx=rcx
                helper.B(0x48, 0x8b, 0x4b, 8, 0x48, 0x8b, 0x53, 16, 0x4c, 0x8b, 0x43, 24, 0xff, 0x13, 0x48, 0x89, 0x43, 32);
                helper.B(0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3);
                var cb = new E(profile); cb.B(0x53, 0x48, 0x83, 0xec, 0x40, 0x48, 0x89, 0xd3); // rbx=self; 64-byte aligned call frame
                cb.B(0x83, 0xf9, 0); cb.J(0x84, "destroy"); cb.B(0x83, 0xf9, 1); cb.J(0x85, "return");
                cb.B(0xff, 0x53, 48, 0x89, 0x43, 60, 0x3b, 0x43, 56); cb.J(0x85, "wrong");
                if (suppressResourceLeakAssertion) EmitSuppressResourceLeakAssertion(cb, exe.BaseAddress);
                if (probe)
                    cb.B(0xc7, 0x43, 68, 1, 0, 0, 0); // Harmless owning-thread/lifetime verification, no editor action.
                else if (runReload)
                    cb.B(0x48, 0x8b, 0x4b, 16, 0x31, 0xd2, 0xff, 0x53, 24, 0x0f, 0xb6, 0xc0, 0x89, 0x43, 68);
                else if (ui is not null)
                {
                    var gui = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "Qt6Gui.dll");
                    CheckHash(gui.FileName, "ca9f20d9b59d5bc321a87005b926b853ebcd6a2394823e14d3c8b5fc5702fa8c");
                    var widgets = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "Qt6Widgets.dll");
                    CheckHash(widgets.FileName, "d34ef78b3d3b5506ee8279ce6efbfc263d773b74860af76e66b0715346897a3a");
                    foreach (var rva in new[] { 0x408c70, 0x409930, 0x40b480, 0x40bd20 }) CheckCode(gui, rva);
                    CheckCode(widgets, 0x577a0); CheckCode(widgets, 0x52d70);
                    if (ui.Action is "set_text" or "choose") { CheckCode(qt, 0x16bff0); CheckCode(qt, 0x1fd0); }
                    if (ui.Action is "select" or "expand" or "collapse") { CheckCode(widgets, 0x2ade00); CheckCode(widgets, 0x2c6f40); CheckCode(widgets, 0x33de90); }
                    if (ui.Action is "open_choices" or "choose") { CheckCode(widgets, 0x2bee90); CheckCode(widgets, 0x143ef0); CheckCode(qt, 0x228d0); }
                    if (ui.Action == "add_item")
                    {
                        foreach (var rva in new[] { 0x2a68079, 0x48e210, 0x479130, 0x4e46c0 }) CheckCode(exe, rva);
                        CheckCode(widgets, 0x34c240); CheckCode(widgets, 0x5c820);
                    }
                    EmitUi(cb, ui, gui.BaseAddress, widgets.BaseAddress, qt.BaseAddress, exe.BaseAddress);
                }
                else if (choices is not null)
                {
                    var widgets = process.Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "Qt6Widgets.dll");
                    CheckHash(widgets.FileName, "d34ef78b3d3b5506ee8279ce6efbfc263d773b74860af76e66b0715346897a3a");
                    foreach (var rva in new[] { 0x472240, 0x478730, 0x47a630 }) CheckCode(exe, rva);
                    foreach (var rva in new[] { 0x22910, 0x1a7e10, 0x38f0, 0xe5400 }) CheckCode(qt, rva);
                    EmitChoices(cb, choices, exe.BaseAddress, widgets.BaseAddress, qt.BaseAddress);
                }
                else if (resourceLeakAssertion is not null) EmitReadResourceLeakAssertion(cb, exe.BaseAddress);
                else if (runtimeSetup is not null) EmitRuntimeSetup(cb, exe.BaseAddress, runtimeSetup.Value, runtimePort);
                else if (projectContext) EmitProjectContext(cb, exe.BaseAddress);
                else if (childPrefab is not null) EmitChildPrefab(cb, exe.BaseAddress);
                else if (entity is not null) EmitEntity(cb, exe.BaseAddress, entity);
                else if (plugin is not null) EmitPlugin(cb, exe.BaseAddress, plugin);
                else if (play is not null) EmitPlay(cb, exe.BaseAddress, play);
                else EmitOpenWorld(cb, exe.BaseAddress, world?.EndsWith(".et", StringComparison.OrdinalIgnoreCase) == true);
                cb.B(0x83, 0x7b, 72, 0); cb.J(0x85, "return");
                cb.B(0xc7, 0x43, 72, 2, 0, 0, 0); cb.J(0x84, "return", true);
                cb.Label("wrong"); cb.B(0xc7, 0x43, 72, 3, 0, 0, 0);
                cb.Label("return"); cb.B(0x48, 0x83, 0xc4, 0x40, 0x5b, 0xc3);
                // Tailcall into stable OS code: when the event is signalled, no instruction/return
                // address references this allocation. The controller can safely release it.
                cb.Label("destroy"); cb.B(0x48, 0x8b, 0x4b, 32, 0x48, 0x8d, 0x43, 40, 0x48, 0x83, 0xc4, 0x40, 0x5b, 0xff, 0x20);
                var queue = new E(); queue.B(0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x89, 0xcb, 0x48, 0x89, 0xda, 0x48, 0x8b, 0x4b, 16);
                queue.B(0x41, 0xb8, 2, 0, 0, 0, 0x45, 0x31, 0xc9, 0x48, 0xb8); queue.Q(qt.BaseAddress + 0x20980);
                queue.B(0xff, 0xd0, 0x0f, 0xb6, 0xc0, 0x89, 0x43, 76, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3);
                var executable = new byte[16384];
                var h = helper.Done(); var c = cb.Done(); var q = queue.Done();
                if (c.Length > 8064) throw new Exception("callback too large");
                h.CopyTo(executable, 0); c.CopyTo(executable, 128); q.CopyTo(executable, 8192);
                // RUNTIME_FUNCTION records and UNWIND_INFO (UWOP_ALLOC_SMALL 32, UWOP_PUSH_NONVOL RBX).
                var starts = new[] { 0, 128, 8192 }; var sizes = new[] { h.Length, c.Length, q.Length };
                for (var i = 0; i < 3; i++) { BitConverter.GetBytes(starts[i]).CopyTo(executable, 8448 + i * 12); BitConverter.GetBytes(starts[i] + sizes[i]).CopyTo(executable, 8452 + i * 12); BitConverter.GetBytes(i == 1 ? 8520 : 8512).CopyTo(executable, 8456 + i * 12); }
                new byte[] { 1, 5, 2, 0, 5, 0x32, 1, 0x30 }.CopyTo(executable, 8512);
                new byte[] { 1, 5, 2, 0, 5, 0x72, 1, 0x30 }.CopyTo(executable, 8520);
                Write(code, executable); Check(N.VirtualProtectEx(hp, code, 16384, 0x20, out _)); Check(N.FlushInstructionCache(hp, code, 16384));
                void Remote(nint start, nint argument, bool queue = false)
                {
                    var thread = N.CreateRemoteThread(hp, 0, 0, start, argument, 0, out _); Check(thread != 0);
                    uncertain = true;
                    if (queue) { pending = true; dispatched = true; }
                    try
                    {
                        var wait = N.WaitForSingleObject(thread, 5000);
                        if (wait != 0) throw new Exception("Remote thread outcome unknown; allocations retained. Restart Workbench before retrying. Wait=" + wait);
                        Check(N.GetExitCodeThread(thread, out var exit));
                        if (exit >= 0x80000000) throw new Exception("Remote thread exception: " + exit.ToString("x"));
                        uncertain = false;
                    }
                    finally { N.CloseHandle(thread); }
                }
                long Call(string function, long a, long b = 0, long cc = 0)
                {
                    var x = new byte[40]; BitConverter.GetBytes((long)Export(function)).CopyTo(x, 0);
                    BitConverter.GetBytes(a).CopyTo(x, 8); BitConverter.GetBytes(b).CopyTo(x, 16); BitConverter.GetBytes(cc).CopyTo(x, 24);
                    Write(data + 128, x); Remote(code, data + 128);
                    try { return BitConverter.ToInt64(Read(data + 160, 8)); } catch { uncertain = true; throw; }
                }
                cleanup = Cleanup;
                registered = true;
                if ((Call("RtlAddFunctionTable", code + 8448, 3, code) & 255) == 0) { registered = false; throw new Exception("Unwind registration failed"); }
                ct.ThrowIfCancellationRequested();
                var foregroundBefore = N.GetForegroundWindow();
                var foregrounds = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
                using var monitoring = new CancellationTokenSource();
                var monitor = Task.Run(() => { while (!monitoring.IsCancellationRequested) {
                    var hwnd = N.GetForegroundWindow();
                    var observed = N.GetWindowThreadProcessId(hwnd, out var p) != 0;
                    foregrounds.TryAdd(hwnd, observed ? (int)p : 0);
                    Thread.Sleep(5);
                } });
                var watch = Stopwatch.StartNew();
                try
                {
                    if (retainedRelease) { Check(N.ResetEvent(released)); releaseReset = true; }
                    Remote(code + 8192, data, true);
                    while (true)
                    {
                        var wait = N.WaitForSingleObject(released, 50);
                        if (wait == 0) { pending = false; break; }
                        if (wait != 258 || process.HasExited || ct.IsCancellationRequested || watch.Elapsed.TotalSeconds > 180)
                            throw new InvalidOperationException("Native operation outcome unknown. Resolve Workbench dialogs and inspect state; never replay the mutation. A later operation may proceed only after the callback release is verified. Temporary native allocations remain retained until process exit.");
                    }
                }
                finally { monitoring.Cancel(); monitor.GetAwaiter().GetResult(); }
                var result = Read(data, entity?.Target is null ? 128 : 512);
                var loadedPath = world is null && entity is null ? null : System.Text.Encoding.UTF8.GetString(Read(data + 2048, 2048)).Split('\0')[0];
                object? fieldValue = field is null ? null : field.Kind == "string"
                    ? System.Text.Encoding.UTF8.GetString(Read(data + 2048, 2048)).Split('\0')[0]
                    : field.Kind == "bool" ? BitConverter.ToInt32(result, 108) != 0 : BitConverter.ToInt32(result, 108);
                if (field?.Kind == "process") fieldValue = MultiplayerProcess.Observe(hp, (nint)BitConverter.ToInt64(result, 112));
                var targets = entity?.Target is null ? null : ReadTargets(result, Read(data + 4096, TargetPageSize * TargetRowSize), loadedPath, watch.ElapsedMilliseconds);
                var project = projectContext ? ReadProjectContext(result, Read(data + 4096, ProjectBufferSize - 4096)) : null;
                var setup = runtimeSetup is null ? null : ReadRuntimeSetup(result, Read(data + 1024, 1024));
                var choiceResult = choices is null ? null : ReadChoices(result, Read(data + ChoicesOutputOffset, ChoicesCapacity * ChoicesRowSize));
                Cleanup();
                if (choiceResult is not null) return choiceResult;
                if (setup is not null) return setup;
                if (project is not null) return project;
                if (targets is not null) return targets;
                var completed = BitConverter.ToUInt32(result, 72);
                var succeeded = BitConverter.ToUInt32(result, 68) == 1;
                var resourceAcknowledged = BitConverter.ToInt32(result, 116) == 1;
                if (resourceLeakAssertion is not null)
                    return new { state = completed == 2 ? resourceLeakAssertion.Value ? succeeded ? "suppressed" : "suppression_failed" : "observed" : "outcome_unknown",
                        processId = process.Id, suppressed = completed == 2 ? BitConverter.ToInt32(result, 68) == 1 : (bool?)null,
                        assertion = "Resources are leaking! Check log!", scope = "This Workbench process until exit; leak diagnostics remain enabled.",
                        expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), allocationsReleased = true };
                if (childPrefab is not null)
                    return ChildPrefabResult(childPrefab, process.Id, completed, succeeded, resourceAcknowledged, watch.ElapsedMilliseconds);
                if (entity?.Action == "save_world")
                    return new { state = completed switch {
                        2 => !succeeded ? "save_failed" : BitConverter.ToInt32(result, 108) != 0 ? "save_incomplete" : "saved",
                        4 => "incompatible_editor", 5 => "editor_busy", 7 => "world_unavailable", 8 => "world_changed",
                        21 => "prefab_mode_unsupported", _ => "outcome_unknown" },
                        processId = process.Id, world = loadedPath, nativeResult = completed == 2 ? succeeded : (bool?)null,
                        modifiedAfter = completed == 2 ? BitConverter.ToInt32(result, 108) != 0 : (bool?)null,
                        elapsedMs = watch.ElapsedMilliseconds, allocationsReleased = true };
                if (entity?.Action is "undo" or "redo" or "history")
                {
                    var before = BitConverter.ToInt32(result, 104);
                    var after = BitConverter.ToInt32(result, 108);
                    var available = BitConverter.ToInt32(result, entity.Action == "redo" ? 116 : 112) != 0;
                    return new { state = completed switch {
                        2 => entity.Action == "history" ? "observed" : !available ? "nothing_to_" + entity.Action
                            : before != after ? "updated" : "history_unchanged",
                        4 => "incompatible_editor", 5 => "editor_busy", 7 => "world_unavailable", 8 => "world_changed",
                        21 => "prefab_mode_unsupported", _ => "outcome_unknown" },
                        processId = process.Id, world = loadedPath, action = entity.Action,
                        beforeIndex = completed == 2 ? before : (int?)null, afterIndex = completed == 2 ? after : (int?)null,
                        couldUndoBefore = completed == 2 ? BitConverter.ToInt32(result, 112) != 0 : (bool?)null,
                        couldRedoBefore = completed == 2 ? BitConverter.ToInt32(result, 116) != 0 : (bool?)null,
                        elapsedMs = watch.ElapsedMilliseconds, allocationsReleased = true };
                }
                var prefabResource = entity?.Action == "export" && completed == 2 && succeeded && File.Exists(entity.Asset) && File.Exists(entity.Asset + ".meta")
                    ? System.Text.RegularExpressions.Regex.Match(File.ReadAllText(entity.Asset + ".meta"), "(?mi)^\\s*Name\\s+\"(\\{[0-9A-Fa-f]{16}\\}[^\"]+\\.et)\"").Groups[1].Value : null;
                if (entity is not null) return new {
                    state = completed switch { 2 => entity.Action == "inspect" ? "observed" : entity.Action == "export" ? succeeded ? !resourceAcknowledged || string.IsNullOrEmpty(prefabResource) ? "export_incomplete" : "exported" : "export_failed" : succeeded ? "created" : "creation_failed", 4 => "incompatible_editor", 5 => "editor_busy", 7 => "world_unavailable", 8 => "world_changed", 9 => "entity_name_exists", 10 => "edit_action_rejected", 11 => "layer_locked", 12 => "entity_missing", 13 => "destination_exists", 14 => "edit_action_incomplete", _ => "outcome_unknown" },
                    processId = process.Id, world = loadedPath, entityName = entity.Name,
                    prefabPath = entity.Action == "export" ? entity.Asset : null, prefabResource,
                    resourceAcknowledged = entity.Action == "export" ? resourceAcknowledged : (bool?)null,
                    historyNotice = entity.Action == "export" ? "Export writes an asset outside world undo. Workbench resource processing can clear preceding edit history. Export completion waits for resource database acknowledgement before follow-up edits." : null,
                    entityFound = BitConverter.ToInt64(result, 104) != 0, layerId = BitConverter.ToInt32(result, 112),
                    expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), completed,
                    allocationsReleased = true };
                if (probe) return new { state = completed == 2 && succeeded ? "probed" : "probe_failed", processId = process.Id,
                    profile = profile.Id, expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), allocationsReleased = true };
                if (field is not null) return new { state = completed == 2 && succeeded ? plugin!.Action == "get" ? "observed" : "updated" : "setting_unavailable", processId = process.Id, tool = plugin!.Tool, field = plugin.Field, value = succeeded ? fieldValue : null, nativeType = BitConverter.ToUInt32(result, 104), expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), allocationsReleased = true };
                if (play is not null)
                {
                    var observed = BitConverter.ToUInt32(result, 112) == 1;
                    var before = observed ? BitConverter.ToUInt32(result, 104) != 0 : (bool?)null;
                    var after = observed ? BitConverter.ToUInt32(result, 108) != 0 : (bool?)null;
                    return new { state = completed switch {
                        2 => play == "status" ? "observed" : before == after
                            ? after == (play == "start") ? "already_" + (after == true ? "playing" : "stopped") : "transition_failed"
                            : after == (play == "start") ? (after == true ? "started" : "stopped") : "transition_failed",
                        4 => "incompatible_editor", 5 => "editor_busy_or_unsaved", 7 => "world_unavailable", _ => "outcome_unknown" },
                        processId = process.Id, action = play, wasPlaying = before, isPlaying = after,
                        expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), completed,
                        queued = BitConverter.ToUInt32(result, 76), elapsedMs = watch.ElapsedMilliseconds,
                        foregroundBefore = (long)foregroundBefore, foregroundAfter = (long)N.GetForegroundWindow(), foregrounds, allocationsReleased = true };
                }
                if (world?.EndsWith(".et", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var prefabMode = BitConverter.ToInt32(result, 104) == 1;
                    var rootId = BitConverter.ToUInt64(result, 112);
                    return new { state = completed switch { 2 => succeeded && prefabMode && rootId != 0 ? "opened" : "prefab_open_unverified",
                        4 => "incompatible_editor", 5 => "editor_busy_or_unsaved", 6 => "unsupported_resource_path", _ => "outcome_unknown" },
                        processId = process.Id, requestedPrefab = world, prefabMode,
                        prefabResource = succeeded && prefabMode && rootId != 0 ? world : null,
                        rootEntityId = rootId != 0 ? rootId.ToString("X16") : null,
                        elapsedMs = watch.ElapsedMilliseconds, allocationsReleased = true };
                }
                return new { state = completed switch { 2 => plugin is not null ? (succeeded ? plugin.Action == "status" ? "available" : "invoked" : "plugin_unavailable") : ui is not null ? (succeeded ? "invoked" : "control_unavailable") : runReload ? (succeeded ? "reloaded" : "reload_failed") : (succeeded ? "opened" : "open_failed"), 4 => "incompatible_editor", 5 => "editor_busy_or_unsaved", 6 => "unsupported_world_path", _ => "outcome_unknown" }, processId = process.Id, runReload, plugin = plugin?.Tool, pluginAction = plugin?.Action, uiAction = ui?.Action, requestedWorld = world, loadedWorld = loadedPath, expectedThread = uiThread, actualThread = BitConverter.ToUInt32(result, 60), completed, nativeResult = BitConverter.ToUInt32(result, 68), queued = BitConverter.ToUInt32(result, 76), elapsedMs = watch.ElapsedMilliseconds, foregroundBefore = (long)foregroundBefore, foregroundAfter = (long)N.GetForegroundWindow(), foregrounds, allocationsReleased = true };
                void Cleanup()
                {
                    if (cleaned || pending || uncertain) return;
                    if (registered)
                    {
                        if ((Call("RtlDeleteFunctionTable", code + 8448) & 255) == 0) { uncertain = true; throw new Exception("Unwind deregistration failed; restart Workbench before retrying."); }
                        registered = false;
                    }
                    if (code != 0) { Check(N.VirtualFreeEx(hp, code, 0, 0x8000)); code = 0; }
                    if (data != 0) { Check(N.VirtualFreeEx(hp, data, 0, 0x8000)); data = 0; }
                    if (remoteEvent != 0) { Check(N.DuplicateHandle(hp, remoteEvent, N.GetCurrentProcess(), out var closed, 0, false, 3)); N.CloseHandle(closed); remoteEvent = 0; }
                    cleaned = true;
                }
                void CheckCode(ProcessModule module, int rva)
                {
                    if (module.BaseAddress == exe.BaseAddress) rva = profile.Rva(rva);
                    using var stream = File.OpenRead(module.FileName); using var pe = new PEReader(stream);
                    var section = pe.PEHeaders.SectionHeaders.Single(s => rva >= s.VirtualAddress && rva < s.VirtualAddress + s.VirtualSize);
                    stream.Position = section.PointerToRawData + rva - section.VirtualAddress; var expected = new byte[32]; stream.ReadExactly(expected);
                    if (!Read(module.BaseAddress + rva, 32).SequenceEqual(expected)) throw new Exception("Native code mismatch");
                }
            }
            finally
            {
                // If dispatch/registration is uncertain, retain target-owned allocations and the
                // named claim until Workbench exits. Never free a potentially executing callback.
                try
                {
                    // A failed queue-thread creation did not consume the old release.
                    // Restore it so the retained remote handle cannot strand the claim.
                    if (releaseReset && !dispatched) Check(N.SetEvent(released));
                    if (!cleaned && !pending && !uncertain && cleanup is not null) cleanup();
                    if (!cleaned && !pending && !uncertain && !registered)
                    {
                        if (code != 0) N.VirtualFreeEx(hp, code, 0, 0x8000);
                        if (data != 0) N.VirtualFreeEx(hp, data, 0, 0x8000);
                        if (remoteEvent != 0 && N.DuplicateHandle(hp, remoteEvent, N.GetCurrentProcess(), out var closed, 0, false, 3)) N.CloseHandle(closed);
                    }
                }
                finally { if (released != 0) N.CloseHandle(released); N.CloseHandle(hp); }
            }
        }
        catch (Exception ex) when (dispatched)
        {
            // Includes exceptions from final cleanup. A released Qt event proves
            // allocation lifetime safety, not that the caller received the result.
            throw new InvalidOperationException("Native operation dispatch was attempted; operation outcome or cleanup may be unknown. Inspect Workbench logs/state and do not blindly retry. " + ex.Message, ex);
        }
    }
    private static void EmitUi(E e, UiCommand command, nint gui, nint widgets, nint core, nint image)
    {
        void Call(nint address) { e.B(0x48, 0xb8); e.Q(address); e.B(0xff, 0xd0); }
        if (command.Action == "close")
        {
            e.B(0x48, 0xb9); e.Q(command.WindowHandle); Call(widgets + 0x577a0); // QWidget::find(WId)
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
            e.B(0x48, 0x89, 0xc1); Call(widgets + 0x52d70); // normal QWidget::close; can prompt/veto
            e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 68);
        }
        else
        {
            e.B(0xb9); e.B(BitConverter.GetBytes(command.AccessibleId)); Call(gui + 0x408c70);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
            e.B(0x48, 0x89, 0x43, 96, 0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0xff, 0x50, 8, 0x84, 0xc0); // isValid()
            e.J(0x84, "uiDone");
            if (command.Action is "select" or "expand" or "collapse" or "open_choices" or "choose" or "add_item")
            {
                // Exact Qt 6.8.3 QAccessibleTableCell layout, checked by RTTI-derived
                // primary vtable. QPointer view.value +0x20; persistent index +0x28.
                // Its data begins with QModelIndex; isValid above checks both lifetimes.
                e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0xb8); e.Q(widgets + 0x4b2b90);
                e.B(0x48, 0x39, 0x01); e.J(0x85, "uiDone");
                e.B(0x48, 0x8b, 0x51, 0x28, 0x48, 0x8b, 0x49, 0x20);
                // QModelIndex is a 24-byte value. Copy before signals can invalidate
                // the accessible cell / persistent-index owner during the mutation.
                e.B(0x48, 0x8b, 0x02, 0x48, 0x89, 0x83, 0x20, 1, 0, 0,
                    0x48, 0x8b, 0x42, 8, 0x48, 0x89, 0x83, 0x28, 1, 0, 0,
                    0x48, 0x8b, 0x42, 16, 0x48, 0x89, 0x83, 0x30, 1, 0, 0,
                    0x48, 0x8d, 0x93, 0x20, 1, 0, 0);
                if (command.Action == "add_item") EmitArrayAdd(e, image, widgets);
                else if (command.Action is "open_choices" or "choose")
                {
                    Call(widgets + 0x2bee90); // Row's native editor widget
                    e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
                    e.B(0x48, 0x89, 0xc1, 0x48, 0x8d, 0x93, 0x80, 1, 0, 0);
                    e.B(0x49, 0xb8); e.Q(widgets + 0x487cf0); // QComboBox
                    e.B(0x41, 0xb9, 1, 0, 0, 0); Call(core + 0x228d0);
                    e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
                    if (command.Action == "open_choices") { e.B(0x48, 0x89, 0xc1); Call(widgets + 0x143ef0); }
                    else
                    {
                        e.B(0x48, 0x89, 0x43, 112);
                        e.B(0x48, 0x8d, 0x8b, 0, 1, 0, 0, 0x48, 0x8d, 0x93, 0, 2, 0, 0, 0x41, 0xb8);
                        e.B(BitConverter.GetBytes(command.Text!.Length)); Call(core + 0x16bff0);
                        e.B(0x48, 0x8b, 0x4b, 112, 0x48, 0x8d, 0x93, 0, 1, 0, 0, 0x41, 0xb8, 16, 0, 0, 0);
                        Call(widgets + 0xa04f0); // findText, exact case-sensitive label
                        e.B(0x89, 0x43, 120, 0x48, 0x8d, 0x8b, 0, 1, 0, 0); Call(core + 0x1fd0);
                        e.B(0x83, 0x7b, 120, 0); e.J(0x8c, "uiDone");
                        e.B(0x48, 0x8b, 0x4b, 112, 0x8b, 0x53, 120); Call(widgets + 0x141ba0);
                        e.B(0x48, 0x8b, 0x4b, 112); Call(widgets + 0x13c120);
                        // Emit the same activation signal as selecting a popup item;
                        // the owning property editor performs its normal commit/undo.
                        e.B(0x48, 0x8b, 0x4b, 112, 0x8b, 0x53, 120); Call(widgets + 0x137e10);
                    }
                }
                else if (command.Action == "select") Call(widgets + 0x2c6f40); // QAbstractItemView::setCurrentIndex
                else { e.B(0x41, 0xb8); e.B(BitConverter.GetBytes(command.Action == "expand" ? 1 : 0)); Call(widgets + 0x33de90); }
            }
            else if (command.Action == "set_text")
            {
                // Construct a real QString from bounded UTF-16 in this allocation.
                e.B(0x48, 0x8d, 0x8b, 0, 1, 0, 0, 0x48, 0x8d, 0x93, 0, 2, 0, 0, 0x41, 0xb8);
                e.B(BitConverter.GetBytes(command.Text!.Length)); Call(core + 0x16bff0);
                e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0x8b, 0x01, 0xba, 2, 0, 0, 0, 0x4c, 0x8d, 0x83, 0, 1, 0, 0, 0xff, 0x50, 0x60); // setText(Value, QString const&)
                e.B(0x48, 0x8d, 0x8b, 0, 1, 0, 0); Call(core + 0x1fd0); // ~QString
            }
            else
            {
                var actionRva = command.Action switch { "invoke" => 0x409930, "menu" => 0x40b480, "toggle" => 0x40bd20, _ => throw new ArgumentException("Unsupported Qt action") };
                Call(gui + actionRva); e.B(0x48, 0x89, 0x43, 104);
                e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0x8b, 0x01, 0xba, 3, 0, 0, 0, 0xff, 0x90, 0x98, 0, 0, 0); // interface_cast(ActionInterface)
                e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
                e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8b, 0x53, 104, 0xff, 0x50, 0x20); // doAction(QString const&)
            }
            e.B(0xc7, 0x43, 68, 1, 0, 0, 0);
        }
        e.Label("uiDone");
    }
    private static void EmitPlugin(E e, nint image, PluginCommand command)
    {
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        // Resolve the existing World Editor and its own loaded plugin instances.
        // No creation, menu traversal, registry substitution, or cached object pointer.
        e.B(0x48, 0x8b, 0x4b, 16, 0x48, 0xba); e.Image(image, 0x2ba4318); Call(0x20ea70);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "pluginDone");
        e.B(0x83, 0x78, 0x24, 1); e.J(0x85, "pluginDone");
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x31, 0xd2, 0xff, 0x90, 0xd8, 0, 0, 0);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "pluginDone");
        e.B(0x48, 0xba); e.Image(image, 0x2ba8d78);
        e.B(0x48, 0x39, 0x10); e.J(0x85, "pluginIncompatible");
        e.B(0x48, 0x8b, 0x40, 0x78, 0x48, 0x85, 0xc0); e.J(0x84, "pluginDone");
        e.B(0x48, 0xba); e.Image(image, 0x2bab900);
        e.B(0x48, 0x39, 0x10); e.J(0x85, "pluginIncompatible");
        e.B(0x44, 0x8b, 0x58, 0x6c, 0x41, 0x81, 0xfb, 0, 2, 0, 0); e.J(0x87, "pluginIncompatible");
        e.B(0x4c, 0x8b, 0x50, 0x60, 0x45, 0x31, 0xc9);
        e.Label("pluginLoop");
        e.B(0x45, 0x39, 0xd9); e.J(0x83, "pluginDone");
        e.B(0x4b, 0x8b, 0x0c, 0xca, 0x48, 0x85, 0xc9); e.J(0x84, "pluginNext");
        e.B(0x48, 0x8b, 0x51, 8, 0x48, 0x8b, 0x52, 16);
        var name = System.Text.Encoding.ASCII.GetBytes((command.Tool == "dedicated" ? "SCR_DedicatedServerPlugin" : "PeerPlugin") + "\0");
        var nameOffset = 0;
        for (; nameOffset + 8 <= name.Length; nameOffset += 8)
        {
            e.B(0x48, 0xb8); e.Q(BitConverter.ToInt64(name, nameOffset));
            e.B(0x48, 0x39, 0x42, (byte)nameOffset); e.J(0x85, "pluginNext");
        }
        for (; nameOffset < name.Length; nameOffset++) { e.B(0x80, 0x7a, (byte)nameOffset, name[nameOffset]); e.J(0x85, "pluginNext"); }
        if (command.Action is "get" or "set" or "clear_closed_server")
        {
            var field = Field(command);
            for (var i = 0; i < field.Path.Length; i++)
            {
                // Engine reflection returns the field storage and its type descriptor.
                e.B(0x48, 0x8d, 0x93, 0x40, 1, 0, 0, 0x4c, 0x8d, 0x83); e.B(BitConverter.GetBytes(512 + i * 128)); Call(0x1e9d100);
                e.B(0x48, 0x8b, 0x8b, 0x40, 1, 0, 0, 0x48, 0x85, 0xc9); e.J(0x84, "pluginDone");
                e.B(0x48, 0x8b, 0x93, 0x48, 1, 0, 0, 0x48, 0x85, 0xd2); e.J(0x84, "pluginDone");
                if (i < field.Path.Length - 1)
                {
                    Call(0x1f6f0b0); e.B(0x48, 0x89, 0xc1, 0x48, 0x85, 0xc9); e.J(0x84, "pluginDone");
                    if (field.PeerIndex is { } index)
                    {
                        e.B(0x83, 0x79, 0x34, (byte)index); e.J(0x86, "pluginDone");
                        e.B(0x48, 0x8b, 0x49, 0x28, 0x48, 0x8b, 0x89); e.B(BitConverter.GetBytes(index * 8));
                        e.B(0x48, 0x85, 0xc9); e.J(0x84, "pluginDone");
                        // Confirm the array element's reflected class before resolving members.
                        e.B(0x48, 0x8b, 0x41, 8, 0x48, 0x8b, 0x40, 16, 0x48, 0xba); e.Q(BitConverter.ToInt64(System.Text.Encoding.ASCII.GetBytes("PeerConf")));
                        e.B(0x48, 0x39, 0x10); e.J(0x85, "pluginDone");
                        e.B(0x66, 0x81, 0x78, 8, 0x69, 0x67); e.J(0x85, "pluginDone");
                        e.B(0x80, 0x78, 10, 0); e.J(0x85, "pluginDone");
                    }
                }
            }
            e.B(0x8b, 0x42, 16, 0x89, 0x43, 104);
            if (field.Kind == "string")
            {
                e.B(0x48, 0x89, 0xd1); Call(0x1f6e490); e.B(0x84, 0xc0); e.J(0x84, "pluginDone");
                if (command.Action == "set")
                {
                    // Engine-owned string assignment copies the input and manages lifetime.
                    e.B(0x48, 0x8d, 0x8b, 0, 4, 0, 0, 0x48, 0x8b, 0x93, 0x48, 1, 0, 0,
                        0x4c, 0x8b, 0x83, 0x40, 1, 0, 0, 0x41, 0xb9, 1, 0, 0, 0); Call(0x1f6ea40);
                }
                e.B(0x48, 0x8b, 0x83, 0x40, 1, 0, 0, 0x48, 0x8b, 0x00, 0x48, 0x85, 0xc0); e.J(0x84, "fieldSuccess");
                e.B(0x45, 0x31, 0xc9);
                e.Label("fieldCopy");
                e.B(0x42, 0x8a, 0x14, 8, 0x42, 0x88, 0x94, 0x0b, 0, 8, 0, 0, 0x84, 0xd2); e.J(0x84, "fieldSuccess");
                e.B(0x41, 0xff, 0xc1, 0x41, 0x81, 0xf9, 0xff, 7, 0, 0); e.J(0x82, "fieldCopy");
                e.J(0x84, "pluginDone", true); // bounded output: never report a truncated setting as complete
            }
            else
            {
                if (field.Kind == "process")
                {
                    e.B(0x3d, 0xad, 1, 0, 0x20); e.J(0x85, "pluginDone");
                    e.B(0x48, 0x8b, 0x01, 0x48, 0x89, 0x43, 112);
                    if (command.Action == "clear_closed_server")
                    {
                        // The built-in End closes the OS handle but leaves this field.
                        // Clear only a handle Windows proves is invalid for a process;
                        // never close a recycled handle or clear a live process handle.
                        e.B(0x48, 0x89, 0xc1, 0xff, 0x93, 0x80, 1, 0, 0, 0x85, 0xc0); e.J(0x85, "fieldSuccess");
                        e.B(0xff, 0x93, 0x88, 1, 0, 0, 0x83, 0xf8, 6); e.J(0x85, "fieldSuccess");
                        e.B(0x48, 0x8b, 0x8b, 0x40, 1, 0, 0, 0x48, 0xc7, 0x01, 0, 0, 0, 0);
                    }
                }
                else if (field.Kind == "array")
                {
                    Call(0x1f6f0b0); e.B(0x48, 0x85, 0xc0); e.J(0x84, "fieldSuccess");
                    e.B(0x48, 0x89, 0x43, 112, 0x8b, 0x40, 0x34, 0x89, 0x43, 108);
                }
                else
                {
                    e.B(0x3d); e.B(BitConverter.GetBytes(field.Kind == "bool" ? 0x20000007 : 0x20000000)); e.J(0x85, "pluginDone");
                    if (command.Action == "set")
                    {
                        e.B(0xc7, 0x01); e.B(BitConverter.GetBytes(field.Kind == "bool" ? command.Value == "true" ? 1 : 0 : int.Parse(command.Value!)));
                    }
                    e.B(0x8b, 0x01, 0x89, 0x43, 108);
                }
            }
            e.Label("fieldSuccess");
        }
        else if (command.Action == "start_peers")
        {
            // The existing PeerPlugin lifecycle entry delegates to its Start(),
            // preserving launchDelay, enabled peers, process handles and CLI assembly.
            // Only this plugin receives the event; no editor Play state is changed.
            e.B(0x48, 0x8d, 0x93, 0, 1, 0, 0, 0x4c, 0x8d, 0x83, 8, 1, 0, 0, 0x45, 0x31, 0xc9,
                0x48, 0x8d, 0x83, 0x20, 1, 0, 0, 0x48, 0x89, 0x44, 0x24, 0x20, 0x48, 0x89, 0x44, 0x24, 0x28);
            Call(0x2e9770);
        }
        else if (command.Action != "status")
            Call(command.Action switch { "run" => 0x2eae90, "configure" => 0x2e9060, "stop" => 0x2e9510, _ => throw new ArgumentException("Invalid plugin action") });
        e.B(0xc7, 0x43, 68, 1, 0, 0, 0); e.J(0x84, "pluginDone", true);
        e.Label("pluginNext"); e.B(0x41, 0xff, 0xc1); e.J(0x84, "pluginLoop", true);
        e.Label("pluginIncompatible"); e.B(0xc7, 0x43, 72, 4, 0, 0, 0);
        e.Label("pluginDone");
    }
    private static void EmitPlay(E e, nint image, string action)
    {
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        void Playing() { e.B(0x48, 0x8b, 0x4b, 88, 0x48, 0x81, 0xc1, 0xa0, 0, 0, 0, 0x48, 0x8b, 0x01, 0xff, 0x90, 0x98, 0, 0, 0, 0x0f, 0xb6, 0xc0); }
        // Only obtain an existing World Editor; status/start never open a world.
        e.B(0x48, 0x8b, 0x4b, 16, 0x48, 0xba); e.Image(image, 0x2ba4318); Call(0x20ea70);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "playMissing");
        e.B(0x83, 0x78, 0x24, 1); e.J(0x85, "playMissing");
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x31, 0xd2, 0xff, 0x90, 0xd8, 0, 0, 0);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "playMissing");
        e.B(0x48, 0x89, 0x43, 88, 0x48, 0xba); e.Image(image, 0x2ba8d78);
        e.B(0x48, 0x39, 0x10); e.J(0x85, "playIncompatible");
        Playing(); e.B(0x89, 0x43, 104, 0x89, 0x43, 108, 0xc7, 0x43, 112, 1, 0, 0, 0);
        if (action != "status")
        {
            // State check and toggle execute in a single GUI callback.
            e.B(0x83, 0xf8, action == "start" ? (byte)1 : (byte)0); e.J(0x84, "playDone");
            if (action == "start")
            {
                e.B(0x48, 0x8b, 0x4b, 88, 0x48, 0x81, 0xc1, 0xa0, 0, 0, 0, 0x48, 0x8b, 0x01, 0xba, 0x1f, 0, 0, 0, 0xff, 0x50, 0x70, 0x84, 0xc0);
                e.J(0x85, "playBusy");
                // Refuse unsaved/empty worlds, avoiding first-play Save As.
                e.B(0x48, 0x8b, 0x43, 88, 0x48, 0x8b, 0x80, 0x20, 1, 0, 0, 0x48, 0x85, 0xc0); e.J(0x84, "playMissing");
                e.B(0x48, 0x8b, 0x40, 0x50, 0x48, 0x85, 0xc0); e.J(0x84, "playMissing");
                e.B(0x80, 0x38, 0); e.J(0x84, "playMissing");
            }
            // Full native Play action honors editor Play settings and owns lifecycle.
            // Its return is void: re-read the actual play flag instead of claiming success.
            e.B(0x48, 0x8b, 0x4b, 88); Call(0x27f050);
            Playing(); e.B(0x89, 0x43, 108);
        }
        e.J(0x84, "playDone", true);
        e.Label("playMissing"); e.B(0xc7, 0x43, 72, 7, 0, 0, 0); e.J(0x84, "playDone", true);
        e.Label("playIncompatible"); e.B(0xc7, 0x43, 72, 4, 0, 0, 0); e.J(0x84, "playDone", true);
        e.Label("playBusy"); e.B(0xc7, 0x43, 72, 5, 0, 0, 0);
        e.Label("playDone");
    }
    // Fixed private ABI; no arbitrary-function/address tool is exposed. RBX remains the
    // slot data pointer; every call shares the callback's aligned shadow space/unwind.
    private static void EmitOpenWorld(E e, nint image, bool prefab)
    {
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        // Find WorldEditor descriptor, then create or obtain its sole existing instance.
        e.B(0x48, 0x8b, 0x4b, 16, 0x48, 0xba); e.Image(image, 0x2ba4318);
        Call(0x20ea70);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "worldDone");
        e.B(0x48, 0x89, 0x43, 80, 0x83, 0x78, 0x24, 1); e.J(0x87, "worldIncompatible");
        e.J(0x84, "worldExisting");
        e.B(0x48, 0x89, 0xc2, 0x48, 0x8b, 0x4b, 16, 0x45, 0x31, 0xc0); Call(0x20fec0);
        e.J(0x84, "worldReady", true);
        e.Label("worldExisting");
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x31, 0xd2, 0xff, 0x90, 0xd8, 0, 0, 0);
        e.Label("worldReady");
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "worldDone");
        e.B(0x48, 0x89, 0x43, 88, 0x48, 0xba); e.Image(image, 0x2ba8d78);
        e.B(0x48, 0x39, 0x10); e.J(0x85, "worldIncompatible");
        // Refuse play and modified state inside the GUI callback, before loading.
        e.B(0x48, 0x8d, 0x88, 0xa0, 0, 0, 0, 0x48, 0x8b, 0x01, 0xff, 0x90, 0x98, 0, 0, 0, 0x84, 0xc0);
        e.J(0x85, "worldBusy");
        // Match OpenResource's owning dirty mask: prefab documents use 0x0c,
        // while ordinary worlds use 0x1f. The prefab preview world is not a file to save.
        e.B(0x48, 0x8b, 0x4b, 88, 0x48, 0x81, 0xc1, 0xa0, 0, 0, 0); Call(0x1a7a900);
        e.B(0x89, 0xc2, 0x48, 0x8b, 0x4b, 88, 0x48, 0x81, 0xc1, 0xa0, 0, 0, 0, 0x48, 0x8b, 0x01, 0xff, 0x50, 0x70, 0x84, 0xc0);
        e.J(0x85, "worldBusy");
        // Construct a real engine ResourceName; full OpenResource handles unload,
        // path resolution, world load and editor lifecycle. It returns a bool.
        e.B(0x48, 0x8d, 0x4b, 96, 0x48, 0x8d, 0x93, 0, 2, 0, 0); Call(0x1e35d50);
        // The engine's full open routine contains fixed 257-byte copies. Check
        // BOTH GUID-resolved ResourceName and filesystem-expanded path before it
        // can unload the current world. Scratch output has the engine's 560-byte
        // buffer size, starting at data+1024. Reject non-world GUID resolutions.
        e.B(0x48, 0x8b, 0x43, 96, 0x48, 0x85, 0xc0); e.J(0x84, "worldRelease");
        e.B(0x48, 0x8b, 0x50, 8, 0x48, 0x85, 0xd2); e.J(0x84, "worldRelease");
        e.B(0x8b, 0x4a, 0xf8, 0x81, 0xf9, 0xf0, 0, 0, 0); e.J(0x87, "worldLongPath");
        e.B(0x83, 0xf9, 4); e.J(0x86, "worldLongPath");
        if (prefab)
        {
            e.B(0x80, 0x7c, 0x0a, 0xfd, 0x2e); e.J(0x85, "worldLongPath");
            e.B(0x66, 0x8b, 0x44, 0x0a, 0xfe, 0x66, 0x0d, 0x20, 0x20, 0x66, 0x3d, 0x65, 0x74); e.J(0x85, "worldLongPath");
        }
        else
        {
        e.B(0x8b, 0x44, 0x0a, 0xfc, 0x0d, 0, 0x20, 0x20, 0x20, 0x3d, 0x2e, 0x65, 0x6e, 0x74); e.J(0x85, "worldLongPath");
        }

        Call(0x1e90630);
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8b, 0x53, 96, 0x48, 0x8b, 0x52, 8,
            0x4c, 0x8d, 0x83, 0, 4, 0, 0, 0x41, 0xb9, 0xff, 0xff, 0xff, 0xff, 0xff, 0x90, 0x48, 1, 0, 0, 0x84, 0xc0);
        e.J(0x84, "worldRelease");
        e.B(0x48, 0x8d, 0x93, 0, 4, 0, 0, 0xb9, 0xf1, 0, 0, 0);
        e.Label("worldPathBound");
        e.B(0x80, 0x3a, 0); e.J(0x84, "worldLoad");
        e.B(0x48, 0xff, 0xc2, 0xff, 0xc9); e.J(0x85, "worldPathBound");
        e.Label("worldLongPath"); e.B(0xc7, 0x43, 72, 6, 0, 0, 0); e.J(0x84, "worldRelease", true);
        e.Label("worldLoad");
        // File existence is a separate native filesystem operation (the resource
        // opener at RVA30da4b supplies R8D=-1 to slot150). This third argument is
        // the mount selector, not optional; a volatile leftover indexes invalid memory.
        // Missing paths must not unload a world.
        Call(0x1e90630);
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8d, 0x93, 0, 4, 0, 0,
            0x41, 0xb8, 0xff, 0xff, 0xff, 0xff, 0xff, 0x90, 0x50, 1, 0, 0, 0x84, 0xc0);
        e.J(0x84, "worldRelease");
        e.B(0x48, 0x8b, 0x4b, 88, 0x48, 0x8d, 0x53, 96); Call(0x2832b0);
        e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 68);
        if (prefab)
        {
            // Verify the owning mode and locate the display root whose prefab
            // ancestor has exactly the requested ResourceName (including GUID).
            e.B(0x48, 0x8b, 0x43, 88, 0x48, 0x8b, 0x80, 0x28, 1, 0, 0, 0x48, 0x89, 0x83, 0x80, 0, 0, 0);
            void Api(int slot) { e.B(0x48, 0x8b, 0x8b, 0x80, 0, 0, 0, 0x48, 0x8b, 0x01, 0xff, 0x90); e.B(BitConverter.GetBytes(e.Profile.WorldApiSlot(slot))); }
            Api(0x80); e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 104, 0x84, 0xc0); e.J(0x84, "worldRelease");
            Api(0xa8); e.B(0x89, 0x83, 0x88, 0, 0, 0, 0xc7, 0x83, 0x8c, 0, 0, 0, 0, 0, 0, 0);
            e.Label("prefabRootLoop");
            e.B(0x8b, 0x93, 0x8c, 0, 0, 0, 0x81, 0xfa, 0, 0x20, 0, 0); e.J(0x83, "worldRelease");
            e.B(0x3b, 0x93, 0x88, 0, 0, 0); e.J(0x83, "worldRelease");
            Api(0xb0); e.B(0xff, 0x83, 0x8c, 0, 0, 0, 0x48, 0x85, 0xc0); e.J(0x84, "prefabRootLoop");
            e.B(0x48, 0x89, 0x83, 0x90, 0, 0, 0, 0x48, 0x8b, 0x48, 32, 0x48, 0x85, 0xc9); e.J(0x84, "prefabRootLoop");
            e.B(0x48, 0x83, 0xc1, 8, 0x48, 0x8d, 0x53, 96); Call(0x1e36fd0);
            e.B(0x85, 0xc0); e.J(0x85, "prefabRootLoop");
            e.B(0x48, 0x8b, 0x83, 0x90, 0, 0, 0, 0x48, 0x8b, 0x40, 40, 0x48, 0x89, 0x43, 112);
        }
        // Release ResourceName exactly as the engine's native API wrapper does.
        e.Label("worldRelease");
        e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0x85, 0xc9); e.J(0x84, "worldPath");
        e.B(0xf0, 0xff, 0x09); e.J(0x85, "worldPath");
        Call(0x269dc0);
        e.B(0x48, 0xb8); e.Image(image, 0x3c7c260);
        e.B(0x48, 0x8b, 0x08, 0x48, 0x8b, 0x01, 0x48, 0x8b, 0x53, 96, 0x45, 0x31, 0xc0, 0xff, 0x50, 0x10);
        e.Label("worldPath");
        // Copy current world path while still on the GUI thread. No borrowed
        // engine string pointer escapes the callback; output is bounded to 2047.
        e.B(0x48, 0x8b, 0x43, 88, 0x48, 0x8b, 0x80, 0x20, 1, 0, 0, 0x48, 0x85, 0xc0); e.J(0x84, "worldDone");
        e.B(0x48, 0x8b, 0x50, 0x50, 0x48, 0x85, 0xd2); e.J(0x84, "worldDone");
        e.B(0x4c, 0x8d, 0x8b, 0, 8, 0, 0, 0x41, 0xb8, 0xff, 7, 0, 0);
        e.Label("worldCopy");
        e.B(0x8a, 0x02, 0x41, 0x88, 0x01, 0x84, 0xc0); e.J(0x84, "worldDone");
        e.B(0x48, 0xff, 0xc2, 0x49, 0xff, 0xc1, 0x41, 0xff, 0xc8); e.J(0x85, "worldCopy");
        e.J(0x84, "worldDone", true);
        e.Label("worldBusy"); e.B(0xc7, 0x43, 72, 5, 0, 0, 0); e.J(0x84, "return", true);
        e.Label("worldIncompatible"); e.B(0xc7, 0x43, 72, 4, 0, 0, 0); e.J(0x84, "return", true);
        e.Label("worldDone");
    }
    // Keep the established name so older server processes share the same claim.
    private static string EventName(Process p) => $"Local\\ReforgerMcp.NativeReload.{p.Id}.{p.StartTime.ToUniversalTime().Ticks}";
    internal sealed class DispatchLease : IDisposable
    {
        private readonly Mutex mutex;
        public DispatchLease(Process process)
        {
            mutex = new Mutex(false, EventName(process) + ".Dispatch");
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; } // release event still decides native safety
            if (!acquired) { mutex.Dispose(); throw new WorkbenchBusyException("Another native dispatcher owns this Workbench operation."); }
        }
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
    public static object Status(Process p)
    {
        var claim = N.OpenEvent(0x100000, false, EventName(p));
        if (claim != 0)
        {
            var released = N.WaitForSingleObject(claim, 0) == 0;
            N.CloseHandle(claim);
            return new { state = released ? "released" : "blocked", message = released
                ? "Previous native callback was released. Its result may remain unknown and retained allocations remain until Workbench exits; the next serialized dispatch can proceed."
                : "Native callback release is unverified. Resolve dialogs and inspect the original operation; do not replay it." };
        }
        if (Marshal.GetLastWin32Error() != 2) return new { state = "unavailable", message = "Cannot inspect native operation claim: " + new Win32Exception(Marshal.GetLastWin32Error()).Message };
        return new { state = "not_active", message = "Binary compatibility and readiness are checked at native dispatch." };
    }
    static void CheckHash(string path, string hash) { using var file = File.OpenRead(path); if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsupported native Workbench binary; reanalyze and update the isolated profile. No fallback: " + path); }
    static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    sealed class E(NativeWorkbenchProfile? profile = null)
    {
        public NativeWorkbenchProfile Profile { get; } = profile ?? NativeWorkbenchProfile.Stable;
        public void Image(nint image, int rva) => Q(Profile.Address(image, rva));
        readonly List<byte> bytes = []; readonly Dictionary<string, int> labels = []; readonly List<(int, string)> jumps = [];
        public void B(params byte[] b) => bytes.AddRange(b); public void Q(long n) => bytes.AddRange(BitConverter.GetBytes(n));
        public void Label(string name) => labels.Add(name, bytes.Count);
        public void J(byte condition, string label, bool always = false) { if (always) B(0xe9); else B(0x0f, condition); jumps.Add((bytes.Count, label)); B(0, 0, 0, 0); }
        public byte[] Done() { var b = bytes.ToArray(); foreach (var (p, l) in jumps) BitConverter.GetBytes(labels[l] - p - 4).CopyTo(b, p); return b; }
    }
    static class N
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint OpenEvent(uint access, bool inherit, string name);
        [DllImport("kernel32", SetLastError = true)] public static extern nint OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32", SetLastError = true)] public static extern bool ReadProcessMemory(nint p, nint address, byte[] buffer, nuint size, out nuint read);
        [DllImport("kernel32", SetLastError = true)] public static extern bool WriteProcessMemory(nint p, nint address, byte[] buffer, nuint size, out nuint written);
        [DllImport("kernel32", SetLastError = true)] public static extern nint VirtualAllocEx(nint p, nint address, nuint size, uint allocation, uint protection);
        [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualFreeEx(nint p, nint address, nuint size, uint kind);
        [DllImport("kernel32", SetLastError = true)] public static extern bool VirtualProtectEx(nint p, nint address, nuint size, uint protection, out uint old);
        [DllImport("kernel32", SetLastError = true)] public static extern bool FlushInstructionCache(nint p, nint address, nuint size);
        [DllImport("kernel32", SetLastError = true)] public static extern nint CreateRemoteThread(nint p, nint attributes, nuint stack, nint start, nint argument, uint flags, out uint id);
        [DllImport("kernel32", SetLastError = true)] public static extern uint WaitForSingleObject(nint handle, uint ms);
        [DllImport("kernel32", SetLastError = true)] public static extern bool ResetEvent(nint handle);
        [DllImport("kernel32", SetLastError = true)] public static extern bool SetEvent(nint handle);
        [DllImport("kernel32", SetLastError = true)] public static extern bool GetExitCodeThread(nint handle, out uint exit);
        [DllImport("kernel32", SetLastError = true)] public static extern bool CloseHandle(nint h);
        [DllImport("kernel32")] public static extern nint GetCurrentProcess();
        [DllImport("kernel32", SetLastError = true)] public static extern bool DuplicateHandle(nint source, nint handle, nint target, out nint duplicate, uint access, bool inherit, uint options);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateEvent(nint attributes, bool manual, bool initial, string? name);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern nint GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true)] public static extern nint GetProcAddress(nint module, string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool GetModuleHandleEx(uint flags, nint address, out nint module);
        [DllImport("kernel32", CharSet = CharSet.Unicode)] public static extern uint GetModuleFileName(nint module, System.Text.StringBuilder path, uint size);
        [DllImport("user32")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32")] public static extern nint GetForegroundWindow();
    }

}


