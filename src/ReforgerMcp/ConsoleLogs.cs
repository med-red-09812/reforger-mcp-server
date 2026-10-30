using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ReforgerMcp;

// One reader for explicitly configured editor and independent client profiles.
public sealed partial class ConsoleLogs(Configuration config)
{
    const long MaxFileBytes = 256L * 1024 * 1024;
    static readonly Regex SessionName = new(@"^logs_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);
    static readonly Regex Prefix = new(@"^(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s+(?<category>[A-Z_0-9]{1,32})\s*(?:\((?<severity>[EWF])\))?\s*:\s*(?<message>.*)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    string Root(string source) => config.LogDirectories.TryGetValue(source, out var root)
        ? root : throw new ArgumentException("Unknown log source. Configured sources: " + string.Join(", ", config.LogDirectories.Keys));

    // -logsDir writes console.log directly; the default profile creates logs_* children.
    private string LogPath(string source, string session)
    {
        if (session != "configured" && !SessionName.IsMatch(session))
            throw new ArgumentException("session must be configured or a logs_YYYY-MM-DD_HH-MM-SS folder name.");
        return Path.Combine(session == "configured" ? Root(source) : Path.Combine(Root(source), session), "console.log");
    }
    private string[] SessionIds(string source)
    {
        var sessions = Directory.GetDirectories(Root(source)).Select(Path.GetFileName)
            .Where(p => p is not null && SessionName.IsMatch(p)).Select(p => p!);
        if (File.Exists(Path.Combine(Root(source), "console.log"))) sessions = sessions.Prepend("configured");
        return sessions.OrderByDescending(s => File.GetLastWriteTimeUtc(LogPath(source, s)))
            .ThenByDescending(s => s, StringComparer.Ordinal).ToArray();
    }

    public object Sessions(string? source, int limit)
    {
        if (limit is < 1 or > 20) throw new ArgumentException("limit must be 1-20.");
        if (source is null) return new { sources = config.LogDirectories.Select(p => new { source = p.Key, directory = p.Value, available = Directory.Exists(p.Value) }).ToArray() };
        var sessions = SessionIds(source);
        return new { source, sessions = sessions.Take(limit).Select(p =>
        {
            var file = new FileInfo(LogPath(source, p));
            return new { session = p, available = file.Exists, bytes = file.Exists ? (long?)file.Length : null,
                lastWriteUtc = file.Exists ? (DateTime?)file.LastWriteTimeUtc : null };
        }).ToArray(), total = sessions.Length, truncated = sessions.Length > limit,
            scope = "Console last-write times order sessions. 'configured' is console.log directly in the configured root. Newest does not mean running. Only console.log is read." };
    }

    public LogResult Read(string source, string? session, string scope, int cycleOffset, string? severity,
        string? category, string? query, bool regex, int startLine, int limit, string? revision, CancellationToken ct, int? afterLine = null)
    {
        severity = severity?.ToLowerInvariant();
        if (scope is not ("latest" or "session" or "tail")) throw new ArgumentException("scope must be latest, session or tail.");
        if (cycleOffset is < 0 or > 20 || (scope != "latest" && cycleOffset != 0)) throw new ArgumentException("cycleOffset must be 0-20 and requires latest scope.");
        if (limit is < 1 or > 200 || startLine < 1) throw new ArgumentException("limit must be 1-200; startLine must be positive.");
        if (severity is not (null or "error" or "warning" or "info")) throw new ArgumentException("severity must be error, warning or info.");
        if (category?.Length > 32 || query?.Length > 512) throw new ArgumentException("category/query too long (32/512 characters).");
        if (regex && string.IsNullOrEmpty(query)) throw new ArgumentException("regex requires query.");
        if (revision is not null && session is null) throw new ArgumentException("Pass the returned session with revision.");
        session ??= SessionIds(source).FirstOrDefault() ?? throw new InvalidOperationException("No log sessions available.");
        var path = LogPath(source, session);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("console.log unavailable in selected session.", path);
        var length = info.Length;
        var created = info.CreationTimeUtc;
        if (revision is not null)
        {
            var separator = revision.IndexOf(':');
            if (separator < 1 || !long.TryParse(revision[..separator], out var snapshotLength) || snapshotLength < 0)
                throw new ArgumentException("Invalid log revision.");
            if (snapshotLength > length) throw new InvalidOperationException("Log revision changed (truncated); restart without revision.");
            length = snapshotLength;
        }
        if (length > MaxFileBytes) throw new InvalidOperationException("console.log exceeds the explicit 256 MiB scan limit.");
        var timer = Stopwatch.StartNew();
        Regex? pattern = regex ? new Regex(query!, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : null;
        var cycles = new List<LogCycle>();
        LogCycle? active = null;
        var total = 0;
        string? header = null;
        var partialLastLineOmitted = false;
        using var firstHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in Lines(path, length, firstHash, ct, timer, () => partialLastLineOmitted = true))
        {
            total++;
            if (total == 2) header = Clip(line, 1000);
            var m = Prefix.Match(line);
            if (!m.Success) continue;
            var message = m.Groups["message"].Value;
            var channel = m.Groups["category"].Value;
            var nested = message.StartsWith("SCRIPT", StringComparison.Ordinal) && Regex.IsMatch(message, @"^SCRIPT\s*:");
            var normalized = nested ? message[(message.IndexOf(':') + 1)..].TrimStart() : message;
            var reload = channel == "SCRIPT" && normalized == "Reloading game scripts";
            var compiling = channel == "SCRIPT" && normalized is "Compiling GameLib scripts" or "Compiling Game scripts";
            // Nested heading opens the operation; the plain heading repeats it when diagnostics flush.
            if (reload && (nested || active?.Kind != "reload") || compiling && active is null)
            {
                active = new LogCycle(total, m.Groups["time"].Value, reload ? "reload" : "compilation", null);
                cycles.Add(active);
                if (cycles.Count > 22) cycles.RemoveAt(0);
            }
            if (active is not null && channel == "PROFILING" &&
                (active.Kind == "reload" ? message.StartsWith("Reloading game scripts took:", StringComparison.Ordinal) : message.StartsWith("Compiling Game scripts took:", StringComparison.Ordinal)))
            {
                cycles[^1] = active with { CompletionLine = total };
                active = null;
            }
        }
        var digest = Convert.ToHexString(firstHash.GetHashAndReset());
        var version = length + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{created.Ticks}|{length}|{digest}")));
        if (revision is not null && revision != version) throw new InvalidOperationException("Log revision changed; restart without revision. Do not combine pages.");
        if (afterLine is not null && cycles.Count(c => c.StartLine > afterLine && c.Kind == "reload") != 1)
            throw new InvalidOperationException("Exactly one fresh reload cycle could not be established; diagnostics are unavailable.");
        LogCycle? cycle = null;
        var from = scope == "tail" ? Math.Max(1, total - limit + 1) : 1;
        var to = total;
        if (scope == "latest")
        {
            if (cycles.Count <= cycleOffset) throw new InvalidOperationException("Compilation boundary unavailable for the requested cycle. Request session or tail explicitly.");
            var index = cycles.Count - 1 - cycleOffset;
            cycle = cycles[index];
            from = cycle.StartLine;
            if (index + 1 < cycles.Count) to = cycles[index + 1].StartLine - 1;
            if (afterLine is not null && cycle.CompletionLine is { } completion) to = completion;
        }
        var entries = new List<LogEntry>();
        var counts = new Dictionary<string, int> { ["error"] = 0, ["warning"] = 0, ["info"] = 0 };
        var categories = new Dictionary<string, int>();
        var number = 0;
        var matched = 0;
        var bytes = 0;
        int? next = null;
        string currentCategory = "UNKNOWN", currentSeverity = "info";
        using var secondHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var line in Lines(path, length, secondHash, ct, timer))
        {
            number++;
            if (number > to) continue;
            var m = Prefix.Match(line);
            if (m.Success)
            {
                currentCategory = m.Groups["category"].Value;
                currentSeverity = m.Groups["severity"].Value switch { "E" or "F" => "error", "W" => "warning", _ => "info" };
            }
            if (number < from) continue;
            counts[currentSeverity]++;
            var key = categories.ContainsKey(currentCategory) || categories.Count < 64 ? currentCategory : "OTHER";
            categories[key] = categories.GetValueOrDefault(key) + 1;
            if (severity is not null && currentSeverity != severity || category is not null && !category.Equals(currentCategory, StringComparison.OrdinalIgnoreCase)) continue;
            if (query is not null && !(pattern?.IsMatch(line) ?? line.Contains(query, StringComparison.OrdinalIgnoreCase))) continue;
            matched++;
            if (number < startLine || next is not null) continue;
            var entry = new LogEntry(number, currentCategory, currentSeverity, Clip(line, 2000), line.Length > 2000, !m.Success);
            var size = JsonSerializer.SerializeToUtf8Bytes(entry).Length;
            if (entries.Count >= limit || bytes + size > 24000) { next = number; continue; }
            entries.Add(entry);
            bytes += size;
        }
        info.Refresh();
        if (!info.Exists || info.Length < length || info.CreationTimeUtc != created || Convert.ToHexString(secondHash.GetHashAndReset()) != digest)
            throw new InvalidOperationException("Log changed during read (replacement/truncation/overwrite); retry this read-only request. No partial result returned.");
        return new(source, session, path, version, header, scope, from, to, total, cycle, counts, categories, matched,
            entries.ToArray(), next, next is not null, partialLastLineOmitted);
    }

    static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
    static IEnumerable<string> Lines(string path, long length, IncrementalHash hash, CancellationToken ct, Stopwatch timer, Action? onPartialLine = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length < length) throw new InvalidOperationException("Log changed during read (truncated).");
        file.Position = Math.Max(0, length - 1);
        var terminated = length == 0 || file.ReadByte() == '\n';
        file.Position = 0;
        using var prefix = new PrefixStream(file, length, hash);
        using var reader = new StreamReader(prefix);
        var line = reader.ReadLine();
        while (line is not null)
        {
            ct.ThrowIfCancellationRequested();
            if (timer.Elapsed > TimeSpan.FromSeconds(10))
                throw new InvalidOperationException("Log scan exceeded 10 seconds or 256 MiB; no partial result returned.");
            var following = reader.ReadLine();
            if (following is null && !terminated) { onPartialLine?.Invoke(); break; }
            yield return line;
            line = following;
        }
        if (prefix.Remaining != 0) throw new InvalidOperationException("Log changed during read (truncated).");
    }

    // Hash and bound both passes to the same byte prefix, allowing ordinary live appends.
    sealed class PrefixStream(Stream inner, long length, IncrementalHash hash) : Stream
    {
        public long Remaining { get; private set; } = length;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = inner.Read(buffer[..(int)Math.Min(buffer.Length, Remaining)]);
            hash.AppendData(buffer[..count]);
            Remaining -= count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = length;
        public override long Position { get => Length - Remaining; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed record LogCycle(int StartLine, string Time, string Kind, int? CompletionLine);
public sealed record LogEntry(int Line, string Category, string Severity, string Text, bool TextTruncated, bool Continuation);
public sealed record LogResult(string Source, string Session, string Path, string Revision, string? Header, string Scope,
    int FromLine, int ToLine, int TotalLines, LogCycle? Cycle, Dictionary<string, int> SeverityCounts, Dictionary<string, int> CategoryCounts,
    int MatchedLines, LogEntry[] Entries, int? NextLine, bool Truncated,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PartialLastLineOmitted);
