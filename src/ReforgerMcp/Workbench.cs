using System.Diagnostics;
using System.ComponentModel;

namespace ReforgerMcp;

// Direct process lifecycle only. Window metadata is not an editor protocol.
public sealed class Workbench(Configuration configuration) : IDisposable
{
    private readonly object gate = new();
    private Process? launched;
    private string? requestedProject;
    private DateTimeOffset? launchedAt;

    public object Status(bool details = true)
    {
        lock (gate)
        {
            var entries = configuration.WorkbenchEntries.ToArray();
            if (entries.Length == 0)
                return new { state = "not_configured", message = "Configure workbench.executable and workbench.addonDirectories." };
            if (entries.Length > 1)
                return new { installations = entries.Select(p => new { installation = p.Key, status = StatusFor(p.Value, details) }).ToArray(),
                    scope = "Separate installation configurations. Launch selects installation; live operations select the observed processId. No global channel switch." };
            return StatusFor(entries[0].Value, details);
        }
    }

    private object StatusFor(WorkbenchConfiguration config, bool details)
    {
            var missing = MissingInputs(config);
            var instances = Observe(config).Where(p => p.Executable is null || p.Executable.Equals(config.Executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!details)
                return new
                {
                    state = instances.Length > 0 ? "running" : missing.Length > 0 ? "unavailable" : "stopped",
                    missingInputs = missing, instances, projectReadiness = "unverified"
                };
            object? lastLaunch = null;
            if (launched is not null && launched.StartInfo.FileName.Equals(config.Executable, StringComparison.OrdinalIgnoreCase))
            {
                var exited = launched.HasExited;
                lastLaunch = new
                {
                    processId = launched.Id, requestedProject, launchedAt,
                    state = exited ? "exited" : "running",
                    exitCode = exited ? (int?)launched.ExitCode : null
                };
            }
            return new
            {
                state = instances.Length > 0 ? "running" : missing.Length > 0 ? "unavailable" : "stopped",
                executable = config.Executable, addonDirectories = config.AddonDirectories,
                missingInputs = missing, instances, lastLaunch,
                projectReadiness = "unverified",
                scope = "OS process and window observations only. Window titles are not proof of the active project or successful initialization. " +
                    "lastLaunch records this server session's request, not current editor state; it is lost on server restart."
            };
    }

    public object OpenProject(string projectPath, CancellationToken cancellationToken, string? installation = null)
    {
        lock (gate)
        {
            var config = configuration.SelectWorkbench(installation);
            if (string.IsNullOrWhiteSpace(projectPath) || !Path.IsPathFullyQualified(projectPath))
                throw new ArgumentException("projectPath must be an absolute path to an existing .gproj file.");
            projectPath = Path.GetFullPath(projectPath);
            if (!Path.GetExtension(projectPath).Equals(".gproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
                throw new ArgumentException("projectPath must be an existing .gproj file.");
            var missing = MissingInputs(config);
            if (missing.Length > 0) throw new InvalidOperationException("Missing configured Workbench inputs: " + string.Join(", ", missing));

            // Shared across server processes in this Windows session. No lock is held across an await.
            using var launchMutex = new Mutex(false, @"Local\ReforgerMcp.Workbench.Launch");
            var acquired = false;
            try
            {
                try { acquired = launchMutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) throw new InvalidOperationException("Workbench launch is busy. Inspect workbench_status before another request.");
                cancellationToken.ThrowIfCancellationRequested();
                var instances = Observe(config);
                if (instances.Length > 0)
                    throw new InvalidOperationException("Workbench is already running or could not be inspected (PIDs: " +
                        string.Join(", ", instances.Select(p => p.ProcessId)) +
                        "). No project was opened or switched. Inspect workbench_status and close the existing editor explicitly before launching.");

                var start = new ProcessStartInfo(config.Executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(config.Executable)!
                };
                start.ArgumentList.Add("-gproj");
                start.ArgumentList.Add(projectPath);
                if (config.AddonDirectories.Length > 0)
                {
                    start.ArgumentList.Add("-addonsDir");
                    start.ArgumentList.Add(string.Join(",", config.AddonDirectories));
                }
                foreach (var (option, value) in new[] { ("-profile", config.ProfileDirectory), ("-logsDir", config.LogDirectory) })
                    if (value is not null) { start.ArgumentList.Add(option); start.ArgumentList.Add(value); }
                cancellationToken.ThrowIfCancellationRequested();
                // Once Start is called, never retry or cancel/kill the editor because a client disconnected.
                var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned no process. Inspect workbench_status before retrying.");
                launched?.Dispose();
                launched = process;
                requestedProject = projectPath;
                launchedAt = DateTimeOffset.UtcNow;
                return new
                {
                    state = "started", processId = process.Id, requestedProject, launchedAt,
                    executable = config.Executable, arguments = start.ArgumentList.ToArray(),
                    projectReadiness = "unverified",
                    message = "The process was started; project initialization may still fail or display a dialog. " +
                        "Use workbench_status and inspect the editor. Do not replay a launch after an uncertain response."
                };
            }
            finally { if (acquired) launchMutex.ReleaseMutex(); }
        }
    }

    private static string[] MissingInputs(WorkbenchConfiguration config) =>
        (File.Exists(config.Executable) ? Array.Empty<string>() : [config.Executable])
        .Concat(config.AddonDirectories.Where(p => !Directory.Exists(p))).ToArray();

    public object Terminate(int processId)
    {
        lock (gate)
        {
            using var process = Process.GetProcessById(processId);
            _ = configuration.WorkbenchFor(process);
            if (process.HasExited) return new { state = "exited", processId };
            // Only the explicitly selected process is terminated, never its children.
            // Windows permissions remain authoritative; access denial is reported.
            process.Kill();
            return new { state = process.WaitForExit(5000) ? "exited" : "termination_pending", processId };
        }
    }

    private static WorkbenchInstance[] Observe(WorkbenchConfiguration config)
    {
        var result = new List<WorkbenchInstance>();
        var configuredName = Path.GetFileNameWithoutExtension(config.Executable);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (!name.Equals(configuredName, StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("ArmaReforgerWorkbench", StringComparison.OrdinalIgnoreCase)) continue;
                    if (process.HasExited) continue;
                    try
                    {
                        result.Add(new(process.Id, process.MainModule?.FileName, process.MainWindowTitle,
                            process.Responding, null));
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        if (!process.HasExited) result.Add(new(process.Id, null, null, null, ex.Message));
                    }
                }
                catch (InvalidOperationException) { /* Process exited during enumeration. */ }
            }
        }
        return result.OrderBy(p => p.ProcessId).ToArray();
    }

    public void Dispose()
    {
        lock (gate) { launched?.Dispose(); launched = null; }
    }
}

public sealed record WorkbenchInstance(int ProcessId, string? Executable, string? WindowTitle, bool? Responding, string? ObservationError);
