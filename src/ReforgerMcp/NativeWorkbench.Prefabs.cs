using System.Text.RegularExpressions;

namespace ReforgerMcp;

internal static partial class NativeWorkbench
{
    internal sealed record ChildPrefabCommand(string Ancestor, string Destination);

    private static object ChildPrefabResult(ChildPrefabCommand command, int processId, uint completed,
        bool succeeded, bool acknowledged, long elapsedMs)
    {
        string? resource = null, storedAncestor = null;
        if (completed == 2 && succeeded && File.Exists(command.Destination) && File.Exists(command.Destination + ".meta"))
        {
            resource = Regex.Match(File.ReadAllText(command.Destination + ".meta"),
                "(?mi)^\\s*Name\\s+\"(\\{[0-9A-Fa-f]{16}\\}[^\"]+\\.et)\"").Groups[1].Value;
            using var reader = new StreamReader(command.Destination);
            var header = new char[4096];
            var count = reader.ReadBlock(header, 0, header.Length);
            storedAncestor = Regex.Match(new string(header, 0, count),
                "^\\s*[A-Za-z_][A-Za-z0-9_]*\\s*:\\s*\"(\\{[0-9A-Fa-f]{16}\\}[^\"]+\\.et)\"").Groups[1].Value;
        }
        var identityVerified = !string.IsNullOrEmpty(resource) &&
            !resource[..18].Equals(command.Ancestor[..18], StringComparison.OrdinalIgnoreCase);
        var ancestorVerified = !string.IsNullOrEmpty(storedAncestor) && storedAncestor[..18].Equals(command.Ancestor[..18], StringComparison.OrdinalIgnoreCase);
        return new { state = completed switch { 2 => !succeeded ? "creation_failed" : acknowledged && identityVerified && ancestorVerified ? "created" : "creation_incomplete",
            13 => "destination_exists", 17 => "ancestor_unavailable", _ => "outcome_unknown" }, processId,
            ancestorResource = storedAncestor, requestedAncestor = command.Ancestor, ancestorVerified, prefabPath = command.Destination, prefabResource = resource,
            resourceAcknowledged = acknowledged, newIdentityVerified = identityVerified,
            identityEvidence = "Workbench-generated resource metadata and serialized child declaration", elapsedMs,
            message = "Creates an inherited resource through Workbench's native resource owner without creating a world entity. Files may remain after incomplete or failed creation; inspect before another request." };
    }

    private static void EmitChildPrefab(E e, nint image)
    {
        void Call(int rva) { e.B(0x48, 0xb8); e.Image(image, rva); e.B(0xff, 0xd0); }
        void Exists(int offset)
        {
            Call(0x1e90630);
            e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8d, 0x93); e.B(BitConverter.GetBytes(offset));
            e.B(0x41, 0xb8, 0xff, 0xff, 0xff, 0xff, 0xff, 0x90, 0x50, 1, 0, 0, 0x84, 0xc0);
        }
        // Recheck both resource and metadata immediately before the creation call.
        Exists(1024); e.J(0x85, "childExists");
        Exists(2048); e.J(0x85, "childExists");
        e.B(0x48, 0x8d, 0x4b, 96, 0x48, 0x8d, 0x93, 0, 2, 0, 0); Call(0x1e35d50);
        e.B(0xc7, 0x43, 72, 17, 0, 0, 0);
        e.B(0x48, 0x8b, 0x43, 96, 0x48, 0x85, 0xc0); e.J(0x84, "childRelease");
        e.B(0x48, 0x8b, 0x50, 8, 0x48, 0x85, 0xd2); e.J(0x84, "childRelease");
        e.B(0x81, 0x7a, 0xf8, 0xf0, 0, 0, 0); e.J(0x87, "childRelease");
        e.B(0x8b, 0x4a, 0xf8, 0x83, 0xf9, 3); e.J(0x86, "childRelease");
        e.B(0x80, 0x7c, 0x0a, 0xfd, 0x2e); e.J(0x85, "childRelease");
        e.B(0x66, 0x8b, 0x44, 0x0a, 0xfe, 0x66, 0x0d, 0x20, 0x20, 0x66, 0x3d, 0x65, 0x74); e.J(0x85, "childRelease");
        e.B(0x48, 0x89, 0x53, 104);
        // Resolving a path alone is insufficient: a stale GUID must not be
        // replaced silently with whichever resource currently owns that path.
        e.B(0x48, 0x8d, 0x8b, 0x80, 0, 0, 0); Call(0x1e35d50);
        e.B(0x48, 0x8d, 0x4b, 96, 0x48, 0x8d, 0x93, 0x80, 0, 0, 0); Call(0x1e36fd0);
        e.B(0x89, 0x83, 0x88, 0, 0, 0);
        e.B(0x48, 0x8b, 0x8b, 0x80, 0, 0, 0, 0x48, 0x85, 0xc9); e.J(0x84, "childPathIdentityReleased");
        e.B(0xf0, 0xff, 0x09); e.J(0x85, "childPathIdentityReleased"); Call(0x269dc0);
        e.B(0x48, 0xb8); e.Image(image, 0x3c7c260);
        e.B(0x48, 0x8b, 0x08, 0x48, 0x8b, 0x01, 0x48, 0x8b, 0x93, 0x80, 0, 0, 0, 0x45, 0x31, 0xc0, 0xff, 0x50, 0x10);
        e.Label("childPathIdentityReleased");
        e.B(0x83, 0xbb, 0x88, 0, 0, 0, 0); e.J(0x85, "childRelease");

