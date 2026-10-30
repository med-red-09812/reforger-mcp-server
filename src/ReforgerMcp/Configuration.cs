using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace ReforgerMcp;

public sealed record Configuration
{
    public string GhidraHome { get; init; } = "";
    public string JavaHome { get; init; } = "";
    public required string DataDirectory { get; init; }
    public Dictionary<string, string> Targets { get; init; } = [];
    public Dictionary<string, string> PakDirectories { get; init; } = [];
    public Dictionary<string, string> UnpackedDirectories { get; init; } = [];
    public WorkbenchConfiguration? Workbench { get; init; }
    public Dictionary<string, WorkbenchConfiguration> Workbenches { get; init; } = [];
    public LocalHttpConfiguration? LocalHttp { get; init; }
    public bool AutomaticResourceLeakSuppression { get; init; } = true;
    public Dictionary<string, string> CaptureExecutables { get; init; } = [];
    public Dictionary<string, string> LogDirectories { get; init; } = [];
    public int AnalysisTimeoutSeconds { get; init; } = 1800;
    public int QueryTimeoutSeconds { get; init; } = 120;
    public int AnalysisCpuCount { get; init; } = 2;
    public int JavaHeapMegabytes { get; init; } = 4096;
    public const string GhidraVersion = "12.1.4";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    [JsonIgnore]
    public IEnumerable<KeyValuePair<string, WorkbenchConfiguration>> WorkbenchEntries =>
        (Workbench is null ? Enumerable.Empty<KeyValuePair<string, WorkbenchConfiguration>>() :
            [new KeyValuePair<string, WorkbenchConfiguration>("workbench", Workbench)]).Concat(Workbenches);

    public WorkbenchConfiguration SelectWorkbench(string? installation = null)
    {
        var entries = WorkbenchEntries.ToArray();
        if (installation is not null)
            return entries.SingleOrDefault(p => p.Key == installation).Value
                ?? throw new InvalidOperationException("Unknown Workbench installation; inspect workbench_status.");
        return entries.Length == 1 ? entries[0].Value : throw new InvalidOperationException(
            entries.Length == 0 ? "Workbench is not configured." : "Select an explicit Workbench installation; Stable and Experimental are configured separately.");
    }

    public WorkbenchConfiguration WorkbenchFor(Process process) => WorkbenchEntries.Select(p => p.Value)
        .SingleOrDefault(w => string.Equals(w.Executable, process.MainModule?.FileName, StringComparison.OrdinalIgnoreCase)
            && (w.ProcessId is null || w.ProcessId == process.Id && w.ProcessStartedUtc?.UtcTicks == process.StartTime.ToUniversalTime().Ticks))
        ?? throw new InvalidOperationException("Process is not a configured Workbench identity; no other installation was selected.");

