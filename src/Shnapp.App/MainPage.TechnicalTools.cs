using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Shnapp.Core;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private static ImagePoint MagnifyEnd(ImagePoint start, ImagePoint pointer, bool maintainProportions)
    {
        double dx = pointer.X - start.X;
        double dy = pointer.Y - start.Y;
        double width = Math.Max(48, Math.Abs(dx));
        double height = Math.Max(48, Math.Abs(dy));
        if (maintainProportions)
        {
            width = height = Math.Max(width, height);
        }

        return new ImagePoint(start.X + Math.CopySign(width, dx == 0 ? 1 : dx),
            start.Y + Math.CopySign(height, dy == 0 ? 1 : dy));
    }

    private async Task AddCalloutAtAsync(ImagePoint anchor)
    {
        DocumentEditor? editor = _editor;
        if (editor is null)
        {
            return;
        }

        Annotation draft = NewAnnotation(AnnotationKind.Callout, anchor);
        string? text = await PromptCalloutTextAsync(null);
        if (text is null || !ReferenceEquals(editor, _editor))
        {
            return;
        }

        try
        {
            Annotation item = draft with { Text = text };
            editor.AddAnnotation(item);
            SelectOnlyAnnotation(item.Id);
            OpenInspectorForSelection();
            UpdateInspector();
            DrawingCanvas.Invalidate();
        }
        catch (ArgumentException exception)
        {
            ShowMessage("Couldn’t place annotation", exception.Message);
        }
    }

    private async Task EditCalloutAsync(Annotation existing)
    {
        DocumentEditor? editor = _editor;
        if (editor is null)
        {
            return;
        }

        string? text = await PromptCalloutTextAsync(existing.Text);
        if (text is null || !ReferenceEquals(editor, _editor) ||
            editor.Current.Annotations.All(item => item.Id != existing.Id))
        {
            return;
        }

        try
        {
            editor.UpdateAnnotation(existing with { Text = text });
            UpdateInspector();
        }
        catch (ArgumentException exception)
        {
            ShowMessage("Couldn’t update annotation", exception.Message);
        }
    }

    private async Task<string?> PromptCalloutTextAsync(string? existing)
    {
        var input = new TextBox
        {
            Text = existing ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 320,
            Height = 160,
            MaxLength = 2_000,
            IsSpellCheckEnabled = true,
            PlaceholderText = "Explain this point…",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "Annotation text");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "Add annotation" : "Edit annotation",
            Content = input,
            PrimaryButtonText = existing is null ? "Add annotation" : "Save annotation",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text),
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(input.Text);
        ContentDialogResult result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return null;
        }

        return input.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Trim();
    }

    private void EditCallout_Click(object sender, RoutedEventArgs args)
    {
        if (SelectedAnnotation() is { Kind: AnnotationKind.Callout } selected)
        {
            _ = EditCalloutAsync(selected);
        }
    }
}
