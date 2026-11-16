global using static HandPegGoldenTests.Reflect;

using System.Reflection;
using HandPegApp.Models.Timeline;
using HandPegApp.Services;
using HandPegApp.ViewModels;

namespace HandPegGoldenTests;

/// <summary>
/// The reflection the goldens still need, in one place: the binding flags for a private instance member, and absence checks
/// that search every kind of member and anchor on a member that must still exist, so a renamed type cannot make them pass.
/// Prefer an internal seam (HandPegApp lists this assembly in InternalsVisibleTo) over a new string lookup.
/// </summary>
internal static class Reflect
{
    /// <summary>A private (or internal) instance member.</summary>
    public const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;

    private const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Whether <paramref name="type"/> has no field, property, method or event named <paramref name="name"/> (public or not,
    /// instance or static). Throws when <paramref name="anchor"/>, a member that must exist, is missing too: then the type
    /// itself moved and the absence would prove nothing.
    /// </summary>
    public static bool HasNoMember(Type type, string name, string anchor)
    {
        if (type.GetMember(anchor, Every).Length == 0)
            throw new MissingMemberException(type.FullName, anchor);
        return type.GetMember(name, Every).Length == 0;
    }
}

/// <summary>A view model for a test: no window, optionally with a document installed and media information set.</summary>
internal static class ViewModelFixture
{
    public static MainViewModel Create(TimelineDocument? document = null, MediaInfo? info = null)
    {
        var vm = new MainViewModel();
        if (info is not null)
            vm.SetMediaInfoForTests(info);
        if (document is not null)
            vm.InstallDocument(document);
        return vm;
    }
}
