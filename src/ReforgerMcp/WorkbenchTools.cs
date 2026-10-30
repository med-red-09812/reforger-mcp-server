using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

[McpServerToolType]
public sealed class WorkbenchTools(Workbench workbench, PakStore store)
{
    private static object Report(Func<object> action)
    {
        try { return action(); }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException))
        { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "workbench_status", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect Workbench processes and PAK freshness together. Compact workbench/pak readiness, revision, source labels and problems by default; details=true adds configuration, launch history and full catalogue coverage. Project readiness remains unverified. Does not launch or prepare.")]
    public async Task<object> Status(CancellationToken cancellationToken = default, bool details = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observed = workbench.Status(details);
            var catalogue = await store.Status(cancellationToken, details);
            return new { workbench = observed, pak = catalogue };
        }
        catch (Exception ex) when (ex is not (McpException or OperationCanceledException))
        { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "workbench_terminate", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Force-terminate explicit processId; unsaved work is lost.")]
    public object Terminate(int processId) => Report(() => workbench.Terminate(processId));

    [McpServerTool(Name = "workbench_open_project", ReadOnly = false, Destructive = false, OpenWorld = true)]
    [Description("Launch existing absolute .gproj; refuses if Workbench is running. Started process does not establish project readiness.")]
    public object OpenProject(
        [Description("Absolute path to the addon .gproj file; paths with spaces are supported.")] string projectPath,
        CancellationToken cancellationToken = default,
        [Description("Configured installation label, e.g. stable or experimental; required when several are configured.")] string? installation = null)
        => Report(() => workbench.OpenProject(projectPath, cancellationToken, installation));
}
