using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private bool _closingTitleEditor;

    private void DocumentTitleButton_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        CommitText();
        DocumentTitleEditor.Text = _editor.Current.Title;
        DocumentTitleButton.Visibility = Visibility.Collapsed;
        DocumentTitleEditor.Visibility = Visibility.Visible;
        DocumentTitleEditor.Focus(FocusState.Programmatic);
        DocumentTitleEditor.SelectAll();
    }

    private void DocumentTitleEditor_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            CommitTitleRename(cancelOnInvalid: false);
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            CancelTitleRename();
        }
    }

    private void DocumentTitleEditor_LostFocus(object sender, RoutedEventArgs args) => CommitTitleRename();

    /// <summary>Finishes a pending inline rename before navigation or persistence.</summary>
    internal void CommitTitleRename(bool cancelOnInvalid = true)
    {
        if (_closingTitleEditor || DocumentTitleEditor.Visibility != Visibility.Visible || _editor is null)
        {
            return;
        }

        try
        {
            string title = ShnappTitles.Normalize(DocumentTitleEditor.Text);
            string previous = _editor.Current.Title;
            _editor.Rename(title);
            CloseTitleEditor();
            if (_editor.Current.Title != previous)
            {
                _controller?.SaveCurrent();
            }
        }
        catch (ArgumentException exception)
        {
            ShowMessage("Title not changed", exception.Message, InfoBarSeverity.Warning);
            if (cancelOnInvalid)
            {
                CloseTitleEditor();
            }
            else
            {
                DocumentTitleEditor.Focus(FocusState.Programmatic);
                DocumentTitleEditor.SelectAll();
            }
        }
    }

    private void CancelTitleRename()
    {
        if (DocumentTitleEditor.Visibility == Visibility.Visible)
        {
            CloseTitleEditor();
        }
    }

    private void CloseTitleEditor()
    {
        _closingTitleEditor = true;
        try
        {
            DocumentTitleEditor.Visibility = Visibility.Collapsed;
            DocumentTitleButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _closingTitleEditor = false;
        }
    }
}
