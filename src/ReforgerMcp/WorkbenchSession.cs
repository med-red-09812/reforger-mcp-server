using System.Diagnostics;
using System.Windows.Automation;


namespace ReforgerMcp;

// Owns readiness checks and serialization of live native Workbench operations.
public sealed partial class WorkbenchSession(Configuration configuration, WorkbenchWindows windows, ConsoleLogs logs)
{
    private const string ResourceManager = "Arma4WorkbenchApp.ResourceManagerMain";
    private const string ScriptEditor = "Arma4WorkbenchApp.ScriptEditorMain";
    private const string OutputId = ScriptEditor + ".outputDock.dockWidgetContents_5.outputEdit";
    private const int OutputLimit = 32768;
    private readonly SemaphoreSlim gate = new(1);
    private readonly object operationLock = new();
    private sealed record OperationRecord(string Id, Task<object> Work, int Pid, long Started);
    private readonly List<OperationRecord> operations = [];
    private const int OperationCapacity = 16;

    private sealed class BlockedException(string message, int pid, long started) : Exception(message)
    {
        public int Pid { get; } = pid;
        public long Started { get; } = started;
    }
    private static BlockedException Blocked(Process process, string message) =>
        new(message, process.Id, process.StartTime.ToUniversalTime().Ticks);

