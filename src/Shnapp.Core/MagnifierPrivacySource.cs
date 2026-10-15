using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Retains frozen covers and active privacy effects when preparing lens source pixels.</summary>
internal static class MagnifierPrivacySource
{
    // The preview lens moves without changing its source pixels. Compare only the
    // original's visible area and layers that can appear beneath the lens.
    internal static bool HasSamePixels(ShnappDocument previous, ShnappDocument current) =>
        previous.PixelWidth == current.PixelWidth &&
        previous.PixelHeight == current.PixelHeight &&
        previous.Crop == current.Crop &&
        previous.BaseImageCrop == current.BaseImageCrop &&
        previous.HideOriginalImage == current.HideOriginalImage &&
        PrivacyLayers(previous).SequenceEqual(PrivacyLayers(current),
            ReferenceEqualityComparer.Instance);

    internal static ShnappDocument? Create(ShnappDocument document)
    {
        ImmutableArray<Annotation> layers = PrivacyLayers(document).ToImmutableArray();
        return layers.IsEmpty ? null : document with
        {
            Annotations = layers,
            HasWindowShadow = false,
        };
    }

    private static IEnumerable<Annotation> PrivacyLayers(ShnappDocument document) =>
        document.OrderedAnnotations.Where(item => !item.HiddenByCrop &&
            (item.IsFlattened || item.Kind == AnnotationKind.Redaction));
}
