using Microsoft.UI.Xaml.Controls;
using Windows.Globalization.NumberFormatting;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    private void ConfigureInspectorNumberFormatting()
    {
        DecimalFormatter seconds = CompactFormatter(.001);
        foreach (NumberBox field in new[]
        {
            ClipStartSeconds, ClipSourceInSeconds, ClipSourceOutSeconds,
            OverlayDurationSeconds, TextDurationSeconds,
        })
            field.NumberFormatter = seconds;

        DecimalFormatter pixels = CompactFormatter(.1);
        foreach (NumberBox field in new[]
        {
            OverlayX, OverlayY, OverlayWidth, OverlayHeight,
            TextX, TextY, TextWidth, TextHeight, TextFontSize,
            TextPaddingBox, TextBorderWidthBox, TextCornerRadiusBox,
        })
            field.NumberFormatter = pixels;

        DecimalFormatter fade = CompactFormatter(.01);
        VideoFadeInSeconds.NumberFormatter = fade;
        VideoFadeOutSeconds.NumberFormatter = fade;
    }

    private static DecimalFormatter CompactFormatter(double increment) => new()
    {
        IntegerDigits = 1,
        FractionDigits = 0,
        NumberRounder = new IncrementNumberRounder { Increment = increment },
    };
}
