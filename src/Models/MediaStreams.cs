namespace HandPegApp.Models;

/// <param name="Index">Position among the audio streams, as used in "0:a:N".</param>
/// <param name="HandlerName">
/// MP4/MOV handler name used in place of a title; read only by the voice track scanner (most are a generic "SoundHandler").
/// </param>
public sealed record AudioStreamInfo(
    int Index, string Codec, int Channels, string ChannelLayout, int SampleRate, long BitRate, string Title, string Language, string HandlerName = "");

/// <param name="Index">Position among the subtitle streams, as used in "0:s:N".</param>
public sealed record SubtitleStreamInfo(int Index, string Codec, string Title, string Language)
{
    /// <summary>Only text subtitles can be drawn by the subtitles filter; bitmap formats cannot.</summary>
    public bool IsText => Codec is not ("hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub");
}
