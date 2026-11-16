using System.Runtime.CompilerServices;
using HandPegApp.Services;

namespace HandPegGoldenTests;

/// <summary>
/// compositor-shared-layout: every struct laid over the compositor's shared memory fits its slot (a source entry is exactly its
/// stride), so the longest path a source entry accepts never reaches the next entry's generation, and the last entry never
/// reaches the resource table. Pure: a control plane made and checked in this process, no worker.
/// </summary>
public static class SharedLayoutTests
{
    public static IEnumerable<GraphResult> All()
    {
        yield return Case("compositor-shared-layout", "# The compositor's shared memory: each struct fits its slot; the longest source path stays inside its own entry.", Layout);
    }

    private static void Layout(Action<bool, string> expect)
    {
        var verified = true;
        try
        {
            SharedLayout.Verify();
        }
        catch (InvalidOperationException)
        {
            verified = false;
        }

        expect(verified, "SharedLayout.Verify finds every shared struct within its slot (the control block, the scene header, a layer, a source and a resource entry)");
        expect(Unsafe.SizeOf<SourceEntry>() == CompositorProtocol.SourceEntryBytes && 32 + CompositorProtocol.MaxPathChars * 2 == CompositorProtocol.SourceEntryBytes,
            "a source entry is exactly its 1 KB stride: 32 bytes of fields and a path field of MaxPathChars UTF-16 characters");

        using var memory = CompositorSharedMemory.Create("HandPeg.GoldenTests.Layout." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N"));
        ref var next = ref memory.Source(1);
        (next.Generation, next.Kind) = (1234, 1);
        ref var resource = ref memory.Resource(0);
        (resource.Generation, resource.Kind) = (5678, 2);

        var longest = new string('A', CompositorProtocol.MaxPathChars - 1);
        CompositorSharedMemory.WriteString(memory.Source(0).Path, longest);
        CompositorSharedMemory.WriteString(memory.Source(CompositorProtocol.MaxSources - 1).Path, longest);
        expect(CompositorSharedMemory.ReadString(memory.Source(0).Path) == longest, "the longest path a source entry accepts (MaxPathChars - 1 characters) reads back whole");
        expect(memory.Source(1).Generation == 1234 && memory.Source(1).Kind == 1, "writing it leaves the next entry's generation and kind untouched");
        expect(memory.Resource(0).Generation == 5678 && memory.Resource(0).Kind == 2, "writing it into the last source entry leaves the resource table's first entry untouched");
    }
}
