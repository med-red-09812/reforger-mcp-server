using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ReforgerMcp;

// Execute the production thunk in this test process against a controlled native
// filesystem. This exercises actual copying/ownership and deliberate overflow;
// it never opens or injects Workbench.
internal static class FileSystemBufferTests
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte FileSystem(nint owner, nint path, nint output, int flag);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void Free(nint header, int flags);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate byte Thunk(nint data);
    [DllImport("kernel32", SetLastError = true)] private static extern nint VirtualAlloc(nint address, nuint size, uint type, uint protection);
    [DllImport("kernel32", SetLastError = true)] private static extern bool VirtualProtect(nint address, nuint size, uint protection, out uint old);
    [DllImport("kernel32")] private static extern bool FlushInstructionCache(nint process, nint address, nuint size);
    [DllImport("kernel32")] private static extern bool VirtualFree(nint address, nuint size, uint type);

    internal static int Run()
    {
        var checks = 0;
        foreach (var slot in new[] { 0x140, 0x148 })
        foreach (var scenario in new[] { "inline", "heap", "oversized", "empty", "native_failure" })
        {
            const int capacity = 16;
            var data = Marshal.AllocHGlobal(4096);
            var table = Marshal.AllocHGlobal(0x160);
            var owner = Marshal.AllocHGlobal(8);
            nint code = 0, allocation = 0;
            var released = 0;
            var initialized = false;
            var text = scenario == "oversized" ? new string('x', capacity) : scenario == "empty" ? "" : "verified";
            FileSystem filesystem = (_, _, output, flag) =>
            {
                initialized = Marshal.ReadIntPtr(output) == 0 && Marshal.ReadByte(output + 0x12d) == 0 && flag == (slot == 0x148 ? -1 : 0);
                if (scenario == "empty") return 1;
                var bytes = Encoding.UTF8.GetBytes(text + "\0");
                var inline = scenario == "inline";
                var header = inline ? output + 8 : allocation = Marshal.AllocHGlobal(bytes.Length + 12);
                Marshal.WriteInt32(header, bytes.Length);
                Marshal.WriteInt32(header + 4, bytes.Length - 1);
                Marshal.WriteInt32(header + 8, 1);
                Marshal.Copy(bytes, 0, header + 12, bytes.Length);
                Marshal.WriteIntPtr(output, header + 12);
                if (inline) Marshal.WriteByte(output + 0x12d, 1);
                return scenario == "native_failure" ? (byte)0 : (byte)1;
            };
            Free free = (header, _) =>
            {
                if (header == allocation && allocation != 0)
                {
                    Marshal.FreeHGlobal(allocation); allocation = 0; released++;
                }
                else released = -100;
            };
            try
            {
                Marshal.Copy(new byte[4096], 0, data, 4096);
                Marshal.WriteByte(data + 512 + capacity, 0x5a);
                Marshal.WriteIntPtr(table + slot, Marshal.GetFunctionPointerForDelegate(filesystem));
                Marshal.WriteIntPtr(owner, table);
                var profile = NativeWorkbenchProfile.Experimental19;
                var type = typeof(NativeWorkbench).GetNestedType("E", BindingFlags.NonPublic)!;
                var emitter = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [profile], null)!;
                void B(params byte[] bytes) => type.GetMethod("B")!.Invoke(emitter, [bytes]);
                void Q(long value) => type.GetMethod("Q")!.Invoke(emitter, [value]);
                B(0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x89, 0xcb); // owning data, aligned Win64 call frame
                B(0x48, 0xb9); Q(owner);
                B(0x48, 0x8b, 0x01, 0x48, 0x8d, 0x93, 0x58, 2, 0, 0, 0x4c, 0x8d, 0x83, 0, 2, 0, 0, 0x41, 0xb9);
                B(BitConverter.GetBytes(slot == 0x148 ? -1 : 0));
                var image = Marshal.GetFunctionPointerForDelegate(free) - profile.Rva(0x1e32f10);
                typeof(NativeWorkbench).GetMethod("EmitFileSystemBuffer", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [emitter, image, slot, capacity, "testFs"]);
                B(0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3);
                var bytes = (byte[])type.GetMethod("Done")!.Invoke(emitter, null)!;
                code = VirtualAlloc(0, (nuint)bytes.Length, 0x3000, 4);
                if (code == 0) throw new InvalidOperationException("Test allocation failed.");
                Marshal.Copy(bytes, 0, code, bytes.Length);
                if (!VirtualProtect(code, (nuint)bytes.Length, 0x20, out _) || !FlushInstructionCache(-1, code, (nuint)bytes.Length))
                    throw new InvalidOperationException("Test executable protection failed.");
                var result = Marshal.GetDelegateForFunctionPointer<Thunk>(code)(data);
                var expectedResult = scenario is "oversized" or "native_failure" ? 0 : 1;
                var expectedText = scenario is "oversized" or "native_failure" ? "" : text;
                if (!initialized || result != expectedResult || Marshal.PtrToStringUTF8(data + 512) != expectedText ||
                    Marshal.ReadByte(data + 512 + capacity) != 0x5a || released != (scenario is "heap" or "oversized" or "native_failure" ? 1 : 0))
                    throw new InvalidOperationException($"Filesystem slot {slot:x}/{scenario}: native copy, bounds, initialization or ownership failed.");
                checks++;
            }
            finally
            {
                GC.KeepAlive(filesystem); GC.KeepAlive(free);
                if (code != 0) VirtualFree(code, 0, 0x8000);
                if (allocation != 0) Marshal.FreeHGlobal(allocation);
                Marshal.FreeHGlobal(owner); Marshal.FreeHGlobal(table); Marshal.FreeHGlobal(data);
            }
        }
        return checks;
    }

    internal static int ProcessHandles()
    {
        var checks = 0;
        foreach (var scenario in new[] { "valid", "reordered", "wrong_name", "short_name", "wrong_kind", "bad_index", "null_module", "canonical_mismatch" })
        {
            var data = Marshal.AllocHGlobal(8192);
            nint code = 0;
            try
            {
                Marshal.Copy(new byte[8192], 0, data, 8192);
                var index = scenario == "reordered" ? 7 : scenario == "bad_index" ? 700 : 422;
                var id = (scenario == "wrong_kind" ? 0x50000000 : 0x20000000) | index;
                Marshal.WriteIntPtr(data + 256 + 0x18, scenario == "null_module" ? 0 : data + 768);
                Marshal.WriteIntPtr(data + 768 + 0x50, data + 1024);
                Marshal.WriteInt32(data + 768 + 0x5c, 479);
                Marshal.WriteIntPtr(data + 1024 + index * 8, data + 7000);
                Marshal.WriteInt32(data + 7008, scenario == "canonical_mismatch" ? id + 1 : id);
                Marshal.WriteIntPtr(data + 7016, data + 7200);
                var text = Encoding.ASCII.GetBytes((scenario == "wrong_name" ? "OtherHandle" : scenario == "short_name" ? "P" : "ProcessHandle") + "\0");
                Marshal.Copy(text, 0, data + 7200, text.Length);
                var type = typeof(NativeWorkbench).GetNestedType("E", BindingFlags.NonPublic)!;
                var emitter = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [NativeWorkbenchProfile.Experimental19], null)!;
                void B(params byte[] bytes) => type.GetMethod("B")!.Invoke(emitter, [bytes]);
                void Label(string label) => type.GetMethod("Label")!.Invoke(emitter, [label]);
                B(0x53, 0x48, 0x83, 0xec, 0x20, 0x48, 0x89, 0xcb, 0x48, 0x8d, 0x93, 0, 1, 0, 0, 0xb8);
                B(BitConverter.GetBytes(id));
                typeof(NativeWorkbench).GetMethod("EmitProcessHandleGuard", BindingFlags.Static | BindingFlags.NonPublic)!
                    .Invoke(null, [emitter, "rejected"]);
                B(0xb8, 1, 0, 0, 0, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3);
                Label("rejected"); B(0x31, 0xc0, 0x48, 0x83, 0xc4, 0x20, 0x5b, 0xc3);
                var bytes = (byte[])type.GetMethod("Done")!.Invoke(emitter, null)!;
                code = VirtualAlloc(0, (nuint)bytes.Length, 0x3000, 4);
                if (code == 0) throw new InvalidOperationException("Test allocation failed.");
                Marshal.Copy(bytes, 0, code, bytes.Length);
                if (!VirtualProtect(code, (nuint)bytes.Length, 0x20, out _) || !FlushInstructionCache(-1, code, (nuint)bytes.Length))
                    throw new InvalidOperationException("Test executable protection failed.");
                var result = Marshal.GetDelegateForFunctionPointer<Thunk>(code)(data);
                if (result != (scenario is "valid" or "reordered" ? 1 : 0))
                    throw new InvalidOperationException("ProcessHandle metadata guard failed: " + scenario);
                checks++;
            }
            finally
            {
                if (code != 0) VirtualFree(code, 0, 0x8000);
                Marshal.FreeHGlobal(data);
            }
        }
        return checks;
    }
}