    public static Configuration Load(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("This release supports Windows x64 Reforger executables only.");
        var configPath = ResolvePath(path, Environment.CurrentDirectory, "configuration file");
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configPath), Json)
            ?? throw new InvalidDataException("Empty configuration.");
        var directory = Path.GetDirectoryName(configPath)!;
        if (config.Targets is null || config.PakDirectories is null || config.UnpackedDirectories is null || config.CaptureExecutables is null || config.LogDirectories is null || config.Workbenches is null)
            throw new InvalidDataException("targets, pakDirectories, captureExecutables and logDirectories must be JSON objects, not null.");
        config = config with
        {
            DataDirectory = ResolvePath(config.DataDirectory, directory, "dataDirectory"),
            LocalHttp = config.LocalHttp is null ? null : config.LocalHttp with
            {
                TokenFile = ResolvePath(config.LocalHttp.TokenFile, directory, "localHttp.tokenFile")
            },
            GhidraHome = string.IsNullOrEmpty(config.GhidraHome) && config.Targets.Count == 0 ? "" :
                ResolvePath(config.GhidraHome, directory, "ghidraHome"),
            JavaHome = string.IsNullOrEmpty(config.JavaHome) && config.Targets.Count == 0 ? "" :
                ResolvePath(config.JavaHome, directory, "javaHome"),
            Targets = config.Targets.ToDictionary(p => p.Key, p => ResolvePath(p.Value, directory, "targets." + p.Key)),
            PakDirectories = config.PakDirectories.ToDictionary(p => p.Key, p => ResolvePath(p.Value, directory, "pakDirectories." + p.Key)),
            UnpackedDirectories = config.UnpackedDirectories.ToDictionary(p => p.Key, p => ResolvePath(p.Value, directory, "unpackedDirectories." + p.Key)),
            CaptureExecutables = config.CaptureExecutables.ToDictionary(p => p.Key, p => ResolvePath(p.Value, directory, "captureExecutables." + p.Key)),
            LogDirectories = config.LogDirectories.ToDictionary(p => p.Key, p => ResolvePath(p.Value, directory, "logDirectories." + p.Key)),
            Workbench = config.Workbench is null ? null : ResolveWorkbench(config.Workbench, "workbench"),
            Workbenches = config.Workbenches.ToDictionary(p => p.Key, p => ResolveWorkbench(p.Value, "workbenches." + p.Key))
        };
        WorkbenchConfiguration ResolveWorkbench(WorkbenchConfiguration value, string field) => value with
        {
            Executable = ResolvePath(value.Executable, directory, field + ".executable"),
            ProfileDirectory = value.ProfileDirectory is null ? null : ResolvePath(value.ProfileDirectory, directory, field + ".profileDirectory"),
            LogDirectory = value.LogDirectory is null ? null : ResolvePath(value.LogDirectory, directory, field + ".logDirectory"),
            AddonDirectories = (value.AddonDirectories ?? throw new InvalidDataException(field + ".addonDirectories must be an array."))
                .Select(p => ResolvePath(p, directory, field + ".addonDirectories")).ToArray()
        };
        if (config.Workbenches.Keys.Any(k => !ValidLabel(k) || k.Equals("workbench", StringComparison.OrdinalIgnoreCase)) ||
            config.Workbenches.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Workbenches.Count)
            throw new InvalidDataException("workbenches labels must be unique ASCII letters/digits/hyphens/underscores (1-64), excluding reserved 'workbench'.");
        var profilePaths = config.WorkbenchEntries.Select(p => p.Value.ProfileDirectory).Where(p => p is not null).ToArray();
        if (profilePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != profilePaths.Length)
            throw new InvalidDataException("Configured Workbench profile directories must be distinct across installations.");
        var captureLabels = config.CaptureExecutables.Keys.Concat(config.WorkbenchEntries.Select(p => p.Key == "workbench" ? p.Key : "workbench-" + p.Key)).ToArray();
        if (captureLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != captureLabels.Length)
            throw new InvalidDataException("captureExecutables labels conflict with a configured Workbench capture label.");
        if (config.LogDirectories.Keys.Any(k => k.Length is < 1 or > 64 || k.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) ||
            config.LogDirectories.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.LogDirectories.Count)
            throw new InvalidDataException("logDirectories labels must be unique ASCII letters/digits/hyphens/underscores (1-64).");
        if (config.CaptureExecutables.Keys.Any(k => k.Length is < 1 or > 64 || k.Equals("workbench", StringComparison.OrdinalIgnoreCase) ||
            k.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) ||
            config.CaptureExecutables.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.CaptureExecutables.Count)
            throw new InvalidDataException("captureExecutables labels must be unique ASCII letters/digits/hyphens/underscores (1-64), excluding reserved 'workbench'.");
        if (config.CaptureExecutables.Values.Any(p => !Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("captureExecutables values must be .exe paths.");
        var capturePaths = config.CaptureExecutables.Values.Concat(config.WorkbenchEntries.Select(p => p.Value.Executable)).ToArray();
        if (capturePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != capturePaths.Length)
            throw new InvalidDataException("Each capture executable path must have one label; workbench.executable is already included.");
        foreach (var (_, workbench) in config.WorkbenchEntries)
        {
            if ((workbench.ProcessId is null) != (workbench.ProcessStartedUtc is null) || workbench.ProcessId is <= 0)
                throw new InvalidDataException("workbench.processId and processStartedUtc must be supplied together with a positive PID.");
            if (workbench.RuntimePort is not null && workbench.RuntimePort is < 1 or > 65535)
                throw new InvalidDataException("workbench.runtimePort must be an explicit local NET API port (1-65535).");
            if (!Path.GetExtension(workbench.Executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("workbench.executable must be an .exe file.");
            if (workbench.AddonDirectories.Any(p => p.Contains(',')))
                throw new InvalidDataException("workbench.addonDirectories cannot contain commas: Workbench uses commas as separators.");
        }
        var runtimePaths = config.Targets.Count > 0 ? new[] { config.GhidraHome, config.JavaHome } : [];
        if (config.LocalHttp is { Port: < 1 or > 65535 })
            throw new InvalidDataException("localHttp.port must be between 1 and 65535.");
        foreach (var p in new[] { config.DataDirectory }.Concat(runtimePaths).Concat(config.Targets.Values).Concat(config.PakDirectories.Values))
            if (!Path.IsPathFullyQualified(p)) throw new InvalidDataException("Configuration paths must be absolute: " + p);
        if (config.Targets.Keys.Any(k => !ValidLabel(k)) || config.Targets.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Targets.Count)
            throw new InvalidDataException("Native target IDs must be unique ASCII letters/digits/hyphens/underscores (1-64).");
        if (config.PakDirectories.Keys.Any(k => k.Length is < 1 or > 64 || k.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))))
            throw new InvalidDataException("PAK source IDs must be 1-64 ASCII letters, digits, hyphens or underscores.");
        foreach (var id in config.PakDirectories.Keys) PakArchive.ValidatePath(id);
        foreach (var (id, root) in config.UnpackedDirectories)
        {
            if (id.Length is < 1 or > 64 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new InvalidDataException("Unpacked source IDs must be 1-64 ASCII letters, digits, hyphens or underscores.");
            if (config.PakDirectories.TryGetValue(id, out var packed) && !root.Equals(packed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A shared packed/unpacked source ID must name the same root.");
        }
        var sourceIds = config.PakDirectories.Keys.Concat(config.UnpackedDirectories.Keys).Distinct(StringComparer.Ordinal).ToArray();
        if (sourceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sourceIds.Length)
            throw new InvalidDataException("Source IDs must be unique ignoring case.");
        if (config.PakDirectories.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.PakDirectories.Count)
            throw new InvalidDataException("PAK source IDs must be unique ignoring case.");
        foreach (var root in config.PakDirectories.Values.Concat(config.UnpackedDirectories.Values))
        {
            var input = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(config.DataDirectory));
            if (input.Equals(output, StringComparison.OrdinalIgnoreCase) ||
                input.StartsWith(output + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("dataDirectory and PAK source directories must not overlap.");
        }
        if (config.AnalysisTimeoutSeconds is < 30 or > 7200 || config.QueryTimeoutSeconds is < 10 or > 600
            || config.AnalysisCpuCount is < 1 or > 16 || config.JavaHeapMegabytes is < 1024 or > 16384)
            throw new InvalidDataException("Invalid runtime bounds; see configuration.example.json.");
        if (config.Targets.Count > 0 && Path.GetFullPath(config.DataDirectory).Split(Path.DirectorySeparatorChar).Any(p => p.StartsWith('.')))
            throw new InvalidDataException("Ghidra project paths cannot contain hidden dot-prefixed components. Choose a different dataDirectory.");
        return config;
    }

    private static bool ValidLabel(string value) => value.Length is >= 1 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Resolve only explicitly supplied locations. Never guess a Steam library or substitute
    // a different product when a configured path is absent.
    private static string ResolvePath(string? value, string baseDirectory, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException(field + " must specify a path.");
        var expanded = Regex.Replace(value, @"%(?<env>[^%]+)%|\$\{(?<folder>[^}]+)\}", match =>
        {
            string? result;
            if (match.Groups["env"].Success)
            {
                var name = match.Groups["env"].Value;
                result = Environment.GetEnvironmentVariable(name);
                if (string.IsNullOrEmpty(result)) throw new InvalidDataException($"{field}: environment variable %{name}% is unset or empty.");
            }
            else
            {
                var name = match.Groups["folder"].Value;
                if (name != "Documents") throw new InvalidDataException($"{field}: unknown folder token ${{{name}}}; supported: ${{Documents}}.");
                result = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify);
                if (string.IsNullOrEmpty(result)) throw new InvalidDataException(field + ": Windows Documents folder is unavailable; specify an explicit path.");
            }
            return result;
        });
        if (Path.IsPathRooted(expanded) && !Path.IsPathFullyQualified(expanded))
            throw new InvalidDataException(field + ": drive-relative or root-relative paths are ambiguous; use a full path or a configuration-relative path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded, baseDirectory));
    }

    public static ConfigurationCheck Check(string path)
    {
        var config = Load(path);
        var locations = new List<ConfigurationLocation>();
        void Add(string field, string value, bool file = false) =>
            locations.Add(new(field, value, file ? "file" : "directory", file ? File.Exists(value) : Directory.Exists(value)));
        foreach (var item in config.PakDirectories) Add("pakDirectories." + item.Key, item.Value);
        if (config.LocalHttp is not null) Add("localHttp.tokenFile", config.LocalHttp.TokenFile, true);
        foreach (var item in config.UnpackedDirectories) Add("unpackedDirectories." + item.Key, item.Value);
        foreach (var item in config.Targets) Add("targets." + item.Key, item.Value, true);
        foreach (var item in config.CaptureExecutables) Add("captureExecutables." + item.Key, item.Value, true);
        foreach (var item in config.LogDirectories) Add("logDirectories." + item.Key, item.Value);
        foreach (var (label, workbench) in config.WorkbenchEntries)
        {
            Add(label + ".executable", workbench.Executable, true);
            for (var i = 0; i < workbench.AddonDirectories.Length; i++)
                Add($"{label}.addonDirectories[{i}]", workbench.AddonDirectories[i]);
        }
        if (config.Targets.Count > 0)
        {
            Add("ghidraHome", config.GhidraHome);
            Add("javaHome", config.JavaHome);
        }
        return new(locations.All(p => p.Exists), config, locations.ToArray(),
            "Read-only path resolution and existence check. dataDirectory may be created on startup. Does not verify write permissions, archive compatibility, Ghidra/JDK versions, or prepare data.");
    }
}

public sealed record LocalHttpConfiguration
{
    public required int Port { get; init; }
    public required string TokenFile { get; init; }
}

public sealed record WorkbenchConfiguration
{
    public required string Executable { get; init; }
    public string? ProfileDirectory { get; init; }
    public string? LogDirectory { get; init; }
    public int? ProcessId { get; init; }
    public DateTimeOffset? ProcessStartedUtc { get; init; }
    public string[] AddonDirectories { get; init; } = [];
    public int? RuntimePort { get; init; }
}

public sealed record ConfigurationLocation(string Field, string Path, string Kind, bool Exists);
public sealed record ConfigurationCheck(bool Success, Configuration Resolved, ConfigurationLocation[] Locations, string Scope);
