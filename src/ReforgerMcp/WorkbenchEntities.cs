using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

public sealed partial class WorkbenchSession
{
    internal Task<object> Entity(string action, int processId, string expectedWorld, string name, string asset,
        float[] position, float[] angles, CancellationToken ct)
    {
        if (action is not ("inspect" or "create" or "export")) throw new ArgumentException("Unsupported entity action.");
        if (processId <= 0) throw new ArgumentException("processId must identify the current Workbench process.");
        if (string.IsNullOrWhiteSpace(expectedWorld) || !expectedWorld.StartsWith('$') || !expectedWorld.Contains(':') ||
            !expectedWorld.EndsWith(".ent", StringComparison.Ordinal) || expectedWorld.Any(char.IsControl) || Encoding.UTF8.GetByteCount(expectedWorld) > 240)
            throw new ArgumentException("expectedWorld must be the exact loadedWorld returned by workbench_open_world, at most 240 UTF-8 bytes.");
        if (!Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,95}$")) throw new ArgumentException("name must be a unique entity identifier of 1-96 ASCII letters, digits or underscores, starting with a letter or underscore.");
        if (position.Length != 3 || angles.Length != 3 || position.Concat(angles).Any(v => !float.IsFinite(v)))
            throw new ArgumentException("position and angles must each contain three finite numbers.");
        if (action == "create" && (string.IsNullOrWhiteSpace(asset) || asset.Any(char.IsControl) || asset.Contains(';') || Encoding.UTF8.GetByteCount(asset) > 240 ||
            (!Regex.IsMatch(asset, "^[A-Za-z_][A-Za-z0-9_]*$") && !Regex.IsMatch(asset, "^\\{[0-9A-Fa-f]{16}\\}.+\\.et$", RegexOptions.IgnoreCase))))
            throw new ArgumentException("asset must be an entity class name or a GUID-qualified prefab resource name ({GUID}path.et), at most 240 UTF-8 bytes. Bare paths can lose prefab identity and are rejected.");
        if (action == "export")
        {
            if (!Path.IsPathFullyQualified(asset) || !asset.EndsWith(".et", StringComparison.OrdinalIgnoreCase) ||
                asset.Any(char.IsControl) || asset.Contains(';') || Encoding.UTF8.GetByteCount(asset) > 240)
                throw new ArgumentException("prefabPath must be an absolute .et path of at most 240 UTF-8 bytes.");
            asset = Path.GetFullPath(asset);
            var directory = new DirectoryInfo(Path.GetDirectoryName(asset)!);
            if (!directory.Exists) throw new ArgumentException("Create the destination directory before exporting a prefab.");
            if (File.Exists(asset) || Directory.Exists(asset) || File.Exists(asset + ".meta") || Directory.Exists(asset + ".meta"))
                throw new ArgumentException("Prefab or metadata already exists; export requires a new path and never intentionally overwrites an asset.");
            asset = asset.Replace('\\', '/');
        }
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed; inspect workbench_script_status.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager);
            RequiredWindow(windows, "Arma4WorkbenchApp.worldEditorMain");
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled)) throw Blocked(process, "Resolve modal dialogs or disabled editors before accessing world entities.");
            token.ThrowIfCancellationRequested();
            return NativeWorkbench.Execute(process, token, entity: new(action, expectedWorld, name, asset, position, angles));
        }, ct);
    }
}

[McpServerToolType]
public sealed class WorkbenchEntityTools(WorkbenchSession session)
{
    private static async Task<object> Report(Func<Task<object>> operation)
    {
        try { return await operation(); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "workbench_entity", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect named editor entity and active layer in exact expectedWorld; requires stopped Play.")]
    public Task<object> Inspect(int processId, string expectedWorld, string name, CancellationToken cancellationToken = default)
        => Report(() => session.Entity("inspect", processId, expectedWorld, name, "", [0,0,0], [0,0,0], cancellationToken));

    [McpServerTool(Name = "workbench_create_entity", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Create uniquely named entity from class or GUID-qualified prefab in active layer of expectedWorld. position=XYZ meters; angles=XYZ Euler degrees. Requires stopped Play; unsaved undo action.")]
    public Task<object> Create(int processId, string expectedWorld, string name, string asset, float[] position, float[]? angles = null, CancellationToken cancellationToken = default)
        => Report(() => session.Entity("create", processId, expectedWorld, name, asset, position, angles ?? [0,0,0], cancellationToken));

    [McpServerTool(Name = "workbench_save_prefab", ReadOnly = false, Destructive = false, OpenWorld = false)]
    [Description("Export named entity as new absolute .et prefabPath in an existing directory; does not replace the instance. Requires stopped Play/exact expectedWorld. Outside undo; may clear history or leave files on failure.")]
    public Task<object> SavePrefab(int processId, string expectedWorld, string name, string prefabPath, CancellationToken cancellationToken = default)
        => Report(() => session.Entity("export", processId, expectedWorld, name, prefabPath, [0,0,0], [0,0,0], cancellationToken));
}
