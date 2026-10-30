using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace HandPegApp.Services;

/// <summary>
/// Whether a file lies on a fast local solid-state drive (Pass 5.0-Release, the user's directive 5: speculative work
/// before a drop is done only where reading ahead costs nothing). Asked of the volume itself: a fixed drive whose device
/// says it has no seek penalty (IOCTL_STORAGE_QUERY_PROPERTY, StorageDeviceSeekPenaltyProperty, the volume opened with no
/// access rights), on an internal bus. A network share, a removable drive (USB, SD, an optical drive), a spinning disk or
/// a volume that will not say counts as not.
/// <para>Learnt once per volume and kept (<see cref="Learn"/>, on the thread pool: never on the interface's thread, and a
/// UNC path or a mapped network drive is never opened at all). <see cref="IsFast"/> only reads what is known: the
/// interface's thread may ask it, and a volume not learnt yet counts as not fast until it is.</para>
/// </summary>
public static partial class DriveKind
{
    private static readonly ConcurrentDictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<bool>> Learning = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a file's volume is known to be a fast local SSD (false while it is not known yet). Touches no disk.</summary>
    public static bool IsFast(string path) => RootOf(path) is { } root && Known.TryGetValue(root, out var fast) && fast;

    /// <summary>Whether a file's volume is a fast local SSD, learnt once per volume on the thread pool (false for anything else, or on failure).</summary>
    public static Task<bool> Learn(string path)
    {
        if (RootOf(path) is not { } root)
            return Task.FromResult(false);
        if (Known.TryGetValue(root, out var fast))
            return Task.FromResult(fast);
        return Learning.GetOrAdd(root, static key => Task.Run(() =>
        {
            var answer = false;
            try
            {
                answer = Ask(key);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write($"Drive kind of {key}: {ex.Message}");
            }

            Known[key] = answer;
            Learning.TryRemove(key, out _);
            return answer;
        }));
    }

    /// <summary>The volume of a local path as "C:\" (null for a UNC path, a device path, or anything not rooted in a drive letter).</summary>
    private static string? RootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length < 3 || trimmed[1] != ':' || !char.IsAsciiLetter(trimmed[0]) || trimmed[2] is not ('\\' or '/'))
            return null;
        return char.ToUpperInvariant(trimmed[0]) + ":\\";
    }

    private const uint DriveFixed = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0, StorageDeviceSeekPenaltyProperty = 7;

    // STORAGE_BUS_TYPE values that are removable or outside the machine: USB, 1394, SD, MMC, iSCSI.
    private static bool ExternalBus(int bus) => bus is 7 or 4 or 12 or 13 or 9;

    private static unsafe bool Ask(string root)
    {
        // A mapped network drive, a removable drive, an optical drive: not, without opening anything.
        if (GetDriveTypeW(root) != DriveFixed)
            return false;

        var device = $@"\\.\{root[..2]}";
        var handle = CreateFileW(device, 0, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
        if (handle == -1 || handle == 0)
            return false;
        try
        {
            // The device behind the volume: on a bus inside the machine, its media not removable.
            var query = stackalloc int[3];
            (query[0], query[1], query[2]) = (StorageDeviceProperty, 0, 0);
            var descriptor = stackalloc byte[1024];
            if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, descriptor, 1024, out var returned, 0) == 0 || returned < 32)
                return false;
            var removable = descriptor[10] != 0;
            var bus = *(int*)(descriptor + 28);
            if (removable || ExternalBus(bus))
                return false;

            // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty.
            (query[0], query[1], query[2]) = (StorageDeviceSeekPenaltyProperty, 0, 0);
            var penalty = stackalloc byte[12];
            if (DeviceIoControl(handle, IoctlStorageQueryProperty, query, 12, penalty, 12, out returned, 0) == 0 || returned < 9)
                return false;
            return penalty[8] == 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string root);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial int DeviceIoControl(nint device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    private static partial int CloseHandle(nint handle);
}
