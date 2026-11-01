using Microsoft.UI.Xaml.Automation;

namespace Shnapp.App.Video;

public sealed partial class VideoEditorPage
{
    public void ShowCaptureWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        CaptureWarningBar.Message = message;
        AutomationProperties.SetName(CaptureWarningBar,
            "Camera recording warning. " + message);
        CaptureWarningBar.IsOpen = true;
    }
}
