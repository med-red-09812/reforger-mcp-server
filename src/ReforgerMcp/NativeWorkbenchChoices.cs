using System.Text;

namespace ReforgerMcp;

// Private, version-checked ABI. Choice metadata belongs to StringProperty; the
// combo is only its view. All borrowed pointers are used on the editor thread.
internal static partial class NativeWorkbench
{
    internal const string ChoicesPrefix = "rst-choices:1:";
    internal sealed record Choice(string Label, string Value, string Description = "");
    internal sealed record ChoiceControl(long Handle, string Provider, string Value);
    internal sealed record ChoicesResult(string State, int Updated, ChoiceControl[] Controls);
    internal sealed record ChoicesCommand(long Window, long Control = 0, string Provider = "", string Current = "", Choice[]? Items = null);
    private const int ChoicesCapacity = 64, ChoicesRowSize = 1536, ChoicesOutputOffset = 4096;
    private const int ChoicesInputOffset = ChoicesOutputOffset + ChoicesCapacity * ChoicesRowSize;
    private const int ChoiceEntrySize = 1536, ChoiceEntryOffset = 1536;
    private const uint ChoicesBufferSize = ChoicesInputOffset + ChoiceEntryOffset + 256 * ChoiceEntrySize;

    private static byte[] ChoicesInput(ChoicesCommand command)
    {
        var items = command.Items ?? [];
        if (items.Length > 256) throw new ArgumentException("Dynamic choices support at most 256 entries per field.");
        var bytes = new byte[ChoiceEntryOffset + items.Length * ChoiceEntrySize];
        void Put(int offset, int size, string value)
        {
            var encoded = Encoding.UTF8.GetBytes(value);
            if (value.Contains('\0') || encoded.Length >= size) throw new ArgumentException("Dynamic choice text exceeds its UTF-8 field bound or contains NUL.");
            encoded.CopyTo(bytes, offset);
        }
        Put(0, 256, ChoicesPrefix + command.Provider);
        Put(256, 1024, command.Current);
        for (var i = 0; i < items.Length; i++)
        {
            Put(ChoiceEntryOffset + i * ChoiceEntrySize, 256, items[i].Label);
            Put(ChoiceEntryOffset + i * ChoiceEntrySize + 256, 1024, items[i].Value);
            Put(ChoiceEntryOffset + i * ChoiceEntrySize + 1280, 256, items[i].Description);
        }
        return bytes;
    }

    private static ChoicesResult ReadChoices(byte[] result, byte[] rows)
    {
        if (BitConverter.ToInt32(result, 72) != 2 || BitConverter.ToInt32(result, 68) != 1)
            throw new InvalidOperationException("Native choice observation did not complete; automatic refresh stopped.");
        var count = BitConverter.ToInt32(result, 120);
        if (BitConverter.ToInt32(result, 124) != 0) throw new InvalidOperationException("More than 64 marked fields are visible; dynamic choices stopped without truncating the list.");
        if (count is < 0 or > ChoicesCapacity) throw new InvalidOperationException("Invalid native choice observation count.");
        var controls = new List<ChoiceControl>();
        string Read(int offset, int size)
        {
            var end = Array.IndexOf(rows, (byte)0, offset, size);
            if (end < 0) throw new InvalidOperationException("Native choice text exceeded the supported bound.");
            return Encoding.UTF8.GetString(rows, offset, end - offset);
        }
        for (var i = 0; i < count; i++)
        {
            int row = i * ChoicesRowSize;
            var marker = Read(row + 16, 256);
            if (!marker.StartsWith(ChoicesPrefix, StringComparison.Ordinal)) continue;
            controls.Add(new(BitConverter.ToInt64(rows, row), marker[ChoicesPrefix.Length..], Read(row + 272, 1024)));
        }
        return new("observed", BitConverter.ToInt32(result, 116), controls.ToArray());
    }

