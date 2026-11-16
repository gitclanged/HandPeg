using HandPegApp.Models.Effects;

namespace HandPegApp.Models.Timeline;

/// <summary>
/// Transactional operations for document-level values (markers, Sequence Output effects, natural frame, frame rate) so they roll back
/// with the transaction and raise <see cref="TimelineDocument.Committed"/>. View-model mirrors (FlushLooks) still copy markers and
/// effects in before reads; these cover direct document changes.
/// </summary>
internal sealed partial class TimelineTransaction
{
    // The document-level values as they were before this transaction (and each around it) first changed one of them.
    private DocumentFields? _documentFieldsBefore;

    private sealed record DocumentFields(
        List<TimelineMarker> Markers, List<EffectState> SequenceEffects, bool SequenceEffectsBypassed,
        int NaturalWidth, int NaturalHeight, bool ExplicitFrame, FrameRate FrameRate, bool ExplicitFrameRate);

    public void SetMarkers(IReadOnlyList<TimelineMarker> markers) => Step(() =>
    {
        if (_doc.Markers.SequenceEqual(markers))
            return false;
        RememberDocumentFields();
        _doc.Markers = [.. markers];
        return true;
    });

    public void SetSequenceEffects(IReadOnlyList<EffectState> effects, bool bypassed) => Step(() =>
    {
        if (bypassed == _doc.SequenceEffectsBypassed && _doc.SequenceEffects.SequenceEqual(effects))
            return false;
        RememberDocumentFields();
        (_doc.SequenceEffects, _doc.SequenceEffectsBypassed) = ([.. effects], bypassed);
        return true;
    });

    public void SetFrame(int naturalWidth, int naturalHeight, bool explicitFrame) => Step(() =>
    {
        var (width, height) = (Math.Max(naturalWidth, 0), Math.Max(naturalHeight, 0));
        if (width == _doc.NaturalWidth && height == _doc.NaturalHeight && explicitFrame == _doc.ExplicitFrame)
            return false;
        RememberDocumentFields();
        (_doc.NaturalWidth, _doc.NaturalHeight, _doc.ExplicitFrame) = (width, height, explicitFrame);
        return true;
    });

    public void SetFrameRate(FrameRate rate, bool explicitRate) => Step(() =>
    {
        if (!rate.IsValid)
            throw new TimelineEditException($"Frame rate {rate.Numerator}/{rate.Denominator} is not positive.");
        if (rate == _doc.FrameRate && explicitRate == _doc.ExplicitFrameRate)
            return false;
        RememberDocumentFields();
        (_doc.FrameRate, _doc.ExplicitFrameRate) = (rate, explicitRate);
        return true;
    });

    /// <summary>Snapshots document-level values before a write; the innermost transaction reports the change.</summary>
    private void RememberDocumentFields()
    {
        foreach (var open in _doc.OpenTransactions)
        {
            open._documentFieldsBefore ??= new DocumentFields(_doc.Markers, _doc.SequenceEffects, _doc.SequenceEffectsBypassed,
                _doc.NaturalWidth, _doc.NaturalHeight, _doc.ExplicitFrame, _doc.FrameRate, _doc.ExplicitFrameRate);
        }

        _doc.OpenTransactions[^1]._documentTouched = true;
    }

    /// <summary>Rollback step restoring document-level values (lists replaced, never mutated).</summary>
    private void RestoreDocumentFields()
    {
        if (_documentFieldsBefore is not { } before)
            return;
        (_doc.Markers, _doc.SequenceEffects, _doc.SequenceEffectsBypassed) = (before.Markers, before.SequenceEffects, before.SequenceEffectsBypassed);
        (_doc.NaturalWidth, _doc.NaturalHeight, _doc.ExplicitFrame) = (before.NaturalWidth, before.NaturalHeight, before.ExplicitFrame);
        (_doc.FrameRate, _doc.ExplicitFrameRate) = (before.FrameRate, before.ExplicitFrameRate);
    }
}
