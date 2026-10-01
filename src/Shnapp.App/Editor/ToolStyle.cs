using Shnapp.Core;

namespace Shnapp.App.Editor;

/// <summary>Session defaults for one editor tool, independent of the selected annotation.</summary>
internal sealed class ToolStyle
{
    internal uint Primary { get; set; } = 0xFFE5484D;
    internal uint Secondary { get; set; } = 0xFFE5484D;
    internal double StrokeWidth { get; set; } = 3;
    internal bool OutlineShape { get; set; } = true;
    internal string FontFamily { get; set; } = "Segoe UI Variable Text";
    internal int FontWeight { get; set; } = 600;
    internal bool Italic { get; set; }
    internal double FontSize { get; set; } = 18;
    internal bool TextKerning { get; set; } = true;
    internal double TextLetterSpacing { get; set; }
    internal TextTransformMode TextTransform { get; set; }
    internal TextHorizontalAlignment TextAlignment { get; set; }
    internal TextTruncation TextTruncation { get; set; }
    internal double TextLineHeight { get; set; }
    internal double StepDiameter { get; set; } = 28;
    internal bool FillShape { get; set; }
    internal double FillOpacity { get; set; } = 25;
    internal bool StartArrow { get; set; }
    internal bool EndArrow { get; set; }
    internal LineEndCap StartCap { get; set; }
    internal LineEndCap EndCap { get; set; }
    internal LinePattern LinePattern { get; set; }
    internal StepLabelFormat StepLabelFormat { get; set; }
    internal RedactionMode RedactionMode { get; set; } = RedactionMode.Solid;

    internal static ToolStyle Defaults(EditorTool tool) => new()
    {
        Primary = tool switch
        {
            EditorTool.Step => 0xFF0A84FF,
            EditorTool.Redaction => 0xFF111418,
            _ => 0xFFE5484D,
        },
        Secondary = tool == EditorTool.Step ? 0xFFFFFFFF : 0xFFE5484D,
        FontSize = tool == EditorTool.Step ? 13 : 18,
        EndCap = tool == EditorTool.Arrow ? LineEndCap.Triangle : LineEndCap.None,
    };
}
