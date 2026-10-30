namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    // EAX=reflected type ID, RDX=member descriptor; preserve RCX field storage.
    // 1.9's ProcessHandle index is script-registered, not a PE constant. Resolve
    // its owning module's canonical type and exact name before reading 64 bits.
    private static void EmitProcessHandleGuard(E e, string failure)
    {
        void I(int value) => e.B(BitConverter.GetBytes(value));
        if (!e.Profile.Experimental)
        {
            e.B(0x3d, 0xad, 1, 0, 0x20); e.J(0x85, failure);
            return;
        }
        e.B(0x41, 0x89, 0xc0, 0x41, 0x81, 0xe0); I(unchecked((int)0xf0000000));
        e.B(0x41, 0x81, 0xf8); I(0x20000000); e.J(0x85, failure);
        e.B(0x4c, 0x8b, 0x52, 0x18, 0x4d, 0x85, 0xd2); e.J(0x84, failure);
        e.B(0x41, 0x81, 0x7a, 0x5c); I(1_000_000); e.J(0x87, failure);
        e.B(0x41, 0x89, 0xc0, 0x41, 0x81, 0xe0); I(0x0fffffff);
        e.B(0x45, 0x3b, 0x42, 0x5c); e.J(0x83, failure);
        e.B(0x4d, 0x8b, 0x52, 0x50, 0x4d, 0x85, 0xd2); e.J(0x84, failure);
        e.B(0x4f, 0x8b, 0x14, 0xc2, 0x4d, 0x85, 0xd2); e.J(0x84, failure);
        e.B(0x41, 0x39, 0x42, 8); e.J(0x85, failure);
        e.B(0x4d, 0x8b, 0x52, 0x10, 0x4d, 0x85, 0xd2); e.J(0x84, failure);
        var name = "ProcessHandle\0"u8;
        for (var i = 0; i < name.Length; i++)
        {
            // Stop on the first mismatch, including a short terminated name.
            e.B(0x41, 0x80, 0x7a, (byte)i, name[i]); e.J(0x85, failure);
        }
    }
}