    private static void EmitChoices(E e, ChoicesCommand command, nint image, nint widgets, nint core)
    {
        void Call(nint address) { e.B(0x48, 0xb8); e.Q(address); e.B(0xff, 0xd0); }
        void ImportCall(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0x10); }
        void Load(int offset) { e.B(0x48, 0x8b, 0x8b); e.B(BitConverter.GetBytes(offset)); }
        void Save(int offset) { e.B(0x48, 0x89, 0x83); e.B(BitConverter.GetBytes(offset)); }
        void Address(int offset) { e.B(0x48, 0x8d, 0x8b); e.B(BitConverter.GetBytes(offset)); }
        // rdx=source, r9=destination. Unterminated strings are reported, not truncated silently.
        void Copy(string label, int bound)
        {
            e.B(0x48, 0x85, 0xd2); e.J(0x84, label + "Done");
            e.B(0x41, 0xb8); e.B(BitConverter.GetBytes(bound));
            e.Label(label); e.B(0x8a, 0x02, 0x41, 0x88, 0x01, 0x84, 0xc0); e.J(0x84, label + "Done");
            e.B(0x48, 0xff, 0xc2, 0x49, 0xff, 0xc1, 0x41, 0xff, 0xc8); e.J(0x85, label);
            e.Label(label + "Done");
        }
        // rcx and rdx point to NUL-terminated input/property strings.
        void Equal(string label, string different)
        {
            e.B(0x48, 0x85, 0xd2); e.J(0x85, label);
            e.B(0x80, 0x39, 0); e.J(0x85, different); e.J(0x84, label + "Done", true);
            e.Label(label); e.B(0x8a, 0x01, 0x3a, 0x02); e.J(0x85, different);
            e.B(0x84, 0xc0); e.J(0x84, label + "Done");
            e.B(0x48, 0xff, 0xc1, 0x48, 0xff, 0xc2); e.J(0x84, label, true); e.Label(label + "Done");
        }
        // Execute uses +128..+167 for the OS unwind-registration helper before
        // entering this callback. Construct an empty QList and cursor explicitly.
        e.B(0x31, 0xc0);
        foreach (var offset in new[] { 128, 136, 144, 160 }) Save(offset);
        e.B(0x48, 0xb9); e.Q(command.Window); Call(widgets + 0x577a0); // QWidget::find
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "choicesDone");
        e.B(0x48, 0x89, 0xc1, 0x48, 0xba); e.Q(widgets + 0x487cf0); // QComboBox meta-object
        e.B(0x4c, 0x8d, 0x83, 128, 0, 0, 0, 0x41, 0xb9, 1, 0, 0, 0);
        Call(core + 0x22910); // qt_qFindChildren_helper; QList<void*> at +128
        e.Label("choicesLoop");
        Load(160); e.B(0x48, 0x3b, 0x8b, 144, 0, 0, 0); e.J(0x83, "choicesFree");
        e.B(0x48, 0x8b, 0x83, 136, 0, 0, 0, 0x48, 0x8b, 0x04, 0xc8); Save(192);
        e.B(0x48, 0xba); e.Image(image, 0x2d87408);
        e.B(0x48, 0x39, 0x10); e.J(0x85, "choicesNext");
        if (command.Control != 0)
        {
            e.B(0x48, 0xba); e.Q(command.Control); e.B(0x48, 0x39, 0xd0); e.J(0x85, "choicesNext");
        }
        // PropComboBox's QWeakPointer must still own a live StringProperty.
        e.B(0x48, 0x8b, 0x50, 0x28, 0x48, 0x85, 0xd2); e.J(0x84, "choicesNext");
        e.B(0x83, 0x7a, 4, 0); e.J(0x84, "choicesNext");
        e.B(0x48, 0x8b, 0x50, 0x30, 0x48, 0xb9); e.Image(image, 0x2d95c90); Call(core + 0x38f0);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "choicesNext"); Save(184);
        e.B(0x83, 0xb8, 4, 1, 0, 0, 0); e.J(0x8e, "choicesNext");
        e.B(0x48, 0x8b, 0x80, 248, 0, 0, 0); Save(208);
        if (command.Items is null)
        {
            Load(208); e.B(0x48, 0x8b, 0x51, 8, 0x48, 0x85, 0xd2); e.J(0x84, "choicesNext");
            for (var i = 0; i < ChoicesPrefix.Length; i++)
            {
                e.B(0x80, 0x7a, (byte)i, (byte)ChoicesPrefix[i]); e.J(0x85, "choicesNext");
            }
            e.B(0x8b, 0x43, 120, 0x83, 0xf8, ChoicesCapacity); e.J(0x83, "choicesOverflow");
            e.B(0x48, 0x69, 0xc0); e.B(BitConverter.GetBytes(ChoicesRowSize));
            e.B(0x48, 0x8d, 0x84, 0x03); e.B(BitConverter.GetBytes(ChoicesOutputOffset)); Save(200);
            Load(192); e.B(0x48, 0x89, 0x08);
            Load(208); e.B(0x48, 0x8b, 0x51, 8, 0x4c, 0x8d, 0x48, 16); Copy("choicesMarker", 256);
            Load(184); e.B(0x48, 0x8b, 0x91, 24, 1, 0, 0);
            Load(200); e.B(0x4c, 0x8d, 0x89, 16, 1, 0, 0); Copy("choicesCurrent", 1024);
            e.B(0xff, 0x43, 120);
        }
        else
        {
            var items = command.Items;
            // EditComboBox owns normal text commit/undo. Do not rebuild its
            // choices while the user is typing, even if another source changes.
            Load(192); ImportCall(0x2b59118); // QComboBox::lineEdit
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "choicesNotEditing");
            e.B(0x48, 0x89, 0xc1); ImportCall(0x2b58310); // QWidget::hasFocus
            e.B(0x84, 0xc0); e.J(0x85, "choicesNext");
            e.Label("choicesNotEditing");
            Load(208); e.B(0x48, 0x8b, 0x51, 8); Address(ChoicesInputOffset); Equal("choicesProviderEqual", "choicesNext");
            Load(184); e.B(0x48, 0x8b, 0x91, 24, 1, 0, 0); Address(ChoicesInputOffset + 256); Equal("choicesCurrentEqual", "choicesNext");
            Load(184); e.B(0x81, 0xb9, 4, 1, 0, 0); e.B(BitConverter.GetBytes(items.Length)); e.J(0x85, "choicesReplace");
            // Compare full metadata before emitting signals, so polling never disturbs an unchanged popup.
            e.B(0x48, 0xc7, 0x83, 216, 0, 0, 0, 0, 0, 0, 0);
            e.Label("choicesCompare"); Load(216); e.B(0x48, 0x81, 0xf9); e.B(BitConverter.GetBytes(items.Length)); e.J(0x83, "choicesPresentation");
            e.B(0x48, 0x6b, 0xc1, 24, 0x48, 0x03, 0x83, 208, 0, 0, 0); Save(224);
            e.B(0x48, 0x69, 0xc1); e.B(BitConverter.GetBytes(ChoiceEntrySize));
            e.B(0x48, 0x8d, 0x84, 0x03); e.B(BitConverter.GetBytes(ChoicesInputOffset + ChoiceEntryOffset)); Save(232);
            foreach (var (input, native, label) in new[] { (0, 0, "Label"), (256, 16, "Value"), (1280, 8, "Description") })
            {
                Load(224); e.B(0x48, 0x8b, 0x51, (byte)native); Load(232);
                e.B(0x48, 0x81, 0xc1); e.B(BitConverter.GetBytes(input)); Equal("choices" + label + "Equal", "choicesReplace");
            }
            e.B(0x48, 0xff, 0x83, 216, 0, 0, 0); e.J(0x84, "choicesCompare", true);
            e.Label("choicesReplace");
            Load(192); e.B(0xba, 1, 0, 0, 0); Call(core + 0xe5400); e.B(0x88, 0x83, 240, 0, 0, 0);
            Load(184); e.B(0x48, 0x81, 0xc1, 208, 0, 0, 0); Call((nint)e.Profile.Address(image, 0x47a630));
            e.B(0x48, 0xc7, 0x83, 216, 0, 0, 0, 0, 0, 0, 0);
            e.Label("choicesAdd"); Load(216); e.B(0x48, 0x81, 0xf9); e.B(BitConverter.GetBytes(items.Length)); e.J(0x83, "choicesRestore");
            e.B(0x48, 0x69, 0xc1); e.B(BitConverter.GetBytes(ChoiceEntrySize));
            e.B(0x48, 0x8d, 0x94, 0x03); e.B(BitConverter.GetBytes(ChoicesInputOffset + ChoiceEntryOffset));
            e.B(0x4c, 0x8d, 0x82, 0, 1, 0, 0, 0x4c, 0x8d, 0x8a, 0, 5, 0, 0);
            Load(184); e.B(0x48, 0x81, 0xc1, 208, 0, 0, 0); Call((nint)e.Profile.Address(image, 0x478730));
            e.B(0x48, 0xff, 0x83, 216, 0, 0, 0); e.J(0x84, "choicesAdd", true);
            e.Label("choicesRestore");
            Load(192); e.B(0xba); e.B(BitConverter.GetBytes(Array.FindIndex(items, i => i.Value == command.Current))); Call(widgets + 0x141ba0);
            Load(192); e.B(0x0f, 0xb6, 0x93, 240, 0, 0, 0); Call(core + 0xe5400);
            e.B(0xff, 0x43, 116);
            e.Label("choicesPresentation");
            // Provider routing is metadata, never a user-facing tooltip.
            Address(256); Call(core + 0x1ea0); // empty QVariant
            Load(192); e.B(0x31, 0xd2, 0x4c, 0x8d, 0x83, 0, 1, 0, 0, 0x41, 0xb9, 3, 0, 0, 0);
            Call(widgets + 0x142570); // clear empty item's ToolTipRole only
            Address(256); Call(core + 0x102480);
            if (command.Current.Length == 0)
            {
                Load(192); e.B(0x48, 0x8d, 0x93, 32, 1, 0, 0); Call(widgets + 0x6c370); // empty QString
            }
        }
        e.Label("choicesNext"); e.B(0x48, 0xff, 0x83, 160, 0, 0, 0); e.J(0x84, "choicesLoop", true);
        e.Label("choicesOverflow"); e.B(0xc7, 0x43, 124, 1, 0, 0, 0);
        e.Label("choicesFree");
        // This fresh QList has no copies and contains non-owning QObject pointers.
        Load(128); e.B(0x48, 0x85, 0xc9); e.J(0x84, "choicesDone");
        e.B(0xba, 8, 0, 0, 0, 0x41, 0xb8, 8, 0, 0, 0); Call(core + 0x1a7e10);
        e.Label("choicesDone"); e.B(0xc7, 0x43, 68, 1, 0, 0, 0);
    }
}
