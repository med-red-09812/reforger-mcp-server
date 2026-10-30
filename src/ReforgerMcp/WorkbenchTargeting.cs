using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ReforgerMcp;

public sealed record WorkbenchPropertyPath(string Property, int Index = -1);

public sealed partial class WorkbenchSession
{
    private sealed record EditObservation(int ProcessId, long ProcessStart, string World, ulong Id, int Component,
        string Section, NativeWorkbench.TargetResult Result, DateTime Expires, WorkbenchPropertyPath[] Path, string? Property, bool PrefabDefinition);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, EditObservation> editObservations = new();
    internal Task<object> QueryEntities(int processId, string expectedWorld, int offset, int limit, CancellationToken ct,
        string action = "query", string? entityId = null, int component = -1, string? nameContains = null,
        string? className = null, string? prefabPath = null, int? subscene = null, int? layer = null, bool? locked = null, int scanLimit = 4096,
        WorkbenchPropertyPath[]? path = null, string? property = null, bool prefabDefinition = false)
    {
        if (action == "save_definition" && !prefabDefinition) throw new ArgumentException("Definition saving requires prefab scope.");
        path ??= [];
        if (path.Length > 8 || (path.Length != 0 && action is not ("properties" or "objects")) || path.Any(p => p is null ||
            string.IsNullOrWhiteSpace(p.Property) || Encoding.UTF8.GetByteCount(p.Property) > 240 || p.Property.Any(char.IsControl) || p.Index is < -1 or > 1000000))
            throw new ArgumentException("path supports up to eight object-property steps with property (1-240 UTF-8 bytes) and index (-1 for an object, otherwise array index 0-1000000). Only property/object inspection accepts a path.");
        if (action == "objects" && (string.IsNullOrWhiteSpace(property) || Encoding.UTF8.GetByteCount(property) > 240 || property.Any(char.IsControl)))
            throw new ArgumentException("Object inspection requires a property name (at most 240 UTF-8 bytes).");
        if (action != "objects" && property is not null) throw new ArgumentException("property is only supported for object-array inspection.");
        ulong id = 0;
        if (action is not ("query" or "components" or "properties" or "objects" or "save_definition") || component < -1 || (action == "components" && component != -1) ||
            (action != "query" && (entityId?.Length != 16 || !ulong.TryParse(entityId, System.Globalization.NumberStyles.HexNumber, null, out id))))
            throw new ArgumentException("Inspection requires a 16-digit entityId and component index >= -1.");
        if (processId <= 0 || offset < 0 || offset > int.MaxValue - 8192 || limit is < 1 or > 128)
            throw new ArgumentException("processId must be positive, offset nonnegative and limit 1-128.");
        if (scanLimit is < 1 or > 8192 || subscene < 0 || layer < 0 || new[] { nameContains, className, prefabPath }.Any(s => s?.Length > 1024 || s?.Contains('\0') == true))
            throw new ArgumentException("scanLimit must be 1-8192, indices nonnegative and filters at most 1024 characters without NUL.");
        if (prefabDefinition)
        {
            if (action == "query" || !System.Text.RegularExpressions.Regex.IsMatch(expectedWorld, "^\\{[0-9A-Fa-f]{16}\\}.+\\.et$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) || expectedWorld.Any(char.IsControl) || expectedWorld.Contains(';') || Encoding.UTF8.GetByteCount(expectedWorld) > 240)
                throw new ArgumentException("Prefab definition inspection requires an exact GUID-qualified .et resource and root entity ID from workbench_open_prefab.");
        }
        else if (string.IsNullOrWhiteSpace(expectedWorld) || !expectedWorld.StartsWith('$') || !expectedWorld.Contains(':') ||
            !expectedWorld.EndsWith(".ent", StringComparison.Ordinal) || expectedWorld.Any(char.IsControl) || Encoding.UTF8.GetByteCount(expectedWorld) > 240)
            throw new ArgumentException("expectedWorld must be the exact loadedWorld, at most 240 UTF-8 bytes.");
        return Start(processId, token =>
        {
            using var process = SelectProcess(processId);
            if (process.Id != processId) throw new InvalidOperationException("Workbench process changed.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager);
            RequiredWindow(windows, "Arma4WorkbenchApp.worldEditorMain");
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled)) throw Blocked(process, "Resolve modal dialogs before querying entities.");
            if (action == "save_definition") editObservations.Clear();
            using var verification = new NativeWorkbench.VerificationScope();
            NativeWorkbench.TargetResult Read(int start, int count) => (NativeWorkbench.TargetResult)NativeWorkbench.Execute(process, token, verification: verification,
                entity: new(action, expectedWorld, property ?? "", "", [0,0,0], [0,0,0]) { PrefabDefinition = prefabDefinition, Target = new(action, start, count, id, component, Path: path, PrefabDefinition: prefabDefinition) });
            var watch = Stopwatch.StartNew();
            var batchSize = action == "query" ? Math.Min(128, scanLimit) : limit;
            var result = Read(offset, batchSize);
            var maxNativeRoundTripMs = result.ElapsedMs;
            var matches = new List<NativeWorkbench.TargetRow>();
            var scanned = 0; var position = offset; var truncatedCandidates = 0;
            if (action == "query" && result.State == "observed")
            {
                var total = result.Total;
                while (true)
                {
                    var examined = Math.Min(batchSize, Math.Max(0, total - position));
                    truncatedCandidates += result.Rows.Count(r => r.TextTruncated);
                    matches.AddRange(result.Rows.Where(r => (!r.TextTruncated || (nameContains is null && className is null && prefabPath is null)) &&
                        (nameContains is null || r.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)) &&
                        (className is null || r.ClassName == className) && (prefabPath is null || r.Value.Equals(prefabPath, StringComparison.OrdinalIgnoreCase)) &&
                        (subscene is null || r.Subscene == subscene) && (layer is null || r.Layer == layer) && (locked is null || r.LayerLocked == locked)));
                    scanned += examined; position += examined;
                    if (matches.Count >= limit)
                    {
                        matches = matches.Take(limit).ToList();
                        position = matches[^1].ScanIndex + 1;
                        break;
                    }
                    if (matches.Count >= limit || position >= total || scanned >= scanLimit || watch.ElapsedMilliseconds >= 1000) break;
                    batchSize = Math.Min(128, scanLimit - scanned);
                    result = Read(position, batchSize);
                    maxNativeRoundTripMs = Math.Max(maxNativeRoundTripMs, result.ElapsedMs);
                    if (result.State != "observed" || result.Total != total)
                        return new { state = result.State == "observed" ? "population_changed" : result.State, message = "Query interrupted; start a fresh observation.", scanned };
                }
            }
            if (action == "save_definition") return new
            {
                state = result.State, processId, prefabResource = result.World, rootEntityId = entityId,
                saved = result.State == "saved", elapsedMs = result.ElapsedMs,
                message = "Current prefab-document save dispatched once. A non-saved result requires inspection; do not replay automatically."
            };
            string? editToken = null;
            if (action != "query" && result.State == "observed")
            {
                foreach (var expired in editObservations.Where(x => x.Value.Expires < DateTime.UtcNow).Select(x => x.Key).ToArray()) editObservations.TryRemove(expired, out _);
                if (editObservations.Count >= 128) editObservations.TryRemove(editObservations.Keys.First(), out _);
                editToken = Guid.NewGuid().ToString("N");
                editObservations[editToken] = new(processId, process.StartTime.ToUniversalTime().Ticks, expectedWorld, id,
                    component, action, result, DateTime.UtcNow.AddMinutes(2), path, property, prefabDefinition);
            }
            if (action != "query") return new
            {
                state = result.State, processId, world = result.World, entityId, component,
                editScope = prefabDefinition ? "prefab_definition" : "world_instance", path, property, containerInherited = result.ContainerInherited,
                inActiveSubscene = (int)(id >> 61) == result.ActiveSubscene,
                editToken, editTokenLifetimeSeconds = 120,
                valueMeaning = "Native authoring serialization, not live script state. serializedValue preserves authoring text; resolvedValue includes schema defaults; supplyingAncestor is the assigning resource path when provenanceState is resolved; localOverride means assignment on the selected editScope, rather than its ancestors. supportedActions describes type support; world, lock and freshness guards still apply.",
                total = result.Total, offset, nextOffset = offset + limit < result.Total ? offset + limit : (int?)null,
                items = result.Rows.Select(r => action is "components" or "objects"
                    ? (object)new { index = r.Layer, r.Name, r.ClassName, r.TextTruncated }
                    : new { index = r.Layer, name = r.Name, nativeType = r.ComponentCount,
                        serializedValue = r.ComponentCount is 1 or 2 or 3 or 4 or 7 or 9 or 10 or 12 or 14 or 129 or 131 or 135 or 138 ? r.Value : null,
                        resolvedValue = r.ComponentCount is 1 or 2 or 3 or 4 or 7 or 9 or 10 or 12 or 14 or 129 or 131 or 135 or 138 ? (r.HasEffectiveAssignment ? r.Value : r.DefaultValue) : null,
                        valueOrigin = r.HasEffectiveAssignment ? (r.LocalOverride ? (prefabDefinition ? "definition" : "instance") : "ancestor") : "schema_default",
                        supplyingAncestor = r.HasEffectiveAssignment && !r.LocalOverride && r.SupplierResource.Length > 0 ? r.SupplierResource : null,
                        provenanceState = !r.HasEffectiveAssignment || r.LocalOverride || r.SupplierResource.Length > 0 ? "resolved" : "unavailable",
                        localOverride = r.LocalOverride, r.TextTruncated,
                        arrayCount = r.ValueArrayCount >= 0 ? (int?)r.ValueArrayCount : null,
                        supportedActions = r.TextTruncated ? Array.Empty<string>() : r.ComponentCount is 129 or 131 or 135 or 138
                            ? new[] { "set", "clear", "insert_value", "remove_value" }
                            : r.ComponentCount is 1 or 2 or 3 or 4 or 7 or 9 or 10 or 14 ? new[] { "set", "clear" } : Array.Empty<string>() }),
                elapsedMs = result.ElapsedMs
            };
            return new
            {
                state = result.State, processId, world = result.World, totalLoaded = result.Total, activeSubscene = result.ActiveSubscene,
                offset, scanned, returned = matches.Count, nextOffset = position < result.Total && result.State == "observed" ? position : (int?)null,
                consistency = "Live observation per page; editor changes between pages can change membership/order.",
                truncatedCandidates,
                textFilterCoverage = truncatedCandidates > 0 && (nameContains is not null || className is not null || prefabPath is not null)
                    ? "incomplete_truncated_candidates" : "complete_for_scanned_text",
                entities = matches.Select(r => new { entityId = r.Id.ToString("X16"), r.Name, r.ClassName, r.Subscene,
                    r.Layer, r.ComponentCount, r.LayerLocked, inActiveSubscene = r.Subscene == result.ActiveSubscene, prefabPath = r.Value, r.TextTruncated }),
                elapsedMs = watch.ElapsedMilliseconds, maxNativeRoundTripMs
            };
        }, ct);
    }

