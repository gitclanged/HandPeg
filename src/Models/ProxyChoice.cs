namespace HandPegApp.Models;

/// <summary>Whether Live Preview plays a clip's file from a proxy (see ProxyCache). Set from the clip's right-click menu.</summary>
public enum ProxyChoice
{
    /// <summary>When the file is heavy, and proxies are switched on (Settings).</summary>
    Auto,

    /// <summary>Always, heavy or not (Make Proxy).</summary>
    Make,

    /// <summary>Never: the original is played (Use Original).</summary>
    Original,
}
