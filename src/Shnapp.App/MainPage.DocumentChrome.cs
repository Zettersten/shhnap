namespace Shnapp.App;

public sealed partial class MainPage
{
    private bool _deferDocumentChromeRefresh;
    private bool _documentChromeRefreshPending;

    private void RunWithDeferredDocumentChromeRefresh(Action change)
    {
        bool wasDeferred = _deferDocumentChromeRefresh;
        _deferDocumentChromeRefresh = true;
        try
        {
            change();
        }
        finally
        {
            _deferDocumentChromeRefresh = wasDeferred;
        }
    }

    private void FlushDeferredDocumentChromeRefresh()
    {
        if (!_documentChromeRefreshPending)
        {
            return;
        }

        _documentChromeRefreshPending = false;
        RefreshDocumentChrome();
    }

    private void RefreshDocumentChrome()
    {
        if (_editor is null)
        {
            return;
        }

        if (_fontPreviewImage is not null)
        {
            ClearFontPreview();
        }

        ViewModel.Title = _editor.Current.Title;
        var viewport = _editor.Current.Viewport;
        ViewModel.Dimensions = $"{viewport.Width:0} × {viewport.Height:0} px";
        UpdateDocumentMetadata();
        ViewModel.Status = FooterToolHint();
        UpdateInspector();
        UpdateCropInspector();
    }
}
