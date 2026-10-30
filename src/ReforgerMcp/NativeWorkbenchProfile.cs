namespace ReforgerMcp;

// Build identity owns address and layout selection. Installation labels and version
// strings never authorize native dispatch. No runtime scanning or address input.
internal sealed partial class NativeWorkbenchProfile(string id, string sha256, bool experimental,
    IReadOnlyDictionary<int, int> addresses)
{
    internal string Id { get; } = id;
    internal string Sha256 { get; } = sha256;
    internal bool Experimental { get; } = experimental;
    internal int ApplicationBusyOffset => Experimental ? 0x1656 : 0x164e;
    internal int ApplicationSettingsOffset => Experimental ? 0x1608 : 0x1600;
    internal int Rva(int stableRva) => !Experimental ? stableRva : addresses.TryGetValue(stableRva, out var value) ? value :
        throw new InvalidOperationException($"Native profile {Id} does not authorize RVA {stableRva:x}. No call dispatched.");
    internal long Address(nint image, int stableRva) => image + Rva(stableRva);
    internal int WorldApiSlot(int stableSlot)
    {
        if (!Experimental) return stableSlot;
        return stableSlot switch
        {
            0x38 or 0x40 or 0x48 or 0x80 or 0x98 or 0xa0 or 0xa8 or 0xb0 or 0x148 or 0x150 => stableSlot,
            0x1c8 => 0x1d0,
            0x1d8 => 0x1e0,
            0x238 => 0x240,
            0x318 => 0x320,
            0x320 => 0x330,
            0x328 => 0x338,
            0x478 => 0x498,
            _ => throw new InvalidOperationException($"Unverified WorldEditor API slot {stableSlot:x} for {Id}.")
        };
    }

    internal static readonly NativeWorkbenchProfile Stable = new("stable-1.8.0.13",
        "7dd36ca1f662b0c13669a4f2f7f10e3c461c2906b021e742272d64b3ab7e9798", false, new Dictionary<int, int>());

    internal static NativeWorkbenchProfile Select(string sha256) =>
        sha256.Equals(Stable.Sha256, StringComparison.OrdinalIgnoreCase) ? Stable :
        sha256.Equals(Experimental19.Sha256, StringComparison.OrdinalIgnoreCase) ? Experimental19 :
            throw new InvalidOperationException("Unsupported native Workbench binary; prepare and verify an exact compatibility profile. No fallback.");
}