    internal Task<object> ObserveProject(int processId, CancellationToken ct) => Run(token =>
    {
        using var process = SelectProcess(processId);
        if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_status.");
        var windows = Windows(process.Id);
        RequiredWindow(windows, ResourceManager);
        if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled))
            throw new InvalidOperationException("Resolve editor dialogs before observing project context.");
        var context = (NativeProjectContext)NativeWorkbench.Execute(process, token, projectContext: true);
        return new ProjectObservation(process.Id, process.StartTime.ToUniversalTime().Ticks,
            process.MainModule!.FileVersionInfo.FileVersion ?? "unknown", context);
    }, ct);

    public async Task<object> Operation(string id, CancellationToken ct, bool details = false, string? knownRevision = null)
    {
        OperationRecord operation;
        lock (operationLock)
            operation = operations.SingleOrDefault(o => o.Id == id)
                ?? throw new ArgumentException("Unknown or expired operationId. The last 16 native operations are retained on this connection; do not replay an uncertain mutation.");
        var result = await windows.PendingResult(operation.Work, id, operation.Pid, operation.Started, "workbench_operation", ct);
        var completed = ReferenceEquals(result, operation.Work.IsCompletedSuccessfully ? operation.Work.Result : null);
        return OperationPresentation.Present(result is RuntimeResult runtime ? runtime.Present(id, details) : result,
            id, completed, "workbench_operation", knownRevision, details);
    }

    public Task<object> Status(CancellationToken ct, int? processId = null) => Run(_ =>
    {
        using var process = SelectProcess(processId);
        var windows = Windows(process.Id);
        var editor = windows.SingleOrDefault(w => w.Current.AutomationId == ScriptEditor);
        var output = editor is null ? null : Output(editor);
        return new
        {
            processId = process.Id,
            executable = process.MainModule!.FileName,
            version = process.MainModule.FileVersionInfo.FileVersion,
            backend = "Windows UI Automation observations",
            nativeReload = NativeWorkbench.Status(process),
            automaticResourceLeakSuppression = AutomaticResourceLeakSuppression,
            windows = windows.Select(w => new
            {
                title = w.Current.Name,
                id = w.Current.AutomationId,
                enabled = w.Current.IsEnabled,
                modal = IsModal(w)
            }).ToArray(),
            compilerOutput = output,
            outputTruncated = output?.Length > OutputLimit,
            diagnostics = editor is null ? null : Diagnostics(editor),
            scope = "Current editor observations; compiler output may be from an earlier operation. Not proof of active project, reload completion, or standalone game-client state."
        };
    }, ct);

    public Task<object> Reload(int processId, CancellationToken ct)
    {
        if (processId <= 0) throw new ArgumentException("processId must come from workbench_script_status.");
        return Start(processId, token => ExecuteCore(processId, token), ct);
    }

    public async Task<object> OpenWorld(string resourceName, int processId, CancellationToken ct, bool prefab = false)
    {
        if (string.IsNullOrWhiteSpace(resourceName) || resourceName.Any(char.IsControl) || resourceName.Contains(';') ||
            !resourceName.EndsWith(prefab ? ".et" : ".ent", StringComparison.OrdinalIgnoreCase) || System.Text.Encoding.UTF8.GetByteCount(resourceName) > 240)
            throw new ArgumentException("resourceName must be one .ent world path or resource name, at most 240 UTF-8 bytes, without control characters or semicolons.");
        if (prefab && !System.Text.RegularExpressions.Regex.IsMatch(resourceName, "^\\{[0-9A-Fa-f]{16}\\}.+\\.et$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new ArgumentException("Prefab opening requires a GUID-qualified resource name: {GUID}path.et.");
        if (processId <= 0) throw new ArgumentException("processId must come from workbench_script_status.");
        var world = resourceName.Replace('\\', '/');
        return await Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager);
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled))
                throw Blocked(process, "Resolve disabled editors or modal dialogs before opening a world.");
            if (windows.Any(w => w.Current.AutomationId is not (ResourceManager or ScriptEditor or "Arma4WorkbenchApp.worldEditorMain" or "Arma4WorkbenchApp.NotificationPopup")))
                throw Blocked(process, "Close other editor windows before opening a world; only Resource Manager, Script Editor and World Editor are supported.");
            if (windows.Any(w => w.Current.Name.Contains('*') || w.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)).Cast<AutomationElement>().Any(t => t.Current.Name.Contains('*'))))
                throw Blocked(process, "Save or discard unsaved editor changes before opening a world.");
            token.ThrowIfCancellationRequested();
            editObservations.Clear();
            return NativeWorkbench.Execute(process, token, world);
        }, ct);
    }

    public Task<object> Play(string action, int processId, CancellationToken ct)
    {
        if (action is not ("status" or "start" or "stop")) throw new ArgumentException("action must be status, start or stop.");
        if (processId <= 0) throw new ArgumentException("processId must come from workbench_script_status.");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager);
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled))
                throw Blocked(process, "Resolve disabled editors or modal dialogs before inspecting or changing Play mode.");
            token.ThrowIfCancellationRequested();
            if (action != "status") editObservations.Clear();
            return NativeWorkbench.Execute(process, token, play: action);
        }, ct);
    }

    private async Task<object> Start(int processId, Func<CancellationToken, object> operation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OperationRecord record;
        lock (operationLock)
        {
            var pending = operations.FirstOrDefault(o => !o.Work.IsCompleted);
            if (pending is not null) return new { state = "blocked", dispatched = false,
                reason = "A native operation is pending. Poll it; this requested operation was not dispatched.",
                poll = new { tool = "workbench_operation", arguments = new { operationId = pending.Id } } };
            using var process = SelectProcess(processId);
            var pid = process.Id;
            var started = process.StartTime.ToUniversalTime().Ticks;
            // Only completed entries are evicted. The native lane still protects uncertain work.
            if (operations.Count == OperationCapacity) operations.RemoveAt(0);
            var work = Run(operation, CancellationToken.None, TimeSpan.FromSeconds(190));
            record = new(Guid.NewGuid().ToString("N"), work, pid, started);
            operations.Add(record);
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        await Task.WhenAny(record.Work, Task.Delay(500));
        var result = await windows.PendingResult(record.Work, record.Id, record.Pid, record.Started, "workbench_operation", CancellationToken.None);
        var completed = ReferenceEquals(result, record.Work.IsCompletedSuccessfully ? record.Work.Result : null);
        if (completed) return result is RuntimeResult finished ? finished.Present(record.Id) : result;
        return OperationPresentation.Present(result is RuntimeResult runtime ? runtime.Present(record.Id) : result,
            record.Id, completed, "workbench_operation");
    }

    private object ExecuteCore(int processId, CancellationToken ct)
    {
        using var process = SelectProcess(processId);
        if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
        var windows = Windows(process.Id);
        if (windows.Any(IsModal)) throw Blocked(process, "Workbench has a modal dialog. Resolve it before reloading.");
        RequiredWindow(windows, ResourceManager);
        if (windows.Any(w => w.Current.AutomationId is not (ResourceManager or ScriptEditor or "Arma4WorkbenchApp.worldEditorMain" or "Arma4WorkbenchApp.NotificationPopup")))
            throw Blocked(process, "Native reload supports Resource Manager, Script Editor and World Editor; another editor is open.");
        if (windows.Any(w => !w.Current.IsEnabled || w.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().Any(t => t.Current.Name.Contains('*'))))
            throw Blocked(process, "Resolve disabled editors or unsaved tabs before native reload.");
        ct.ThrowIfCancellationRequested();
        editObservations.Clear();
        // Keep Stop and Reload in one serialized operation, including across MCP servers.
        // Native Execute takes this same reentrant mutex for each main-thread dispatch.
        using var reloadLease = new NativeWorkbench.DispatchLease(process);
        System.Text.Json.Nodes.JsonObject? play = null;
        if (windows.Any(w => w.Current.AutomationId == "Arma4WorkbenchApp.worldEditorMain"))
        {
            play = System.Text.Json.JsonSerializer.SerializeToNode(
                NativeWorkbench.Execute(process, ct, play: "stop"), Configuration.Json)!.AsObject();
            if (play["state"]?.GetValue<string>() is not ("stopped" or "already_stopped") ||
                play["isPlaying"]?.GetValue<bool>() != false)
                return new { state = "reload_not_started", processId, reloadDispatched = false,
                    reason = "Play did not reach a confirmed stopped state. Inspect this operation before retrying.", play };
        }
        var checkpoint = logs.BeginReload(process, ct);
        var result = System.Text.Json.JsonSerializer.SerializeToNode(NativeWorkbench.Execute(process, ct), Configuration.Json)!.AsObject();
        result["play"] = play;
        if (result["state"]?.GetValue<string>() is "reloaded" or "reload_failed")
            result["diagnostics"] = System.Text.Json.JsonSerializer.SerializeToNode(logs.ReloadErrors(checkpoint, process, ct), Configuration.Json);
        return result;
    }
    private Process SelectProcess(int? processId = null) => SelectProcess(configuration, processId);
    internal static Process SelectProcess(Configuration configuration, int? requestedProcessId = null)
    {
        if (requestedProcessId is { } pid)
        {
            var process = Process.GetProcessById(pid);
            try
            {
                _ = configuration.WorkbenchFor(process);
                return process;
            }
            catch { process.Dispose(); throw; }
        }
        var entries = configuration.WorkbenchEntries.Select(p => p.Value).ToArray();
        if (entries.Length == 0) throw new InvalidOperationException("Workbench is not configured.");
        var candidates = entries.SelectMany(w => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(w.Executable)))
            .DistinctBy(p => p.Id).ToArray();
        Process? selected = null;
        try
        {
            foreach (var candidate in candidates)
            {
                if (candidate.HasExited) continue;
                if (!entries.Any(w => string.Equals(candidate.MainModule?.FileName, w.Executable, StringComparison.OrdinalIgnoreCase))) continue;
                _ = configuration.WorkbenchFor(candidate);
                if (selected is not null) throw new InvalidOperationException("Multiple configured Workbench processes; supply processId explicitly. No installation was selected.");
                selected = candidate;
            }
            return selected ?? throw new InvalidOperationException("Configured Workbench is not running.");
        }
        catch { selected?.Dispose(); throw; }
        finally { foreach (var candidate in candidates) if (candidate != selected) candidate.Dispose(); }
    }
    internal static AutomationElement[] Windows(int pid)
    {
        // Qt owned dialogs are not necessarily children of the UIA desktop root.
        // Enumerate native top-level windows, then inspect each through UIA.
        var handles = new List<nint>();
        CaptureNative.EnumWindowsCallback callback = (hwnd, _) =>
        {
            CaptureNative.GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == pid && CaptureNative.IsWindowVisible(hwnd)) handles.Add(hwnd);
            return true;
        };
        if (!CaptureNative.EnumWindows(callback, 0)) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        return handles.Select(AutomationElement.FromHandle).ToArray();
    }
    private static bool IsModal(AutomationElement window) => window.TryGetCurrentPattern(WindowPattern.Pattern, out var p) && ((WindowPattern)p).Current.IsModal;
    private static AutomationElement RequiredWindow(AutomationElement[] windows, string id) =>
        windows.SingleOrDefault(w => w.Current.AutomationId == id) ?? throw new InvalidOperationException(
            "Native editor window unavailable or incompatible: " + id + ".");
    private static T Pattern<T>(AutomationElement element, AutomationPattern pattern) => element.TryGetCurrentPattern(pattern, out var result)
        ? (T)result : throw new InvalidOperationException("Required native accessibility pattern unavailable: " + pattern.ProgrammaticName);
    private static string Output(AutomationElement editor)
    {
        var element = editor.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, OutputId))
            ?? throw new InvalidOperationException("Native compiler output unavailable or incompatible.");
        return Pattern<TextPattern>(element, TextPattern.Pattern).DocumentRange.GetText(OutputLimit + 1);
    }

    private static object Diagnostics(AutomationElement editor)
    {
        var list = editor.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty,
            ScriptEditor + ".errorsDock.dockWidgetContents_17.errorsList"));
        if (list is null) return new { state = "unavailable" };
        // A filtered TreeWalker can normalize the excluded tree root and escape to
        // siblings outside this editor. Descendant search is explicitly root-bounded.
        var nodes = list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TreeItem));
        var cells = nodes.Cast<AutomationElement>().Take(200).Select(node =>
        {
            var name = node.Current.Name; return name.Length > 2000 ? name[..2000] : name;
        }).ToArray();
        return new
        {
            state = "observed",
            cells,
            truncated = nodes.Count > 200,
            scope = "Native accessible diagnostic cells in display order, including group headings and description/addon/file/line cells. Filtered or collapsed UI entries may be omitted; not a complete log."
        };
    }

    private async Task<object> Run(Func<CancellationToken, object> operation, CancellationToken ct, TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        if (!await gate.WaitAsync(0, ct)) throw new WorkbenchBusyException("Native Workbench operation is busy. Inspect screenshots; do not retry a mutation blindly.");
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(40));
        // Keep the lock until UIA returns, even when the caller stops waiting.
        var work = Task.Run(() =>
        {
            using var mutex = new Mutex(false, @"Local\ReforgerMcp.Workbench.Scripts");
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new WorkbenchBusyException("Another server is using native Workbench commands.");
                deadline.Token.ThrowIfCancellationRequested();
                try { return operation(deadline.Token); }
                catch (BlockedException ex)
                {
                    return windows.BlockedResult(ex.Message, ex.Pid, ex.Started, deadline.Token).GetAwaiter().GetResult();
                }
            }
            finally { if (acquired) mutex.ReleaseMutex(); deadline.Dispose(); gate.Release(); }
        }, CancellationToken.None);
        try { return await work.WaitAsync(timeout ?? TimeSpan.FromSeconds(40), ct); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new InvalidOperationException("Native operation wait ended; its outcome may be unknown. A blocked provider retains the operation lock until it returns. Inspect Workbench; do not blindly retry.", ex);
        }
    }
}




