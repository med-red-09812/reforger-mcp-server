namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    // Workbench 1.8.0.13 / Qt 6.8.3. Resolve the observed row through Qt,
    // then invoke ArrayProperty's owning add handler (including subtype overrides).
    // No array memory writes, simulated button click, or plugin-specific behavior.
    private static void EmitArrayAdd(E e, nint image, nint widgets)
    {
        void Call(nint address) { e.B(0x48, 0xb8); e.Q(address); e.B(0xff, 0xd0); }
        void Cast(int source, int target)
        {
            e.B(0x31, 0xd2, 0x49, 0xb8); e.Image(image, source);
            e.B(0x49, 0xb9); e.Image(image, target);
            e.B(0xc7, 0x44, 0x24, 0x20, 0, 0, 0, 0); // pointer cast: return null on mismatch
            Call((nint)e.Profile.Address(image, 0x2a68079)); // __RTDynamicCast
            e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
            e.B(0x48, 0x89, 0xc1);
        }

        // RCX is the accessible cell's live view. The copied QModelIndex is +288.
        Cast(0x3d15228, 0x3d16380); // QWidget -> QTreeWidget
        e.B(0x48, 0x8d, 0x93, 0x20, 1, 0, 0);
        Call(widgets + 0x34c240); // QTreeWidget::itemFromIndex
        e.B(0x48, 0x85, 0xc0); e.J(0x84, "uiDone");
        e.B(0x48, 0x89, 0xc1);
        Cast(0x3d155f0, 0x3ccb080); // QTreeWidgetItem -> ArrayProperty; adjusts multiple inheritance
        e.B(0x48, 0x89, 0x43, 112);

        // Respect the native '+' enabled state, including parent restrictions.
        e.B(0x48, 0x8b, 0x88, 0xa8, 0, 0, 0, 0x48, 0x85, 0xc9); e.J(0x84, "uiDone");
        e.B(0x48, 0xba); e.Image(image, 0x2d89d98); // ArrayEditWidget
        e.B(0x48, 0x39, 0x11); e.J(0x85, "uiDone");
        e.B(0x48, 0x8b, 0x49, 0x30, 0x48, 0x85, 0xc9); e.J(0x84, "uiDone");
        e.B(0x31, 0xd2); Call(widgets + 0x5c820); // QWidget::isEnabledTo(nullptr)
        e.B(0x84, 0xc0); e.J(0x84, "uiDone");

        e.B(0x48, 0x8b, 0x4b, 112, 0x48, 0x8b, 0x01, 0xff, 0x90, 0x98, 2, 0, 0);
        // The normal handler can open a type picker or cancel creation. EmitUi
        // reports dispatch only; callers must inspect the resulting list.
    }
}
