using System.Text;

namespace ReforgerMcp;

internal sealed record NativeProjectAddon(string Guid, string Id, string Title, string ProjectFile,
    string MountedRoot, bool MountResolved, bool Loaded, bool Current);
internal sealed record NativeProjectContext(NativeProjectAddon[] Addons);

internal static partial class NativeWorkbench
{
    private const int ProjectMaxAddons = 1024, ProjectRowSize = 4096;
    private const int ProjectBufferSize = 4096 + 2 * ProjectMaxAddons * ProjectRowSize;

    private static NativeProjectContext ReadProjectContext(byte[] header, byte[] rows)
    {
        if (BitConverter.ToInt32(header, 72) != 2)
            throw new InvalidOperationException("Project context unavailable or exceeds native observation bounds; no partial context returned.");
        int loaded = BitConverter.ToInt32(header, 104), available = BitConverter.ToInt32(header, 108), current = BitConverter.ToInt32(header, 112);
        if (loaded is < 1 or > ProjectMaxAddons || available is < 0 or > ProjectMaxAddons || current < 0 || current >= loaded)
            throw new InvalidOperationException("Invalid native project counts.");
        string Text(int offset, int size)
        {
            int count = Array.IndexOf(rows, (byte)0, offset, size) - offset;
            if (count < 0) throw new InvalidOperationException("Unterminated native project text.");
            return PakArchive.Utf8.GetString(rows, offset, count);
        }
        return new(Enumerable.Range(0, loaded + available).Select(i =>
        {
            int o = i * ProjectRowSize;
            return new NativeProjectAddon(Text(o, 64), Text(o + 64, 256), Text(o + 320, 512), Text(o + 832, 1024),
                Text(o + 2048, 1024), rows[o + 3328] != 0, i < loaded, i == current);
        }).ToArray());
    }

    // Same project owner/arrays as GameProject.GetLoadedAddons/GetAvailableAddons.
    // Copy on the editor thread: no engine pointers escape this callback.
    private static void EmitProjectContext(E e, nint image)
    {
        void I(int n) => e.B(BitConverter.GetBytes(n));
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        Call(0x1ea3f00);
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "projectInvalid");
        e.B(0x48, 0x89, 0x43, 96);
        e.B(0x8b, 0x88); I(0xd4); e.B(0x89, 0x4b, 104, 0x85, 0xc9); e.J(0x8e, "projectInvalid");
        e.B(0x81, 0xf9); I(ProjectMaxAddons); e.J(0x87, "projectInvalid");
        e.B(0x8b, 0x90); I(0xe4); e.B(0x89, 0x53, 108, 0x81, 0xfa); I(ProjectMaxAddons); e.J(0x87, "projectInvalid");
        e.B(0x8b, 0x90); I(0x120); e.B(0x89, 0x53, 112, 0x39, 0xca); e.J(0x83, "projectInvalid");
        // Row output pointer, retained across native filesystem calls.
        e.B(0x48, 0x8d, 0x83); I(4096); e.B(0x48, 0x89, 0x83); I(256);
        foreach (var (label, arrayOffset, countOffset) in new[] { ("loaded", 0xc8, 104), ("available", 0xd8, 108) })
        {
            e.B(0xc7, 0x83); I(264); I(0);
            e.Label(label + "Loop");
            e.B(0x8b, 0x8b); I(264); e.B(0x3b, 0x4b, (byte)countOffset); e.J(0x83, label + "End");
            e.B(0x48, 0x8b, 0x43, 96, 0x48, 0x8b, 0x80); I(arrayOffset);
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "projectInvalid");
            e.B(0x48, 0x8b, 0x04, 0xc8, 0x48, 0x85, 0xc0); e.J(0x84, "projectInvalid");
            e.B(0x48, 0x89, 0x83); I(272);
            foreach (var (member, offset, size) in new[] { (0x28, 0, 64), (0x20, 64, 256), (0x30, 320, 512), (0x38, 832, 1024) })
            {
                var key = label + offset;
                e.B(0x48, 0x8b, 0x83); I(272); e.B(0x48, 0x8b, 0x50, (byte)member);
                e.B(0x48, 0x85, 0xd2); e.J(0x84, key + "End");
                e.B(0x8b, 0x4a, 0xf8, 0x81, 0xf9); I(size); e.J(0x83, "projectInvalid");
                e.B(0x4c, 0x8b, 0x83); I(256); e.B(0x49, 0x81, 0xc0); I(offset);
                e.B(0x45, 0x31, 0xc9);
                e.Label(key + "Copy"); e.B(0x44, 0x39, 0xc9); e.J(0x84, key + "End");
                e.B(0x42, 0x8a, 0x04, 0x0a, 0x43, 0x88, 0x04, 0x08, 0x41, 0xff, 0xc1); e.J(0x84, key + "Copy", true);
                e.Label(key + "End");
            }
            // Build $ID: and resolve through the same filesystem owner as GetAbsolutePath.
            e.B(0x4c, 0x8b, 0x83); I(256); e.B(0x41, 0xc6, 0x80); I(3072); e.B(0x24, 0x31, 0xc9);
            e.Label(label + "Mount");
            e.B(0x41, 0x8a, 0x44, 0x08, 64, 0x84, 0xc0); e.J(0x84, label + "MountEnd");
            e.B(0x41, 0x88, 0x84, 0x08); I(3073); e.B(0xff, 0xc1); e.J(0x84, label + "Mount", true);
            e.Label(label + "MountEnd"); e.B(0x41, 0xc6, 0x84, 0x08); I(3073); e.B(0x3a);
            Call(0x1e90630);
            e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8b, 0x93); I(256);
            e.B(0x4c, 0x8d, 0x82); I(2048); e.B(0x48, 0x81, 0xc2); I(3072);
            e.B(0x45, 0x31, 0xc9, 0xff, 0x90); I(0x140);
            e.B(0x48, 0x8b, 0x93); I(256); e.B(0x88, 0x82); I(3328);
            e.B(0x48, 0x81, 0x83); I(256); I(ProjectRowSize);
            e.B(0xff, 0x83); I(264); e.J(0x84, label + "Loop", true);
            e.Label(label + "End");
        }
        e.B(0xc7, 0x43, 72, 2, 0, 0, 0); e.J(0x84, "return", true);
        e.Label("projectInvalid"); e.B(0xc7, 0x43, 72, 28, 0, 0, 0); e.J(0x84, "return", true);
    }
}
