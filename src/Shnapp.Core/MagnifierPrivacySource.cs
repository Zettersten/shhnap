using System.Collections.Immutable;

namespace Shnapp.Core;

/// <summary>Retains frozen covers and active privacy effects when preparing lens source pixels.</summary>
internal static class MagnifierPrivacySource
{
    internal static ShnappDocument? Create(ShnappDocument document)
    {
        ImmutableArray<Annotation> layers = document.OrderedAnnotations
            .Where(item => !item.HiddenByCrop &&
                (item.IsFlattened || item.Kind == AnnotationKind.Redaction))
            .ToImmutableArray();
        return layers.IsEmpty ? null : document with
        {
            Annotations = layers,
            HasWindowShadow = false,
        };
    }
}