        Call(0x1e90630);
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8b, 0x53, 104,
            0x41, 0xb8, 0xff, 0xff, 0xff, 0xff, 0xff, 0x90, 0x50, 1, 0, 0, 0x84, 0xc0); e.J(0x84, "childRelease");
        // Same mounted-path conversion used by Resource Manager's inherited-file action.
        Call(0x1e90630);
        e.B(0x48, 0x89, 0xc1, 0x48, 0x8b, 0x00, 0x48, 0x8d, 0x93, 0, 4, 0, 0,
            0x4c, 0x8d, 0x83, 0, 6, 0, 0, 0x45, 0x31, 0xc9);
        EmitFileSystemBuffer(e, image, 0x140, 560, "childResolvedDestination");
        e.B(0x84, 0xc0); e.J(0x84, "childRelease");
        e.B(0x80, 0xbb, 0, 6, 0, 0, 0); e.J(0x84, "childRelease");
        // App::CreateInheritedFile(sourcePath, mountedDestination, true).
        // Its owner allocates a detached source, sets its ancestor and writes it.
        e.B(0x48, 0x8b, 0x4b, 16, 0x48, 0x8b, 0x53, 104, 0x4c, 0x8d, 0x83, 0, 6, 0, 0,
            0x41, 0xb9, 1, 0, 0, 0); Call(0x1e6ac0);
        e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 68, 0xc7, 0x43, 72, 2, 0, 0, 0, 0x84, 0xc0); e.J(0x84, "childRelease");
        // Bounded database acknowledgement; no repeated creation call.
        e.B(0x48, 0x8d, 0x8b, 0, 4, 0, 0, 0x48, 0x8d, 0x93, 0x80, 0x0a, 0, 0); Call(0x1f6600);
        e.B(0x84, 0xc0); e.J(0x84, "childRelease");
        e.B(0x31, 0xc9, 0x48, 0x8d, 0x93, 0x8c, 0x0a, 0, 0, 0x41, 0xb8, 0x88, 0x13, 0, 0); Call(0x31a780);
        e.B(0x0f, 0xb6, 0xc0, 0x89, 0x43, 116, 0xf0, 0xff, 0x8b, 0x88, 0x0a, 0, 0);
        e.Label("childRelease");
        e.B(0x48, 0x8b, 0x4b, 96, 0x48, 0x85, 0xc9); e.J(0x84, "return");
        e.B(0xf0, 0xff, 0x09); e.J(0x85, "return"); Call(0x269dc0);
        e.B(0x48, 0xb8); e.Image(image, 0x3c7c260);
        e.B(0x48, 0x8b, 0x08, 0x48, 0x8b, 0x01, 0x48, 0x8b, 0x53, 96, 0x45, 0x31, 0xc0, 0xff, 0x50, 0x10);
        e.J(0x84, "return", true);
        e.Label("childExists"); e.B(0xc7, 0x43, 72, 13, 0, 0, 0); e.J(0x84, "return", true);
    }
}
