using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

[McpServerToolType]
public sealed class WorkbenchWindowTools(WorkbenchWindows windows)
{
    private static async Task<object> Report(Func<Task<object>> action)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException)) { throw new McpException(ex.Message); }
    }
    [McpServerTool(Name = "workbench_windows", ReadOnly = true, OpenWorld = false)]
    [Description("List Workbench windows and fresh selection IDs. details=false returns headers with close-only capability; true includes text/buttons. Listing invalidates previous selections.")]
    public Task<object> List(bool details = true, CancellationToken cancellationToken = default, int? processId = null)
        => Report(() => details ? windows.List(cancellationToken, processId) : windows.Headers(cancellationToken, processId));

    [McpServerTool(Name = "workbench_window_action_status", ReadOnly = true, OpenWorld = false)]
    [Description("Poll native UI operationId. Pass knownRevision from the previous response to omit unchanged content; returned poll arguments include it. New dialog text/buttons and expired selections remain visible. details=true returns full observations. Polling preserves unchanged selection IDs.")]
    public Task<object> Status(string operationId, CancellationToken cancellationToken = default, bool details = false, string? knownRevision = null)
        => Report(() => windows.Status(operationId, cancellationToken, details, knownRevision));

    [McpServerTool(Name = "workbench_controls", ReadOnly = true, OpenWorld = false)]
    [Description("List direct children of window/control selectionId. Returned control IDs identify child containers; nextOffset continues the same parent. Non-password editable text clips at 1024 characters.")]
    public Task<object> Controls(string selectionId, int offset = 0, CancellationToken cancellationToken = default)
        => Report(() => windows.Controls(selectionId, offset, cancellationToken));

    [McpServerTool(Name = "workbench_window_action", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Run advertised native action on observed selectionId; may save/delete/open dialogs. Selections expire after 2 minutes, a listing/action or restart. Pending operationId belongs to workbench_window_action_status.")]
    public Task<object> Act(string selectionId, [Description("invoke|menu|toggle|set_text|select|expand|collapse|open_choices|choose|add_item|close; only advertised capabilities.")] string action, [Description("set_text: at most 1024 UTF-16 units, no NUL. choose: exact advertised label. add_item: array row.")] string? value = null, CancellationToken cancellationToken = default)
        => Report(() => windows.Act(selectionId, action, cancellationToken, value));
}
