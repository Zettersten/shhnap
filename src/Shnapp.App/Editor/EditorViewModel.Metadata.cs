using CommunityToolkit.Mvvm.ComponentModel;

namespace Shnapp.App.Editor;

public sealed partial class EditorViewModel
{
    /// <summary>Capture date, current canvas dimensions, and saved PNG size for the editor footer.</summary>
    [ObservableProperty]
    public partial string DocumentMetadata { get; set; } = string.Empty;
}
