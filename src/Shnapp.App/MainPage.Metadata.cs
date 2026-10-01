using System.Globalization;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Shnapp.App;

public sealed partial class MainPage
{
    internal void UpdateDocumentMetadata()
    {
        if (_editor is null || _controller is null)
        {
            ViewModel.DocumentMetadata = string.Empty;
            AutomationProperties.SetName(DocumentMetadataText, string.Empty);
            ToolTipService.SetToolTip(DocumentMetadataText, null);
            return;
        }

        var document = _editor.Current;
        var viewport = document.Viewport;
        string date = document.CreatedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt", CultureInfo.CurrentCulture);
        string size = "Saving…";
        try
        {
            var png = new FileInfo(_controller.Library.GetExportPath(document.Id));
            if (png.Exists)
            {
                size = ShnappMetadata.FormatFileSize(png.Length);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        ViewModel.DocumentMetadata = $"{date}   ·   {viewport.Width:0} × {viewport.Height:0} px   ·   PNG {size}";
        AutomationProperties.SetName(DocumentMetadataText, ViewModel.DocumentMetadata);
        ToolTipService.SetToolTip(DocumentMetadataText, ViewModel.DocumentMetadata);
    }
}
