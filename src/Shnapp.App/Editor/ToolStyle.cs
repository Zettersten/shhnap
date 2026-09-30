namespace Shnapp.App.Editor;

/// <summary>Session defaults for one editor tool, independent of the selected annotation.</summary>
internal sealed class ToolStyle
{
    internal uint Primary { get; set; } = 0xFFE5484D;
    internal uint Secondary { get; set; } = 0xFFE5484D;
    internal double StrokeWidth { get; set; } = 3;
    internal string FontFamily { get; set; } = "Segoe UI Variable Text";
    internal int FontWeight { get; set; } = 600;
    internal bool Italic { get; set; }
    internal double FontSize { get; set; } = 18;
    internal double StepDiameter { get; set; } = 28;
    internal bool FillShape { get; set; }
    internal double FillOpacity { get; set; } = 25;
    internal bool StartArrow { get; set; }
    internal bool EndArrow { get; set; }

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
        EndArrow = tool == EditorTool.Arrow,
    };
}
