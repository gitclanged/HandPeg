param([string]$GraphFile, [string]$Video, [int]$Seconds = 4, [string]$Libmpv = "$env:LOCALAPPDATA\HandPeg-Data\deps\mpv\libmpv-2.dll")
# Plays a video in libmpv with a lavfi-complex graph, headless, and says whether it survived. Run in its own
# process: a crash in libmpv takes the process with it. -Libmpv defaults to the copy HandPeg downloads.
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class Mpv {
    const string L = @"$Libmpv";
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr h);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr h, byte[] n, byte[] v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr h, byte[] n, byte[] v);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_string(IntPtr h, byte[] a);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr h, double t);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int e);
    [DllImport(L, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_request_log_messages(IntPtr h, byte[] level);
    public static byte[] U(string s) { var b = Encoding.UTF8.GetBytes(s); Array.Resize(ref b, b.Length + 1); return b; }
}
"@
$h = [Mpv]::mpv_create()
foreach ($pair in @(@('vo', 'null'), @('ao', 'null'), @('config', 'no'), @('terminal', 'no'), @('idle', 'yes'), @('keep-open', 'yes'), @('pause', 'no'),
                    @('hwdec', 'auto-copy'), @('profile', 'fast'), @('vd-lavc-threads', '0'), @('hr-seek', 'yes'))) {
    [void][Mpv]::mpv_set_option_string($h, [Mpv]::U($pair[0]), [Mpv]::U($pair[1]))
}
[void][Mpv]::mpv_initialize($h)
[void][Mpv]::mpv_request_log_messages($h, [Mpv]::U('error'))
$graph = (Get-Content -Raw $GraphFile).Trim()
$cmd = 'loadfile "' + $Video.Replace('\', '/') + '"'
[void][Mpv]::mpv_command_string($h, [Mpv]::U($cmd))
Start-Sleep -Milliseconds 1500
$r = [Mpv]::mpv_set_property_string($h, [Mpv]::U('lavfi-complex'), [Mpv]::U($graph))
"set lavfi-complex: $r $([Runtime.InteropServices.Marshal]::PtrToStringAnsi([Mpv]::mpv_error_string($r)))"
$until = [DateTime]::Now.AddSeconds($Seconds)
while ([DateTime]::Now -lt $until) { [void][Mpv]::mpv_wait_event($h, 0.1) }
"survived $Seconds s"
