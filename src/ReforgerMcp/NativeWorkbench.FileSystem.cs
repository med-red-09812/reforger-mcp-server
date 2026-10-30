namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    // RCX=filesystem, RDX=path, R8=raw destination, R9=method flag/mount.
    // 1.8 writes a raw buffer. 1.9 writes a SmallString with pointer, inline
    // storage at +0x14 and activation flag +0x12d; the owning interface slots
    // remain 0x140/0x148. Convert once here and release its engine ownership.
    private static void EmitFileSystemBuffer(E e, nint image, int slot, int capacity, string label)
    {
        if (slot is not (0x140 or 0x148) || capacity is < 1 or > 1024)
            throw new ArgumentException("Unsupported bounded filesystem text contract.");
        void I(int value) => e.B(BitConverter.GetBytes(value));
        if (!e.Profile.Experimental)
        {
            e.B(0xff, 0x90); I(slot);
            return;
        }
        const int text = 3584, result = 3904, destination = 3912;
        e.B(0x4c, 0x89, 0x83); I(destination); // Preserve caller's raw output.
        e.B(0x41, 0xc6, 0x00, 0); // Empty output on failure/bounds refusal.
        e.B(0x48, 0xc7, 0x83); I(text); I(0);
        e.B(0xc6, 0x83); I(text + 0x12d); e.B(0);
        e.B(0x4c, 0x8d, 0x83); I(text);
        e.B(0xff, 0x90); I(slot);
        e.B(0x88, 0x83); I(result);
        e.B(0x48, 0x8b, 0x93); I(text);
        e.B(0x48, 0x85, 0xd2); e.J(0x84, label + "Done");
        e.B(0x80, 0xbb); I(result); e.B(0); e.J(0x84, label + "Release");
        e.B(0x8b, 0x4a, 0xf8, 0x81, 0xf9); I(capacity);
        e.J(0x83, label + "TooLong");
        e.B(0x4c, 0x8b, 0x83); I(destination);
        e.B(0x45, 0x31, 0xc9);
        e.Label(label + "Copy");
        e.B(0x44, 0x39, 0xc9); e.J(0x84, label + "Terminated");
        e.B(0x42, 0x8a, 0x04, 0x0a, 0x43, 0x88, 0x04, 0x08, 0x41, 0xff, 0xc1);
        e.J(0x84, label + "Copy", true);
        e.Label(label + "Terminated");
        e.B(0x43, 0xc6, 0x04, 0x08, 0); e.J(0x84, label + "Release", true);
        e.Label(label + "TooLong");
        e.B(0xc6, 0x83); I(result); e.B(0);
        e.Label(label + "Release");
        e.B(0x48, 0x8b, 0x8b); I(text);
        e.B(0xf0, 0xff, 0x49, 0xfc); e.J(0x85, label + "Done");
        e.B(0x48, 0x8d, 0x83); I(text + 0x14);
        e.B(0x48, 0x39, 0xc1); e.J(0x84, label + "Done");
        e.B(0x48, 0x83, 0xe9, 12, 0x31, 0xd2, 0x48, 0xb8); e.Image(image, 0x1e32f10); e.B(0xff, 0xd0);
        e.Label(label + "Done");
        e.B(0x0f, 0xb6, 0x83); I(result);
    }
}
