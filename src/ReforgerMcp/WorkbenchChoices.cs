using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

public sealed partial class WorkbenchSession
{
    internal Task<object> RefreshChoices(int processId, NativeWorkbench.VerificationScope verification, CancellationToken ct) => Run(token =>
    {
        var elapsed = Stopwatch.StartNew();
        using var process = SelectProcess(processId);
        if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; enable choices for the intended process again.");
        var windows = Windows(process.Id);
        var editor = windows.SingleOrDefault(w => w.Current.AutomationId == "Arma4WorkbenchApp.worldEditorMain");
        if (editor is null || windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled))
            return new { state = "waiting_for_editor" };
        var port = configuration.WorkbenchFor(process).RuntimePort ?? throw new InvalidOperationException("Configure workbench.runtimePort and install the editor adapter.");
        var window = editor.Current.NativeWindowHandle;
        var observed = (NativeWorkbench.ChoicesResult)NativeWorkbench.Execute(process, token,
            choices: new(window), verification: verification);
        int updated = 0;
        foreach (var group in observed.Controls.GroupBy(c => c.Provider))
        {
            if (!Regex.IsMatch(group.Key, "^[A-Za-z_][A-Za-z0-9_]{0,95}$"))
                throw new InvalidOperationException("Invalid provider identifier in dynamic choice metadata.");
            RuntimeNet.VerifyListener(port, process.Id);
            var receipt = Guid.NewGuid().ToString("N");
            var response = RuntimeNet.Call(port, new { APIFunc = "RST_EditorChoices", provider = group.Key, token = receipt });
            if (response.GetProperty("adapterBuild").GetString() != RuntimeAdapter.Build || response.GetProperty("token").GetString() != receipt)
                throw new InvalidOperationException("Editor choices adapter identity mismatch; install this server's adapter.");
            var state = response.GetProperty("state").GetString();
            if (state == "world_unavailable") return new { state = "waiting_for_editor" };
            if (state != "ready") throw new InvalidOperationException("Choice provider " + group.Key + ": " + state);
            // JsonApiStruct omits registered empty arrays. A ready response
            // without values therefore means the provider found no sources.
            var supplied = response.TryGetProperty("values", out var rawValues)
                ? rawValues.EnumerateArray().Select(v => v.GetString() ?? "") : [];
            var values = supplied
                .Where(v => v.Length != 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var control in group)
            {
                var items = new List<NativeWorkbench.Choice> { new("(None)", "", NativeWorkbench.ChoicesPrefix + group.Key) };
                items.AddRange(values.Select(v => new NativeWorkbench.Choice(v, v)));
                if (control.Value.Length != 0 && !values.Contains(control.Value, StringComparer.Ordinal))
                    items.Add(new(control.Value + " (not found)", control.Value, "The saved value has no source in this world."));
                var result = (NativeWorkbench.ChoicesResult)NativeWorkbench.Execute(process, token,
                    choices: new(window, control.Handle, group.Key, control.Value, items.ToArray()), verification: verification);
                updated += result.Updated;
            }
        }
        return new { state = "active", controls = observed.Controls.Length, updated, elapsedMs = elapsed.ElapsedMilliseconds };
    }, ct);
}

// Explicit process-local opt in. The service stops on errors/uncertain outcomes;
// it does not keep retrying failed native writes or substitute another backend.
public sealed class WorkbenchChoices(WorkbenchSession session, ILogger<WorkbenchChoices> logger) : BackgroundService
{
    private int processId;
    private int generation;
    private readonly object stateLock = new();
    private object status = new { state = "disabled" };
    public object Configure(string action, int pid)
    {
        lock (stateLock)
        {
            if (action == "enable")
            {
                if (pid <= 0) throw new ArgumentException("Choose the current Workbench processId.");
                processId = pid;
                generation++;
                status = new { state = "starting", processId = pid };
            }
            else if (action == "disable") { processId = 0; generation++; status = new { state = "disabled" }; }
            else if (action != "status") throw new ArgumentException("action must be enable, disable or status.");
            return status;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        NativeWorkbench.VerificationScope? verification = null;
        int verifiedGeneration = -1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(1000, stoppingToken);
                int pid, version;
                lock (stateLock) { pid = processId; version = generation; }
                if (verifiedGeneration != version)
                {
                    verification?.Dispose();
                    verification = null;
                    verifiedGeneration = version;
                }
                if (pid == 0) continue;
                try
                {
                    verification ??= new NativeWorkbench.VerificationScope();
                    var result = await session.RefreshChoices(pid, verification, stoppingToken);
                    lock (stateLock) { if (version == generation) status = result; }
                }
                catch (WorkbenchBusyException) { /* Known refusal before dispatch; wait for idle. */ }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    lock (stateLock)
                    {
                        if (version == generation)
                        {
                            processId = 0;
                            generation++;
                            status = new { state = "unavailable", processId = pid, error = ex.Message };
                        }
                    }
                    logger.LogWarning("Dynamic choices stopped: {Reason}", ex.Message);
                }
            }
        }
        finally { verification?.Dispose(); }
    }
}

[McpServerToolType]
public sealed class WorkbenchChoicesTools(WorkbenchChoices choices)
{
    [McpServerTool(Name = "workbench_dynamic_choices", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("action=status|enable|disable: automatic inline choices for marked string combos; requires installed editor adapter. Refreshes live providers every second, changes no saved properties; stops on error.")]
    public object Configure(string action = "status", int processId = 0) => choices.Configure(action, processId);
}
