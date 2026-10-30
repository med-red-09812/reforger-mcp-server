using System.Text;

namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    internal const int TargetRowSize = 4096, TargetPageSize = 128;
    internal const int TargetInputOffset = 4096 + TargetPageSize * TargetRowSize;
    internal sealed record TargetCommand(string Action, int Offset = 0, int Limit = TargetPageSize, ulong Id = 0, int Component = -1,
        long ExpectedSource = 0, long ExpectedContainer = 0, long ExpectedWorld = 0, string? ExpectedValue = null,
        long ExpectedClass = 0, bool ExpectedLocalOverride = false, WorkbenchPropertyPath[]? Path = null, long[]? ExpectedPathSources = null,
        int ArrayIndex = -1, int ExpectedArrayCount = -1, long ExpectedMember = 0, bool PrefabDefinition = false,
        int ExpectedValueArrayCount = -1);
    internal sealed record TargetRow(ulong Id, long Source, string Name, string ClassName, int Subscene, int Layer,
        int ComponentCount, bool LayerLocked, bool TextTruncated, string Value, bool LocalOverride, int ScanIndex, bool HasEffectiveAssignment, string DefaultValue, string SupplierResource, int ValueArrayCount);
    internal sealed record TargetResult(string State, string? World, long WorldIdentity, int Total, TargetRow[] Rows, long ElapsedMs,
        long EntitySource, long ContainerSource, int ActiveSubscene, long ClassIdentity, bool ContainerInherited, long[] PathSources);

    private static TargetResult ReadTargets(byte[] header, byte[] rows, string? world, long elapsed)
    {
        var state = BitConverter.ToInt32(header, 72) switch
        {
            2 => "observed", 4 => "incompatible_editor", 5 => "editor_busy", 7 => "world_unavailable",
            8 => "world_changed", 10 => "edit_action_rejected", 11 => "layer_locked", 12 => "entity_missing",
            14 => "edit_action_incomplete", 15 => "target_changed", 16 => "inherited_subscene_read_only",
            17 => "property_missing", 18 => "value_changed", 19 => "updated", 20 => "edit_rejected", 21 => "prefab_mode_unsupported", 22 => "path_missing_or_incompatible", 23 => "array_storage_changed", 24 => "inherited_member_removal_unsupported", 25 => "saved", 26 => "save_failed", 27 => "save_incomplete", _ => "outcome_unknown"
        };
        var count = BitConverter.ToInt32(header, 116);
        if (count < 0 || count > TargetPageSize) throw new InvalidOperationException("Invalid native target count.");
        string Text(int offset, int length) => Encoding.UTF8.GetString(rows, offset, length).Split('\0')[0];
        var result = new List<TargetRow>();
        for (var i = 0; i < count; i++)
        {
            var o = i * TargetRowSize;
            result.Add(new(BitConverter.ToUInt64(rows, o + 8), BitConverter.ToInt64(rows, o), Text(o + 32, 240),
                Text(o + 272, 240), BitConverter.ToInt32(rows, o + 20), BitConverter.ToInt32(rows, o + 16),
                BitConverter.ToInt32(rows, o + 24), rows[o + 28] != 0, rows[o + 29] != 0, Text(o + 512, 1024), rows[o + 30] != 0,
                BitConverter.ToInt32(rows, o + 1536), rows[o + 31] != 0, Text(o + 1544, 1024), Text(o + 2568, 240), BitConverter.ToInt32(rows, o + 2808)));
        }
        return new(state, world, BitConverter.ToInt64(header, 120), BitConverter.ToInt32(header, 112), result.ToArray(), elapsed,
            BitConverter.ToInt64(header, 304), BitConverter.ToInt64(header, 312), BitConverter.ToInt32(header, 384), BitConverter.ToInt64(header, 392), header[400] != 0,
            Enumerable.Range(0, 9).Select(i => BitConverter.ToInt64(header, 408 + i * 8)).ToArray());
    }

    private static void EmitTarget(E e, nint image, TargetCommand command)
    {
        void I(int value) => e.B(BitConverter.GetBytes(value));
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        void Api(int slot) { e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0x8b, 0x01, 0xff, 0x90); I(e.Profile.WorldApiSlot(slot)); }
        var mutation = command.Action is "set" or "clear" or "add_component" or "remove_component" or "insert_object" or "remove_object";
        // All source reads and calls occur on the editor thread. No source pointer is
        // dereferenced by the controlling process after this callback returns.
        e.B(0x48, 0x8b, 0x43, 96, 0x48, 0x8b, 0x40, 0x10, 0x48, 0x8b, 0x80, 0x80, 0, 0, 0,
            0x48, 0x89, 0x43, 120);
        e.B(0x8b, 0x88); I(0x186bc); e.B(0x89, 0x8b); I(384);
        if (command.Action == "query") { Api(0xa8); e.B(0x89, 0x43, 112); }
        else
        {
            e.B(0x48, 0xba); e.Q(unchecked((long)command.Id)); Api(0xa0);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "entityMissing");
            if (command.PrefabDefinition)
            {
                e.B(0x48, 0x8b, 0x40, 32, 0x48, 0x85, 0xc0); e.J(0x84, "entityMissing");
            }
            e.B(0x48, 0x89, 0x43, 104);
            e.B(0x48, 0x89, 0x83); I(304);
            if (command.Action == "save_definition")
            {
                Api(0x48); e.B(0x84, 0xc0); e.J(0x85, "entityBusy");
                // Full prefab-editor save owner flushes pending modifications,
                // saves its changed definitions and refreshes editor state.
                // SaveEntityTemplate alone writes the file but leaves pending edits.
                e.B(0x48, 0x8b, 0x4b, 88); Call(0x27a0a0);
                e.B(0x48, 0x8b, 0x4b, 88, 0x48, 0x81, 0xc1, 0xa0, 0, 0, 0, 0x48, 0x8b, 0x01,
                    0xba, 0x0c, 0, 0, 0, 0xff, 0x50, 0x70);
                e.B(0xc7, 0x43, 72, 27, 0, 0, 0, 0x84, 0xc0); e.J(0x85, "return");
                e.B(0xc7, 0x43, 72, 25, 0, 0, 0); e.J(0x84, "return", true);
            }

            if (mutation)
            {
                e.B(0x48, 0xba); e.Q(command.ExpectedSource); e.B(0x48, 0x39, 0xd0); e.J(0x85, "targetChanged");
                e.B(0x48, 0xba); e.Q(command.ExpectedWorld); e.B(0x48, 0x39, 0x53, 120); e.J(0x85, "targetChanged");
                Api(0x48); e.B(0x84, 0xc0); e.J(0x85, "entityBusy");
                if (!command.PrefabDefinition)
                {
                e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x8b, 0x50, 40, 0x48, 0xc1, 0xea, 61,
                    0x48, 0x8b, 0x4b, 120, 0x3b, 0x91); I(0x186bc); e.J(0x85, "targetInherited");
                e.B(0x44, 0x8b, 0x40, 20); Call(0x1a74310); e.B(0x84, 0xc0); e.J(0x85, "entityLayerLocked");
                }
            }
            if (command.Action == "components" || command.Component >= 0)
            {
                e.B(0x48, 0x8b, 0x4b, 104); Call(0x1e6a060); e.B(0x89, 0x43, 112);
                if (command.Component >= 0)
                {
                    e.B(0x3d); I(command.Component); e.J(0x86, "entityMissing");
                    e.B(0x48, 0x8b, 0x4b, 104, 0xba); I(command.Component); Call(0x1e69fe0);
                    e.B(0x48, 0x85, 0xc0); e.J(0x84, "entityMissing"); e.B(0x48, 0x89, 0x43, 104);
                }
            }
            e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x89, 0x83); I(408);
            if (command.Component >= 0)
            {
                // GetComponent may return the prefab's shared component source.
                // Compare the component with the ancestor array by its engine name,
                // as the editor's localization operation does (not GetParent).
                e.B(0x48, 0x8b, 0x8b); I(304);
                e.B(0x48, 0xba); e.Image(image, 0x3ca125c); e.B(0x8b, 0x12);
                e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
                e.B(0x48, 0x85, 0xc0); e.J(0x84, "componentInherited");
                e.B(0x48, 0x8b, 0x8b); I(304); e.B(0x48, 0x8b, 0x49, 32);
                e.B(0x48, 0x85, 0xc9); e.J(0x84, "componentOwnershipDone");
                e.B(0x48, 0xba); e.Image(image, 0x3ca125c); e.B(0x8b, 0x12);
                e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
                e.B(0x48, 0x85, 0xc0); e.J(0x84, "componentOwnershipDone");
                e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x93); I(408);
                e.B(0x48, 0x83, 0xc2, 8, 0x45, 0x31, 0xc0); Call(0x1a93450);
                e.B(0x48, 0x3b, 0x83); I(408); e.J(0x85, "componentOwnershipDone");
                e.Label("componentInherited"); e.B(0xc6, 0x83); I(400); e.B(1);
                e.Label("componentOwnershipDone"); e.B(0x48, 0x8b, 0x83); I(408);
            }
            if (mutation && command.Path is { Length: > 0 })
            {
                e.B(0x48, 0xba); e.Q(command.ExpectedPathSources![0]); e.B(0x48, 0x39, 0xd0); e.J(0x85, "targetChanged");
            }
            if (command.Path is { Length: > 0 })
            {
                // Match the owning editor's path resolver (141a88840): use
                // effective object storage or the schema default, then select
                // one object/array member. No persistent controller dereference.
                for (var step = 0; step < command.Path.Length; step++)
                {
                    var part = command.Path[step];
                    e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x89, 0x83); I(504);
                    e.B(0x48, 0x8b, 0x4b, 104, 0x48, 0x8b, 0x49, 24, 0x48, 0x8d, 0x93); I(TargetInputOffset + step * 256); Call(0x1e39810);
                    e.B(0x83, 0xf8, 0xff); e.J(0x84, "targetPathMissing");
                    e.B(0x89, 0x83); I(404);
                    e.B(0x89, 0x83); I(TargetInputOffset + 2048 + step * 4);
                    e.B(0x48, 0x8b, 0x4b, 104, 0x48, 0x8b, 0x51, 24, 0x48, 0x8b, 0x52, 40,
                        0x48, 0x6b, 0xc0, 0x58, 0x81, 0x3c, 0x02); I(part.Index < 0 ? 13 : 141); e.J(0x85, "targetPathMissing");
                    e.B(0x8b, 0x93); I(404); e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
                    e.B(0x48, 0x85, 0xc0); e.J(0x85, "pathStorage" + step);
                    e.B(0x48, 0x8b, 0x4b, 104, 0x48, 0x8b, 0x49, 24, 0x48, 0x8b, 0x49, 40, 0x8b, 0x93); I(404);
                    e.B(0x48, 0x6b, 0xd2, 0x58, 0x48, 0x8b, 0x44, 0x11, 16);
                    e.Label("pathStorage" + step); e.B(0x48, 0x85, 0xc0); e.J(0x84, "targetPathMissing");
                    if (part.Index >= 0)
                    {
                        e.B(0x81, 0x78, 12); I(part.Index); e.J(0x86, "targetPathMissing");
                        e.B(0x48, 0x8b, 0x00, 0x48, 0x85, 0xc0); e.J(0x84, "targetPathMissing");
                        e.B(0x48, 0x8b, 0x80); I(checked(part.Index * 8));
                    }
                    else e.B(0x48, 0x8b, 0x00);
                    e.B(0x48, 0x85, 0xc0); e.J(0x84, "targetPathMissing");
                    e.B(0x48, 0x89, 0x43, 104);
                    e.B(0x48, 0x89, 0x83); I(416 + step * 8);
                    CheckChildOwnership("pathOwnership" + step, 504, 416 + step * 8, TargetInputOffset + 2048 + step * 4, part.Index >= 0);
                    e.B(0x48, 0x8b, 0x83); I(416 + step * 8);
                    if (mutation)
                    {
                        e.B(0x48, 0xba); e.Q(command.ExpectedPathSources![step + 1]); e.B(0x48, 0x39, 0xd0); e.J(0x85, "targetChanged");
                    }
                }
            }
            if (command.Action == "properties")
            {
                e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x8b, 0x40, 24, 0x8b, 0x40, 52, 0x89, 0x43, 112);
            }
            e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x89, 0x83); I(280);
            e.B(0x48, 0x89, 0x83); I(312);
            e.B(0x48, 0x8b, 0x50, 24, 0x48, 0x89, 0x93); I(392);
            if (command.Action is "objects" or "insert_object" or "remove_object") ResolveArray();
            if (mutation)
            {
                e.B(0x48, 0xba); e.Q(command.ExpectedContainer); e.B(0x48, 0x39, 0xd0); e.J(0x85, "targetChanged");
                if (command.ExpectedClass != 0)
                {
                    e.B(0x48, 0xba); e.Q(command.ExpectedClass); e.B(0x48, 0x39, 0x50, 24); e.J(0x85, "targetChanged");
                }
                EmitMutation();
                e.J(0x84, "return", true);
            }
        }
        e.B(0xc7, 0x83); I(256); I(command.Offset);
        e.B(0x48, 0x8d, 0x83); I(4096); e.B(0x48, 0x89, 0x83); I(264);
        e.Label("targetLoop");
        e.B(0x81, 0xbb); I(256); I(command.Offset + command.Limit);
        e.J(0x83, "targetDone");
        e.B(0x8b, 0x93); I(256); e.B(0x3b, 0x53, 112); e.J(0x83, "targetDone");
        if (command.Action == "properties")
        {
            e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x89, 0x52, 16);
            // Property definition: 0x58-byte entries, native type at +0 and name at +8.
            e.B(0x48, 0x8b, 0x83); I(280); e.B(0x48, 0x8b, 0x40, 24, 0x48, 0x8b, 0x40, 40,
                0x48, 0x6b, 0xca, 0x58, 0x48, 0x01, 0xc8, 0x8b, 0x08, 0x41, 0x89, 0x4a, 24, 0x48, 0x8b, 0x50, 8);
            Copy("propertyName", 32, 240);
            e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x8b, 0x93); I(256);
            e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
            e.B(0x80, 0xbb); I(400); e.B(0); e.J(0x84, "propertyLocalContainer");
            e.B(0x31, 0xc0); e.Label("propertyLocalContainer");
            e.B(0x48, 0x85, 0xc0, 0x0f, 0x95, 0xc0, 0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x88, 0x42, 30);
            e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x8b, 0x93); I(256);
            e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
            e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0xc7, 0x82); I(2808); I(-1);
            foreach (var type in new[] { 129, 131, 135, 138 })
            { e.B(0x41, 0x81, 0x7a, 24); I(type); e.J(0x84, "valueArrayCount"); }
            e.J(0x84, "valueArrayCountDone", true);
            e.Label("valueArrayCount");
            e.B(0x48, 0x89, 0xc2, 0x48, 0x85, 0xd2); e.J(0x85, "valueArrayStorage");
            e.B(0x48, 0x8b, 0x93); I(280); e.B(0x48, 0x8b, 0x52, 24, 0x48, 0x8b, 0x52, 40, 0x8b, 0x8b); I(256);
            e.B(0x48, 0x6b, 0xc9, 0x58, 0x48, 0x8b, 0x54, 0x0a, 16);
            e.Label("valueArrayStorage");
            e.B(0x31, 0xc9, 0x48, 0x85, 0xd2); e.J(0x84, "valueArrayStoreCount");
            e.B(0x8b, 0x4a, 12);
            e.Label("valueArrayStoreCount"); e.B(0x41, 0x89, 0x8a); I(2808);
            e.Label("valueArrayCountDone");
            e.B(0x48, 0x85, 0xc0, 0x0f, 0x95, 0xc0, 0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x88, 0x42, 31);
            // Object containers are inspected through paths; value arrays use the
            // same native authoring serializer as scalar properties.
            e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x8b, 0x42, 24, 0x83, 0xf8, 1); e.J(0x82, "propertySkipValue");
            e.B(0x83, 0xf8, 4); e.J(0x86, "propertyValue");
            foreach (var type in new[] { 7, 9, 10, 12, 14 }) { e.B(0x83, 0xf8, (byte)type); e.J(0x84, "propertyValue"); }
            foreach (var type in new[] { 129, 131, 135, 138 }) { e.B(0x3d); I(type); e.J(0x84, "propertyValue"); }
            e.J(0x84, "propertySkipValue", true);
            e.Label("propertyValue");
            e.B(0x48, 0xc7, 0x83); I(288); I(0);
            e.B(0x4c, 0x8b, 0x83); I(280); e.B(0x49, 0x8b, 0x48, 24, 0x8b, 0x93); I(256);
            e.B(0x4c, 0x8d, 0x8b); I(288); e.B(0x48, 0x8b, 0x01, 0xff, 0x50, 0x58);
            e.B(0x48, 0x8b, 0x93); I(288); Copy("propertyValueText", 512, 1024);
            e.B(0x48, 0x8b, 0x8b); I(288); e.B(0x48, 0x85, 0xc9); e.J(0x84, "propertyDefault");
            e.B(0xf0, 0xff, 0x49, 0xfc); e.J(0x85, "propertyDefault");
            e.B(0x48, 0x83, 0xe9, 12, 0x31, 0xd2); Call(0x1e32f10);
            e.Label("propertyDefault");
            e.B(0x48, 0xc7, 0x83); I(288); I(0);
            e.B(0x4c, 0x8b, 0x83); I(280); e.B(0x49, 0x8b, 0x48, 24, 0x8b, 0x93); I(256);
            e.B(0x4c, 0x8d, 0x8b); I(288); e.B(0x48, 0x8b, 0x01, 0xff, 0x50, 0x50);
            e.B(0x48, 0x8b, 0x93); I(288); Copy("propertyDefaultText", 1544, 1024);
            e.B(0x48, 0x8b, 0x8b); I(288); e.B(0x48, 0x85, 0xc9); e.J(0x84, "propertySkipValue");
            e.B(0xf0, 0xff, 0x49, 0xfc); e.J(0x85, "propertySkipValue");
            e.B(0x48, 0x83, 0xe9, 12, 0x31, 0xd2); Call(0x1e32f10);
            e.Label("propertySkipValue");
            // Find the actual assigning source, then its resource root. An
            // inherited value can skip multiple ancestors. Bound malformed chains.
            e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x80, 0x7a, 31, 0); e.J(0x84, "propertySupplierDone");
            e.B(0x41, 0x80, 0x7a, 30, 0); e.J(0x85, "propertySupplierDone");
            e.B(0x48, 0x8b, 0x83); I(280); e.B(0x48, 0x89, 0x83); I(528);
            e.B(0xc7, 0x83); I(536); I(64);
            e.Label("propertySupplierLoop");
            e.B(0x48, 0x8b, 0x8b); I(528); e.B(0x8b, 0x93); I(256);
            e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
            e.B(0x48, 0x85, 0xc0); e.J(0x85, "propertySupplierFound");
            e.B(0x48, 0x8b, 0x83); I(528); e.B(0x48, 0x8b, 0x40, 32);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "propertySupplierDone");
            e.B(0x48, 0x89, 0x83); I(528); e.B(0xff, 0x8b); I(536); e.J(0x85, "propertySupplierLoop");
            e.J(0x84, "propertySupplierDone", true);
            e.Label("propertySupplierFound");
            // Property-owned child containers need not have GetParent links.
            // Resolve this exact native path in each prefab ancestor and identify
            // the oldest resource that owns the assigning container identity.
            BuildContainerPath();
            e.B(0x48, 0x8b, 0x83); I(304); e.B(0x48, 0x8b, 0x40, 32, 0x48, 0x89, 0x83); I(544);
            e.B(0x48, 0xc7, 0x83); I(552); I(0); e.B(0xc7, 0x83); I(536); I(64);
            e.Label("propertyResourceLoop");
            e.B(0x48, 0x8b, 0x83); I(544); e.B(0x48, 0x85, 0xc0); e.J(0x84, "propertyResourceDone");
            e.B(0x48, 0x89, 0x83); I(320); e.B(0x48, 0x8d, 0x8b); I(320); e.B(0x31, 0xd2); Call(0x1ad6860);
            e.B(0x48, 0x3b, 0x83); I(528); e.J(0x85, "propertyResourceNext");
            e.B(0x48, 0x8b, 0x83); I(544); e.B(0x48, 0x89, 0x83); I(552);
            e.Label("propertyResourceNext");
            e.B(0x48, 0x8b, 0x83); I(544); e.B(0x48, 0x8b, 0x40, 32, 0x48, 0x89, 0x83); I(544);
            e.B(0xff, 0x8b); I(536); e.J(0x85, "propertyResourceLoop");
            e.B(0x48, 0xc7, 0x83); I(552); I(0); // depth limit is unavailable, not a guessed supplier
            e.Label("propertyResourceDone");
            e.B(0x48, 0x8d, 0x8b); I(320); Call(0x273cd0);
            // Reset the released path before the next property's path is built.
            e.B(0x48, 0xc7, 0x83); I(328); I(0); e.B(0x48, 0xc7, 0x83); I(336); I(0);
            e.B(0x48, 0x8b, 0x83); I(552); e.B(0x48, 0x85, 0xc0); e.J(0x84, "propertySupplierDone");
            e.B(0x48, 0x8d, 0x48, 8); Call(0x1e370e0);
            e.B(0x48, 0x8b, 0x10); Copy("propertySupplierText", 2568, 240);
            e.Label("propertySupplierDone");
            e.B(0xff, 0x83); I(256); e.J(0x84, "targetNext", true);
        }
        else if (command.Action == "components")
        {
            e.B(0x48, 0x8b, 0x8b); I(280); Call(0x1e69fe0);
            e.B(0xff, 0x83); I(256);
        }
        else if (command.Action == "objects")
        {
            e.B(0x48, 0x8b, 0x83); I(488); e.B(0x48, 0x8b, 0x00, 0x48, 0x8b, 0x04, 0xd0);
            e.B(0xff, 0x83); I(256);
        }
        else { Api(0xb0); e.B(0xff, 0x83); I(256); }
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "targetLoop");
        e.B(0x48, 0x89, 0x43, 104, 0x4c, 0x8b, 0x93); I(264);
        e.B(0x49, 0x89, 0x02);
        if (command.Action == "query")
        {
        e.B(0x8b, 0x93); I(256); e.B(0xff, 0xca, 0x41, 0x89, 0x92); I(1536);
        e.B(0x48, 0x8b, 0x50, 0x28, 0x49, 0x89, 0x52, 8,
            0x48, 0xc1, 0xea, 61, 0x41, 0x89, 0x52, 20,
            0x8b, 0x50, 20, 0x41, 0x89, 0x52, 16);
        e.B(0x48, 0x89, 0xc1); Call(0x1e6a060);
        e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x89, 0x42, 24);
        e.B(0x48, 0x8b, 0x4b, 120, 0x41, 0x8b, 0x52, 20, 0x45, 0x8b, 0x42, 16); Call(0x1a74310);
        e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x41, 0x88, 0x42, 28);
        }
        else { e.B(0x8b, 0x83); I(256); e.B(0xff, 0xc8, 0x41, 0x89, 0x42, 16); }
        e.B(0x48, 0x8b, 0x4b, 104, 0x48, 0x83, 0xc1, 8); Call(0x1e370e0);
        e.B(0x48, 0x8b, 0x10); Copy("targetName", 32, 240);
        e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x8b, 0x40, 24, 0x48, 0x8b, 0x50, 32);
        Copy("targetClass", 272, 240);
        if (command.Action == "query")
        {
            e.B(0x48, 0x8b, 0x43, 104, 0x48, 0x8b, 0x48, 32, 0x48, 0x85, 0xc9); e.J(0x84, "targetNext");
            e.B(0x48, 0x83, 0xc1, 8); Call(0x1e370e0); e.B(0x48, 0x8b, 0x10); Copy("targetPrefab", 512, 1024);
        }
        e.Label("targetNext");
        e.B(0xff, 0x43, 116, 0x48, 0x81, 0x83); I(264); I(TargetRowSize);
        e.J(0x84, "targetLoop", true);
        e.Label("targetDone"); e.B(0xc7, 0x43, 68, 1, 0, 0, 0);
        e.J(0x84, "entityDone", true);
        foreach (var (label, status) in new[] { ("targetChanged", 15), ("targetInherited", 16), ("propertyMissing", 17), ("valueChanged", 18), ("targetPrefabMode", 21), ("targetPathMissing", 22), ("arrayStorageChanged", 23), ("inheritedMemberRemoval", 24) })
        { e.Label(label); e.B(0xc7, 0x43, 72); I(status); e.J(0x84, "return", true); }

        void BuildContainerPath()
        {
                // Keep the world entity as root. The owning editor resolves the
                // component path and creates an instance override when inherited.
                e.B(0x48, 0x8b, 0x83); I(304); e.B(0x48, 0x89, 0x83); I(320);
                if (command.Component >= 0)
                {
                    e.B(0x48, 0x8d, 0x8b); I(320); Call(0x1ad60d0);
                    e.B(0x48, 0x89, 0xc1, 0x48, 0xba); e.Image(image, 0x3ca125c); e.B(0x8b, 0x12);
                    e.B(0x4c, 0x8b, 0x83); I(408);
                    e.B(0x4d, 0x8b, 0x48, 24, 0x4d, 0x8b, 0x49, 32, 0x49, 0x83, 0xc0, 8);
                    Call(0x1ad7760);
                }
                for (var step = 0; step < (command.Path?.Length ?? 0); step++)
                {
                    e.B(0x48, 0x8d, 0x8b); I(320); Call(0x1ad60d0);
                    e.B(0x48, 0x89, 0xc1, 0x8b, 0x93); I(TargetInputOffset + 2048 + step * 4);
                    e.B(0x4c, 0x8b, 0x83); I(416 + step * 8);
                    e.B(0x4d, 0x8b, 0x48, 24, 0x4d, 0x8b, 0x49, 32, 0x49, 0x83, 0xc0, 8); Call(0x1ad7760);
                }
        }

        void CheckChildOwnership(string label, int parent, int child, int propertyIndex, bool array)
        {
            e.B(0x80, 0xbb); I(400); e.B(0); e.J(0x85, label + "Done");
            e.B(0x48, 0x8b, 0x8b); I(parent); e.B(0x8b, 0x93); I(propertyIndex);
            e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, label + "Inherited");
            e.B(0x48, 0x8b, 0x8b); I(parent); e.B(0x48, 0x8b, 0x49, 32);
            e.B(0x48, 0x85, 0xc9); e.J(0x84, label + "Done");
            e.B(0x8b, 0x93); I(propertyIndex); e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, label + "Done");
            if (array)
            {
                e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x93); I(child);
                e.B(0x48, 0x83, 0xc2, 8, 0x45, 0x31, 0xc0); Call(0x1a93450);
            }
            else e.B(0x48, 0x8b, 0x00);
            e.B(0x48, 0x3b, 0x83); I(child); e.J(0x85, label + "Done");
            e.Label(label + "Inherited"); e.B(0xc6, 0x83); I(400); e.B(1);
            e.Label(label + "Done");
        }

        void ResolveArray()
        {
            e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x48, 0x8b, 0x49, 24, 0x48, 0x8d, 0x93); I(768); Call(0x1e39810);
            e.B(0x83, 0xf8, 0xff); e.J(0x84, "propertyMissing"); e.B(0x89, 0x83); I(296);
            e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x48, 0x8b, 0x51, 24, 0x48, 0x8b, 0x52, 40,
                0x48, 0x6b, 0xc0, 0x58, 0x81, 0x3c, 0x02); I(141); e.J(0x85, "targetPathMissing");
            e.B(0x8b, 0x93); I(296); e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
            e.B(0x48, 0x85, 0xc0); e.J(0x85, "arrayStorage");
            e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x48, 0x8b, 0x49, 24, 0x48, 0x8b, 0x49, 40, 0x8b, 0x93); I(296);
            e.B(0x48, 0x6b, 0xd2, 0x58, 0x48, 0x8b, 0x44, 0x11, 16);
            e.Label("arrayStorage"); e.B(0x48, 0x89, 0x83); I(488);
            e.B(0xc7, 0x43, 112, 0, 0, 0, 0);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "arrayCountReady");
            e.B(0x8b, 0x40, 12, 0x89, 0x43, 112);
            e.Label("arrayCountReady");
            if (mutation)
            {
                e.B(0x81, 0x7b, 112); I(command.ExpectedArrayCount); e.J(0x85, "valueChanged");
                if (command.ArrayIndex < command.ExpectedArrayCount)
                {
                    e.B(0x48, 0x8b, 0x83); I(488); e.B(0x48, 0x8b, 0x00, 0x48, 0x8b, 0x80); I(checked(command.ArrayIndex * 8));
                    e.B(0x48, 0xba); e.Q(command.ExpectedMember); e.B(0x48, 0x39, 0xd0); e.J(0x85, "targetChanged");
                }
            }
            e.B(0x48, 0x8b, 0x83); I(280);
        }

        void EmitMutation()
        {
            if (command.Action == "remove_object")
            {
                e.B(0x48, 0xb8); e.Q(command.ExpectedMember); e.B(0x48, 0x89, 0x83); I(512);
                CheckChildOwnership("removeOwnership", 280, 512, 296, true);
                e.B(0x80, 0xbb); I(400); e.B(0); e.J(0x85, "inheritedMemberRemoval");
                // A local override of an ancestor member still cannot be deleted
                // persistently by the engine's remove command: it reappears on load.
                e.B(0x48, 0x8b, 0x83); I(512); e.B(0x48, 0x83, 0x78, 32, 0); e.J(0x85, "inheritedMemberRemoval");
                e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x8b, 0x93); I(296);
                e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
                e.B(0x48, 0x85, 0xc0); e.J(0x84, "arrayStorageChanged");
                e.B(0x81, 0x78, 12); I(command.ExpectedArrayCount); e.J(0x85, "arrayStorageChanged");
            }
            if (command.Action is "set" or "clear")
            {
                e.B(0x48, 0x8b, 0x48, 24, 0x48, 0x8d, 0x93); I(768); Call(0x1e39810);
                e.B(0x83, 0xf8, 0xff); e.J(0x84, "propertyMissing"); e.B(0x89, 0x83); I(296);
                e.B(0x89, 0xc2, 0x48, 0x8b, 0x8b); I(280); e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x130);
                e.B(0x80, 0xbb); I(400); e.B(0); e.J(0x84, "compareLocalContainer");
                e.B(0x31, 0xc0); e.Label("compareLocalContainer");
                e.B(0x48, 0x85, 0xc0, 0x0f, 0x95, 0xc0, 0x3c, command.ExpectedLocalOverride ? (byte)1 : (byte)0); e.J(0x85, "valueChanged");
                if (command.ExpectedValueArrayCount >= 0)
                {
                    // Comma serialization alone cannot distinguish ["a", "b"]
                    // from an externally authored ["a,b"]. Recheck cardinality
                    // in this same callback before the whole-property setter.
                    e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x8b, 0x93); I(296);
                    e.B(0x48, 0x8b, 0x01, 0xff, 0x90); I(0x128);
                    e.B(0x48, 0x85, 0xc0); e.J(0x85, "compareArrayStorage");
                    e.B(0x48, 0x8b, 0x8b); I(280); e.B(0x48, 0x8b, 0x49, 24, 0x48, 0x8b, 0x49, 40, 0x8b, 0x93); I(296);
                    e.B(0x48, 0x6b, 0xd2, 0x58, 0x48, 0x8b, 0x44, 0x11, 16);
                    e.Label("compareArrayStorage");
                    e.B(0x31, 0xd2, 0x48, 0x85, 0xc0); e.J(0x84, "compareArrayCount");
                    e.B(0x8b, 0x50, 12);
                    e.Label("compareArrayCount"); e.B(0x81, 0xfa); I(command.ExpectedValueArrayCount); e.J(0x85, "valueChanged");
                }
                // Compare the serialized observation inside the same callback before editing.
                e.B(0x4c, 0x8b, 0x83); I(280); e.B(0x49, 0x8b, 0x48, 24, 0x8b, 0x93); I(296);
                e.B(0x4c, 0x8d, 0x8b); I(288); e.B(0x48, 0x8b, 0x01, 0xff, 0x50, 0x58);
                e.B(0x48, 0x8b, 0x93); I(288); e.B(0x48, 0x85, 0xd2); e.J(0x85, "compareValue");
                e.B(0x48, 0x8d, 0x93); I(360); // zero byte
                e.Label("compareValue"); e.B(0x4c, 0x8d, 0x8b); I(4096);
                e.Label("compareValueLoop"); e.B(0x8a, 0x02, 0x41, 0x3a, 0x01); e.J(0x85, "compareValueMismatch");
                e.B(0x84, 0xc0); e.J(0x84, "compareValueFree"); e.B(0x48, 0xff, 0xc2, 0x49, 0xff, 0xc1); e.J(0x84, "compareValueLoop", true);
                e.Label("compareValueMismatch"); e.B(0xc7, 0x83); I(364); I(1);
                e.Label("compareValueFree");
                e.B(0x48, 0x8b, 0x8b); I(288); e.B(0x48, 0x85, 0xc9); e.J(0x84, "compareValueFreed");
                e.B(0xf0, 0xff, 0x49, 0xfc); e.J(0x85, "compareValueFreed"); e.B(0x48, 0x83, 0xe9, 12, 0x31, 0xd2); Call(0x1e32f10);
                e.Label("compareValueFreed"); e.B(0x83, 0xbb); I(364); e.B(0); e.J(0x85, "valueChanged");
            }
            e.B(0x48, 0x8d, 0x93); I(768); e.B(0x4c, 0x8d, 0x83); I(360); e.B(0x45, 0x31, 0xc9); Api(0x38);
            e.B(0x84, 0xc0); e.J(0x84, "entityActionRejected");
            if (command.Action is "set" or "clear" or "insert_object" or "remove_object")
            {
                BuildContainerPath();
                e.B(0x48, 0x8d, 0x93); I(320); e.B(0x44, 0x8b, 0x83); I(296);
                if (command.Action == "set") { e.B(0x4c, 0x8d, 0x8b); I(1024); }
                if (command.Action == "insert_object")
                {
                    e.B(0x4c, 0x8d, 0x8b); I(1024); e.B(0xc7, 0x44, 0x24, 0x20); I(command.ArrayIndex);
                }
                if (command.Action == "remove_object")
                {
                    // false selects RemoveObjectArrayVariableMemberCommand;
                    // true can replace a local member with its ancestor (reset).
                    e.B(0x41, 0xb9); I(command.ArrayIndex); e.B(0xc7, 0x44, 0x24, 0x20); I(0);
                }
                Api(command.Action switch { "set" => 0x318, "clear" => 0x478, "insert_object" => 0x320, _ => 0x328 });
                e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 68);
                e.Label("mutationPathRelease");
                e.B(0x48, 0x8d, 0x8b); I(320); Call(0x273cd0);
            }
            else
            {
                e.B(0x48, 0x8b, 0x93); I(304);
                if (command.Action == "add_component") { e.B(0x4c, 0x8d, 0x83); I(768); Api(0x148); e.B(0x48, 0x85, 0xc0, 0x0f, 0x95, 0xc0); }
                else { e.B(0x4c, 0x8b, 0x83); I(312); Api(0x150); }
                e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 68);
            }
            e.B(0x48, 0x8d, 0x93); I(768); Api(0x40); e.B(0x84, 0xc0); e.J(0x84, "entityActionIncomplete");
            e.B(0x83, 0x7b, 72, 0); e.J(0x85, "mutationDone");
            e.B(0xc7, 0x43, 72); I(20); e.B(0x83, 0x7b, 68, 1); e.J(0x85, "mutationDone");
            e.B(0xc7, 0x43, 72); I(19); e.Label("mutationDone");
        }

        void Copy(string label, int offset, int size)
        {
            e.B(0x4c, 0x8b, 0x93); I(264); e.B(0x4d, 0x8d, 0x8a); I(offset);
            e.B(0x48, 0x85, 0xd2); e.J(0x84, label + "Done");
            e.B(0x41, 0xb8); I(size - 1);
            e.Label(label + "Loop"); e.B(0x8a, 0x02, 0x41, 0x88, 0x01, 0x84, 0xc0); e.J(0x84, label + "Done");
            e.B(0x48, 0xff, 0xc2, 0x49, 0xff, 0xc1, 0x41, 0xff, 0xc8); e.J(0x85, label + "Loop");
            e.B(0x41, 0xc6, 0x42, 29, 1);
            e.Label(label + "Done");
        }
    }
}
