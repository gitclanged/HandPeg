using System.IO;
using System.Runtime.InteropServices;

namespace HandPegBootstrap;

/// <summary>
/// HTTPS through WinHTTP (Pass 5.2.1): Windows' own client, which takes the PC's proxy (automatic, else the one set for WinHTTP), follows
/// redirects and checks certificates; nothing of .NET's network stack is built into HandPeg.exe. Only https:// addresses are fetched.
/// A download's SHA-512 is worked out by BCrypt as its bytes arrive and must be the one Microsoft lists, or the file is deleted.
/// </summary>
internal static unsafe class Web
{
    private const string Agent = "HandPeg-Portable/5.2.1";

    /// <summary>A small file (the release list), whole.</summary>
    public static byte[] Get(string url, CancellationToken cancel)
    {
        using var memory = new MemoryStream();
        Fetch(url, memory, cancel, null, null);
        return memory.ToArray();
    }

    /// <summary>A file written to <paramref name="path"/>, its SHA-512 (hex) checked; <paramref name="progress"/> told the bytes so far and the whole (0 when unknown).</summary>
    public static void Download(string url, string path, string sha512, CancellationToken cancel, Action<long, long> progress)
    {
        using var hash = new Sha512();
        try
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                Fetch(url, file, cancel, hash, progress);

            var got = Convert.ToHexString(hash.Finish());
            if (!got.Equals(sha512, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"{Path.GetFileName(path)} did not arrive as Microsoft published it (its SHA-512 differs): it was not used.");
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private static void Fetch(string url, Stream output, CancellationToken cancel, Sha512? hash, Action<long, long>? progress)
    {
        var (host, port, path) = Crack(url);
        var session = Native.WinHttpOpen(Agent, Native.WinHttpAccessTypeAutomaticProxy, IntPtr.Zero, IntPtr.Zero, 0);
        if (session == IntPtr.Zero)
            session = Native.WinHttpOpen(Agent, Native.WinHttpAccessTypeDefaultProxy, IntPtr.Zero, IntPtr.Zero, 0);
        if (session == IntPtr.Zero)
            throw Failed("open a connection");

        IntPtr connection = IntPtr.Zero, request = IntPtr.Zero;
        try
        {
            Native.WinHttpSetTimeouts(session, 30_000, 30_000, 30_000, 60_000);
            connection = Native.WinHttpConnect(session, host, port, 0);
            if (connection == IntPtr.Zero)
                throw Failed($"reach {host}");
            request = Native.WinHttpOpenRequest(connection, "GET", path, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, Native.WinHttpFlagSecure);
            if (request == IntPtr.Zero || !Native.WinHttpSendRequest(request, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, IntPtr.Zero)
                || !Native.WinHttpReceiveResponse(request, IntPtr.Zero))
                throw Failed($"download from {host}");

            uint status = 0, size = sizeof(uint);
            Native.WinHttpQueryHeaders(request, Native.WinHttpQueryStatusCode | Native.WinHttpQueryFlagNumber, IntPtr.Zero, &status, ref size, IntPtr.Zero);
            if (status != 200)
                throw new IOException($"{host} answered {status} for {Path.GetFileName(path)}.");

            long total = 0;
            size = sizeof(long);
            if (!Native.WinHttpQueryHeaders(request, Native.WinHttpQueryContentLength | Native.WinHttpQueryFlagNumber64, IntPtr.Zero, &total, ref size, IntPtr.Zero))
                total = 0;

            var buffer = new byte[1 << 16];
            long received = 0;
            fixed (byte* bytes = buffer)
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (!Native.WinHttpReadData(request, bytes, (uint)buffer.Length, out var read))
                        throw Failed($"download from {host}");
                    if (read == 0)
                        break;
                    output.Write(buffer, 0, (int)read);
                    hash?.Add(bytes, read);
                    received += read;
                    progress?.Invoke(received, total);
                }
            }

            if (total > 0 && received != total)
                throw new IOException($"The download from {host} was cut short ({received} of {total} bytes).");
        }
        finally
        {
            if (request != IntPtr.Zero)
                Native.WinHttpCloseHandle(request);
            if (connection != IntPtr.Zero)
                Native.WinHttpCloseHandle(connection);
            Native.WinHttpCloseHandle(session);
        }
    }

    /// <summary>An https:// address's host, port and path with its query; anything else is refused.</summary>
    private static (string Host, ushort Port, string Path) Crack(string url)
    {
        var parts = new Native.UrlComponents { Size = (uint)sizeof(Native.UrlComponents), SchemeLength = uint.MaxValue, HostNameLength = uint.MaxValue, UrlPathLength = uint.MaxValue, ExtraInfoLength = uint.MaxValue };
        fixed (char* text = url)
        {
            if (!Native.WinHttpCrackUrl(text, (uint)url.Length, 0, ref parts) || parts.SchemeNumber != Native.InternetSchemeHttps)
                throw new IOException($"Not an https address: {url}");
            return (new string(parts.HostName, 0, (int)parts.HostNameLength), parts.Port,
                new string(parts.UrlPath, 0, (int)parts.UrlPathLength) + new string(parts.ExtraInfo, 0, (int)parts.ExtraInfoLength));
        }
    }

    private static IOException Failed(string what) => new($"HandPeg could not {what} (WinHTTP error {Marshal.GetLastPInvokeError()}).");

    /// <summary>SHA-512 by BCrypt, a piece at a time.</summary>
    private sealed class Sha512 : IDisposable
    {
        private readonly IntPtr _algorithm, _hash;

        public Sha512()
        {
            if (Native.BCryptOpenAlgorithmProvider(out _algorithm, "SHA512", IntPtr.Zero, 0) != 0 || Native.BCryptCreateHash(_algorithm, out _hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0) != 0)
                throw new IOException("Windows could not work out a SHA-512.");
        }

        public void Add(byte* bytes, uint length)
        {
            if (Native.BCryptHashData(_hash, bytes, length, 0) != 0)
                throw new IOException("Windows could not work out a SHA-512.");
        }

        public byte[] Finish()
        {
            var digest = new byte[64];
            fixed (byte* output = digest)
            {
                if (Native.BCryptFinishHash(_hash, output, 64, 0) != 0)
                    throw new IOException("Windows could not work out a SHA-512.");
            }

            return digest;
        }

        public void Dispose()
        {
            Native.BCryptDestroyHash(_hash);
            Native.BCryptCloseAlgorithmProvider(_algorithm, 0);
        }
    }
}