    internal Task<object> EditEntity(string editToken, string action, string? key, JsonElement? value, int component, CancellationToken ct, int index = -1)
    {
        if (action is not ("set" or "clear" or "add_component" or "remove_component" or "insert_object" or "remove_object" or "insert_value" or "remove_value")) throw new ArgumentException("Unsupported edit action.");
        if (!editObservations.TryGetValue(editToken, out var target)) throw new ArgumentException("Unknown editToken; inspect again.");
        var processId = target.ProcessId;
        return Start(processId, token =>
        {
            if (!editObservations.TryGetValue(editToken, out var observation) || observation.Expires < DateTime.UtcNow)
                throw new ArgumentException("Expired or unknown editToken. Inspect again; never replay an uncertain mutation.");
            var result = observation.Result;
            var container = result.ContainerSource;
            string serialized = "", name = key ?? "", expected = "";
            var expectedLocalOverride = false;
            var expectedValueArrayCount = -1;
            long expectedMember = 0;
            var selectedComponent = observation.Component;
            var nativeAction = action is "insert_value" or "remove_value" ? "set" : action;
            if (action is "set" or "clear" or "insert_value" or "remove_value")
            {
                if (observation.Section != "properties") throw new ArgumentException("Inspect the target properties first.");
                var property = result.Rows.SingleOrDefault(r => r.Name == key) ?? throw new ArgumentException("Property was not in this inspection page.");
                if (property.TextTruncated) throw new ArgumentException("Truncated property cannot be edited.");
                if (property.ComponentCount is not (1 or 2 or 3 or 4 or 7 or 9 or 10 or 14 or 129 or 131 or 135 or 138))
                    throw new ArgumentException("Unsupported property type; inspect nested objects through a path and object arrays through section objects.");
                expected = property.Value;
                expectedLocalOverride = property.LocalOverride;
                expectedValueArrayCount = property.ValueArrayCount;
                if (action == "set") serialized = SerializeProperty(property.ComponentCount, value ?? throw new ArgumentException("value is required."));
                if (action is "insert_value" or "remove_value")
                {
                    if (property.ComponentCount is not (129 or 131 or 135 or 138)) throw new ArgumentException("Expected a supported value-array property.");
                    var resolved = property.HasEffectiveAssignment ? property.Value : property.DefaultValue;
                    var entries = property.ValueArrayCount == 0 && resolved.Length == 0 ? new List<string>() : resolved.Split(',').ToList();
                    if (entries.Count != property.ValueArrayCount || entries.Any(v => v.Length == 0))
                        throw new ArgumentException("Native array serialization is ambiguous; replace the complete array with set instead.");
                    if (index < 0 || index > entries.Count || (action == "remove_value" && index == entries.Count))
                        throw new ArgumentException("index is outside the inspected value array.");
                    if (action == "remove_value") entries.RemoveAt(index);
                    else
                    {
                        var entry = SerializeProperty(property.ComponentCount - 128, value ?? throw new ArgumentException("value is required."));
                        if (entry.Length == 0 || entry.Contains(',') || entry.Contains('\0'))
                            throw new ArgumentException("Native value arrays cannot represent empty entries or entries containing commas or NUL.");
                        entries.Insert(index, entry);
                    }
                    serialized = string.Join(",", entries);
                }
            }
            else if (action is "insert_object" or "remove_object")
            {
                if (observation.Section != "objects") throw new ArgumentException("Inspect the object array first using section objects and property.");
                if (index < 0 || index > result.Total || (action == "remove_object" && index == result.Total))
                    throw new ArgumentException("index is outside the inspected object array.");
                if (index < result.Total)
                    expectedMember = (result.Rows.SingleOrDefault(r => r.Layer == index) ?? throw new ArgumentException("Inspect the page containing index first.")).Source;
                else if (result.Total != 0 && !result.Rows.Any(r => r.Layer == result.Total - 1))
                    throw new ArgumentException("Inspect the final page before appending.");
                if (action == "insert_object")
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,239}$")) throw new ArgumentException("key must be the inserted object's class name.");
                    serialized = name;
                }
                name = observation.Property!;
            }
            else
            {
                if (observation.Section != "components" || observation.Component != -1) throw new ArgumentException("Inspect the entity components first.");
                if (action == "add_component")
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]{0,239}$")) throw new ArgumentException("key must be a component class name.");
                }
                else
                {
                    var selected = result.Rows.SingleOrDefault(r => r.Layer == component) ?? throw new ArgumentException("Component index was not in this inspection page.");
                    selectedComponent = component; container = selected.Source; name = selected.ClassName;
                }
            }
            if (Encoding.UTF8.GetByteCount(name) > 240 || Encoding.UTF8.GetByteCount(serialized) > 1023 || serialized.Contains('\0'))
                throw new ArgumentException("Property name limit is 240 UTF-8 bytes and value limit is 1023 bytes without NUL.");
            using var process = SelectProcess(processId);
            if (process.Id != observation.ProcessId || process.StartTime.ToUniversalTime().Ticks != observation.ProcessStart)
                throw new InvalidOperationException("Workbench process changed; inspect again.");
            var windows = Windows(process.Id);
            RequiredWindow(windows, ResourceManager); RequiredWindow(windows, "Arma4WorkbenchApp.worldEditorMain");
            if (windows.Any(IsModal) || windows.Any(w => !w.Current.IsEnabled)) throw Blocked(process, "Resolve modal dialogs before editing entities.");
            // Consume all observations before dispatch: callbacks may replace entities
            // and component indices. No automatic retry or post-edit pointer reuse.
            editObservations.Clear();
            var edited = (NativeWorkbench.TargetResult)NativeWorkbench.Execute(process, token,
                entity: new(nativeAction, observation.World, name, serialized, [0,0,0], [0,0,0])
                { PrefabDefinition = observation.PrefabDefinition, Target = new(nativeAction, Id: observation.Id, Component: selectedComponent, ExpectedSource: result.EntitySource,
                    ExpectedContainer: container, ExpectedWorld: result.WorldIdentity, ExpectedValue: expected,
                    ExpectedClass: action == "remove_component" ? 0 : result.ClassIdentity, ExpectedLocalOverride: expectedLocalOverride,
                    Path: observation.Path, ExpectedPathSources: result.PathSources,
                    ArrayIndex: index, ExpectedArrayCount: result.Total, ExpectedMember: expectedMember, PrefabDefinition: observation.PrefabDefinition,
                    ExpectedValueArrayCount: expectedValueArrayCount) });
            if (action is "insert_object" or "remove_object" && edited.State == "updated")
            {
                var after = (NativeWorkbench.TargetResult)NativeWorkbench.Execute(process, token,
                    entity: new("objects", observation.World, observation.Property!, "", [0,0,0], [0,0,0])
                    { PrefabDefinition = observation.PrefabDefinition, Target = new("objects", Limit: 1, Id: observation.Id, Component: selectedComponent, Path: observation.Path, PrefabDefinition: observation.PrefabDefinition) });
                var expectedCount = result.Total + (action == "insert_object" ? 1 : -1);
                return new { state = after.State != "observed" ? "edit_readback_unavailable" : after.Total == expectedCount ? "updated" : "edit_effect_mismatch",
                    action, entityId = observation.Id.ToString("X16"), component = selectedComponent, property = observation.Property, index,
                    beforeCount = result.Total, afterCount = after.State == "observed" ? after.Total : (int?)null, readbackState = after.State,
                    world = edited.World, unsaved = true, message = "Array edit dispatched once. Inspect resulting members; never replay a mismatch or uncertain outcome automatically." };
            }
            return new { state = edited.State, action, entityId = observation.Id.ToString("X16"), component = selectedComponent,
                world = edited.World, unsaved = edited.State is "updated" or "edit_action_incomplete", elapsedMs = edited.ElapsedMs,
                message = edited.State == "inherited_member_removal_unsupported"
                    ? "No change made. This entry belongs to an ancestor prefab. The current native remove command does not persist its deletion on this instance; edit the defining prefab instead."
                    : observation.PrefabDefinition
                        ? "Inspect again for readback and fresh edit tokens. Save this definition with workbench_save_prefab_definition."
                        : "Inspect again for readback and fresh edit tokens. Changes use editor undo actions; world saving is separate." };
        }, ct);
    }

    private static string SerializeProperty(int type, JsonElement value)
    {
        if (type is 129 or 131 or 135 or 138)
        {
            if (value.ValueKind != JsonValueKind.Array) throw new ArgumentException("Expected a JSON array.");
            var entries = value.EnumerateArray().Select(v => SerializeProperty(type - 128, v)).ToArray();
            if (entries.Any(v => v.Length == 0 || v.Contains(',') || v.Contains('\0')))
                throw new ArgumentException("Native value arrays cannot represent empty entries or entries containing commas or NUL.");
            return string.Join(",", entries);
        }
        string Number(JsonElement v) => v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var n) && float.IsFinite(n)
            ? n.ToString("R", CultureInfo.InvariantCulture) : throw new ArgumentException("Expected a finite number.");
        return type switch
        {
            1 => Number(value),
            2 or 3 or 4 => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == type
                ? string.Join(" ", value.EnumerateArray().Select(Number)) : throw new ArgumentException($"Expected {type} finite vector coordinates."),
            7 => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var integer) ? integer.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("Expected a 32-bit integer."),
            9 => value.ValueKind == JsonValueKind.True ? "1" : value.ValueKind == JsonValueKind.False ? "0" : throw new ArgumentException("Expected a boolean."),
            10 or 14 => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException("Expected a string."),
            _ => throw new ArgumentException("Unsupported property type.")
        };
    }
}

