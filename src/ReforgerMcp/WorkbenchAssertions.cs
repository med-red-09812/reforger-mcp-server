namespace ReforgerMcp;

public sealed partial class WorkbenchSession
{
    internal volatile object AutomaticResourceLeakSuppression = new { state = "not_started" };

    public Task<object> ResourceLeakAssertion(string action, int processId, CancellationToken ct)
    {
        if (action is not ("status" or "suppress")) throw new ArgumentException("action must be status or suppress.");
        if (processId <= 0) throw new ArgumentException("processId must come from workbench_script_status.");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
            var editorWindows = Windows(process.Id);
            if (!editorWindows.Any(w => w.Current.AutomationId == ResourceManager))
                throw Blocked(process, "Waiting for the Resource Manager before assertion suppression.");
            if (editorWindows.Any(IsModal) || editorWindows.Any(w => !w.Current.IsEnabled))
                throw Blocked(process, "Resolve existing editor dialogs before changing or observing assertion suppression.");
            return NativeWorkbench.Execute(process, token, resourceLeakAssertion: action == "suppress");
        }, ct);
    }
}
