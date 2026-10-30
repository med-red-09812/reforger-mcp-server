using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

public sealed partial class WorkbenchSession
{
    internal Task<object> History(int processId, string expectedWorld, string action, CancellationToken ct)
    {
        if (action is not ("history" or "undo" or "redo" or "save_world")) throw new ArgumentException("Unsupported world operation.");
        if (processId <= 0 || string.IsNullOrWhiteSpace(expectedWorld) || !expectedWorld.StartsWith('$') ||
            !expectedWorld.Contains(':') || !expectedWorld.EndsWith(".ent", StringComparison.Ordinal) ||
            expectedWorld.Any(char.IsControl) || Encoding.UTF8.GetByteCount(expectedWorld) > 240)
            throw new ArgumentException("Supply the observed processId and exact loadedWorld (at most 240 UTF-8 bytes).");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager); RequiredWindow(windows, "Arma4WorkbenchApp.worldEditorMain");
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled))
                throw Blocked(process, "Resolve dialogs before accessing editor history.");
            if (action != "history") editObservations.Clear();
            return NativeWorkbench.Execute(process, token, entity: new(action, expectedWorld, "", "", [0,0,0], [0,0,0]));
        }, ct);
    }
}

[McpServerToolType]
public sealed class WorkbenchHistoryTools(WorkbenchSession session)
{
    [McpServerTool(Name = "workbench_save_world", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Save ALL pending edits in the existing named expectedWorld; invalidates edit tokens. Requires stopped Play and world mode.")]
    public Task<object> Save(int processId, string expectedWorld, CancellationToken cancellationToken = default)
        => Run(processId, expectedWorld, "save_world", cancellationToken);

    [McpServerTool(Name = "workbench_history_status", ReadOnly = true, OpenWorld = false)]
    [Description("Read undo/redo history, including user actions, in exact expectedWorld. Requires stopped Play and world mode.")]
    public Task<object> Status(int processId, string expectedWorld, CancellationToken cancellationToken = default)
        => Run(processId, expectedWorld, "history", cancellationToken);

    [McpServerTool(Name = "workbench_history", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("action=undo|redo in exact expectedWorld, including user actions. Consumes edit tokens; changes remain unsaved. Requires stopped Play and world mode.")]
    public Task<object> Edit(int processId, string expectedWorld, string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("undo" or "redo")) throw new McpException("action must be undo or redo.");
        return Run(processId, expectedWorld, action, cancellationToken);
    }

    private async Task<object> Run(int processId, string expectedWorld, string action, CancellationToken ct)
    {
        try { return await session.History(processId, expectedWorld, action, ct); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }
}
