using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ReforgerMcp;

public sealed partial class ConsoleLogs
{
    internal sealed record ReloadCheckpoint(LogResult? Before, string? Reason);

    // Associate an existing configured console with its OS owner, never its label or age.
    // The newest two configured candidates cover direct -logsDir and default session folders.
    internal ReloadCheckpoint BeginReload(Process process, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var candidates = new List<(string Source, string Session)>();
            foreach (var source in config.LogDirectories.Keys)
            {
                budget.Token.ThrowIfCancellationRequested();
                foreach (var session in SessionIds(source).Take(2))
                    if (File.Exists(LogPath(source, session)) && OwnedBy(LogPath(source, session), process))
                        candidates.Add((source, session));
            }
            if (candidates.Count != 1) return new(null, "Exactly one configured console.log owned by this Workbench process was not found.");
            var selected = candidates[0];
            var before = Read(selected.Source, selected.Session, "session", 0, null, null, null, false, 1, 1, null, budget.Token);
            if (before.PartialLastLineOmitted) return new(null, "Log ends with an incomplete line; a fresh reload boundary cannot be established.");
            return new(before, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { return new(null, "Reload log checkpoint unavailable: " + ex.Message); }
    }

    internal object ReloadErrors(ReloadCheckpoint checkpoint, Process process, CancellationToken ct)
    {
        if (checkpoint.Before is not { } before) return new { state = "unavailable", reason = checkpoint.Reason };
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (!OwnedBy(before.Path, process)) throw new InvalidOperationException("Workbench no longer owns the checkpoint log.");
            // Verify the entire old prefix before trusting line positions in the appended data.
            Read(before.Source, before.Session, "session", 0, null, null, null, false, 1, 1, before.Revision, budget.Token);
            var current = Read(before.Source, before.Session, "latest", 0, "error", null, null, false,
                before.TotalLines + 1, 40, null, budget.Token, afterLine: before.TotalLines);
            if (current.Cycle is not { Kind: "reload", CompletionLine: not null } cycle || cycle.StartLine <= before.TotalLines)
                throw new InvalidOperationException("Fresh completed reload log boundary unavailable.");
            // A replacement between the checkpoint verification and current read must not pass.
            Read(before.Source, before.Session, "session", 0, null, null, null, false, 1, 1, before.Revision, budget.Token);
            return new { state = "observed", errors = current,
                scope = "Error lines from the single completed reload cycle appended to this process-owned log after dispatch preparation. Raw messages retain file/line information where emitted. Not an independent success or root-cause diagnosis." };
        }
        catch (Exception ex)
        { return new { state = "unavailable", reason = "Fresh reload diagnostics unavailable: " + ex.Message }; }
    }

    // Read-only Restart Manager resource ownership. Never calls shutdown/restart.
    private static bool OwnedBy(string path, Process process)
    {
        var error = RmStartSession(out var session, 0, new StringBuilder(33));
        if (error != 0) throw new Win32Exception(error);
        try
        {
            error = RmRegisterResources(session, 1, [path], 0, 0, 0, 0);
            if (error != 0) throw new Win32Exception(error);
            uint count = 16;
            var owners = new Owner[count];
            error = RmGetList(session, out _, ref count, owners, out _);
            if (error != 0) throw new Win32Exception(error); // Overflow/change is explicit unavailability.
            var start = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            return owners.Take((int)count).Any(o => o.Process.Pid == process.Id &&
                (((long)o.Process.High << 32) | o.Process.Low) == start);
        }
        finally { RmEndSession(session); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Identity { public int Pid; public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Owner
    {
        public Identity Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
        public uint Type, Status, Session;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }
    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint handle, uint flags, StringBuilder key);
    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint handle, uint files, string[] paths, uint apps, nint processes, uint services, nint names);
    [DllImport("rstrtmgr", CharSet = CharSet.Unicode)] private static extern int RmGetList(uint handle, out uint needed, ref uint count, [In, Out] Owner[] owners, out uint reasons);
    [DllImport("rstrtmgr")] private static extern int RmEndSession(uint handle);
}