[McpServerToolType]
public sealed class WorkbenchTargetTools(WorkbenchSession session)
{
    [McpServerTool(Name = "workbench_edit_entity", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Edit inspected world instance or opened prefab according to editToken scope; unsaved undo action. Token consumed before dispatch. Locked/stale targets and inherited entry deletion are unsupported.")]
    public async Task<object> Edit(string editToken, [Description("set|clear: property key, typed JSON value for set. add_component: class key; remove_component: observed component index. insert_object: class key/insertion index; remove_object: local entry index; both require objects token. insert_value|remove_value: properties token/property key/entry index; insertion takes typed value.")] string action, string? key = null, [Description("Typed JSON; value-array set uses array. Native bool arrays use 0/1. Empty entries and embedded commas unsupported.")] JsonElement? value = null, int component = -1, int index = -1, CancellationToken cancellationToken = default)
    {
        try { return await session.EditEntity(editToken, action, key, value, component, cancellationToken, index); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }
    [McpServerTool(Name = "workbench_inspect_entity", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect current editor entityId in exact expectedWorld; returns scoped editToken. section=components|properties|objects; objects requires property. component=-1 means entity, otherwise observed component index. Scalar values are serialized text; localOverride means direct assignment.")]
    public async Task<object> Inspect(int processId, string expectedWorld, string entityId, string section = "components", int component = -1,
        int offset = 0, int limit = 100, [Description("Up to 8 {property,index} object steps; index=-1 for single object, otherwise zero-based array index.")] WorkbenchPropertyPath[]? path = null, string? property = null, CancellationToken cancellationToken = default)
    {
        if (section is not ("components" or "properties" or "objects")) throw new McpException("section must be components, properties or objects.");
        try { return await session.QueryEntities(processId, expectedWorld, offset, limit, cancellationToken, section, entityId, component, path: path, property: property); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }
    [McpServerTool(Name = "workbench_save_prefab_definition", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Save all pending definitions in the opened prefab document using exact prefabResource/rootEntityId from workbench_open_prefab; not a world save.")]
    public async Task<object> SavePrefabDefinition(int processId, string prefabResource, string rootEntityId, CancellationToken cancellationToken = default)
    {
        try { return await session.QueryEntities(processId, prefabResource, 0, 1, cancellationToken, "save_definition", rootEntityId, prefabDefinition: true); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "workbench_inspect_prefab", ReadOnly = true, OpenWorld = false)]
    [Description("Inspect opened prefab using exact prefabResource/rootEntityId; returns definition-scoped editToken. section=components|properties|objects; objects requires property. component=-1 means root, otherwise observed component index. Inspection does not save edits.")]
    public async Task<object> InspectPrefab(int processId, string prefabResource, string rootEntityId, string section = "components", int component = -1,
        int offset = 0, int limit = 100, [Description("Up to 8 {property,index} object steps; index=-1 for single object, otherwise zero-based array index.")] WorkbenchPropertyPath[]? path = null, string? property = null, CancellationToken cancellationToken = default)
    {
        if (section is not ("components" or "properties" or "objects")) throw new McpException("section must be components, properties or objects.");
        try { return await session.QueryEntities(processId, prefabResource, offset, limit, cancellationToken, section, rootEntityId, component, path: path, property: property, prefabDefinition: true); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }

    [McpServerTool(Name = "workbench_query_entities", ReadOnly = true, OpenWorld = false)]
    [Description("Query live editor entities: name substring (case-insensitive), exact class/prefabPath (path, not GUID/ancestry), subscene/layer/lock. limit=1-128; scanLimit=1-8192. nextOffset continues the same filters, including after zero matches. Pages are live observations.")]
    public async Task<object> Query(int processId, string expectedWorld, int offset = 0, int limit = 100,
        string? nameContains = null, string? className = null, string? prefabPath = null, int? subscene = null, int? layer = null,
        bool? locked = null, int scanLimit = 4096, CancellationToken cancellationToken = default)
    {
        try { return await session.QueryEntities(processId, expectedWorld, offset, limit, cancellationToken,
            nameContains: nameContains, className: className, prefabPath: prefabPath, subscene: subscene, layer: layer, locked: locked, scanLimit: scanLimit); }
        catch (Exception e) when (e is not (McpException or OperationCanceledException)) { throw new McpException(e.Message); }
    }
}
