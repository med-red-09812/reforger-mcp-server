namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    // Workbench 1.8.0.13, GameApp.cpp:1287. This is the engine's per-site suppression
    // latch, not its global assertion switch. See docs/workbench-native-reload.md.
    private const int ResourceLeakSuppressionRva = 0x3edf9f0;

    private static void EmitSuppressResourceLeakAssertion(E code, nint imageBase)
    {
        // Runs on the owning Qt thread before lifecycle code can enter a modal loop.
        code.B(0x48, 0xb8); code.Image(imageBase, ResourceLeakSuppressionRva);
        code.B(0xc6, 0x00, 0x01); // mov byte ptr [rax],1
    }

    private static void EmitReadResourceLeakAssertion(E code, nint imageBase)
    {
        code.B(0x48, 0xb8); code.Image(imageBase, ResourceLeakSuppressionRva);
        code.B(0x0f, 0xb6, 0x00, 0x89, 0x43, 68); // result = byte ptr [rax]
    }
}
