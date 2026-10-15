using System.Collections.Immutable;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Shnapp.Core;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private Storyboard? _inlineTextStyleStoryboard;
    private bool _inlineTextStyleRequested;

    private void TextOverlay_SizeChanged(object sender, SizeChangedEventArgs args) =>
        PositionInlineTextStyleRow();

    private void PositionInlineTextStyleRow()
    {
        if (_textBox is null)
        {
            return;
        }

        const double inset = 8;
        const double gap = 8;
        double viewportWidth = Math.Max(TextOverlay.ActualWidth, DrawingCanvas.ActualWidth);
        double viewportHeight = Math.Max(TextOverlay.ActualHeight, DrawingCanvas.ActualHeight);
        double toolbarWidth = InlineTextStyleRow.Width;
        double toolbarHeight = InlineTextStyleRow.Height;
        double editorLeft = Canvas.GetLeft(_textBox);
        double editorTop = Canvas.GetTop(_textBox);
        double editorHeight = double.IsFinite(_textBox.Height) ? _textBox.Height : 40;

        double left = Math.Clamp(editorLeft, inset, Math.Max(inset, viewportWidth - toolbarWidth - inset));
        double topAbove = editorTop - toolbarHeight - gap;
        double topBelow = editorTop + editorHeight + gap;
        double bottomLimit = Math.Max(inset, viewportHeight - toolbarHeight - inset);
        double top = topAbove >= inset ? topAbove : topBelow <= bottomLimit ? topBelow : inset;
        Canvas.SetLeft(InlineTextStyleRow, left);
        Canvas.SetTop(InlineTextStyleRow, Math.Clamp(top, inset, bottomLimit));
    }

    private void SetInlineTextStyleVisible(bool visible)
    {
        if (_inlineTextStyleRequested == visible)
        {
            return;
        }

        _inlineTextStyleRequested = visible;
        _inlineTextStyleStoryboard?.Stop();
        _inlineTextStyleStoryboard = null;
        var motion = (TranslateTransform)InlineTextStyleRow.RenderTransform;
        InlineTextStyleRow.IsHitTestVisible = visible;

        if (!visible && InlineTextStyleRow.Visibility != Visibility.Visible)
        {
            return;
        }

        if (visible)
        {
            PositionInlineTextStyleRow();
            InlineTextStyleRow.Visibility = Visibility.Visible;
        }

        if (!_surfaceMotionSettings.AnimationsEnabled || _accessibility.HighContrast)
        {
            InlineTextStyleRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            InlineTextStyleRow.Opacity = visible ? 1 : 0;
            motion.Y = 0;
            return;
        }

        InlineTextStyleRow.Opacity = visible ? 0 : 1;
        motion.Y = visible ? -8 : 0;
        int milliseconds = visible ? 150 : 110;
        var easing = new CubicEase { EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn };
        Storyboard storyboard = new();
        Animate(InlineTextStyleRow, "Opacity", visible ? 0 : 1, visible ? 1 : 0);
        Animate(motion, "Y", visible ? -8 : 0, visible ? 0 : 8);
        storyboard.Completed += (_, _) =>
        {
            storyboard.Stop();
            if (!ReferenceEquals(_inlineTextStyleStoryboard, storyboard))
            {
                return;
            }

            InlineTextStyleRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            InlineTextStyleRow.Opacity = visible ? 1 : 0;
            motion.Y = 0;
            _inlineTextStyleStoryboard = null;
        };
        _inlineTextStyleStoryboard = storyboard;
        storyboard.Begin();

        void Animate(DependencyObject target, string property, double from, double to)
        {
            DoubleAnimation animation = new()
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = easing,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
    }

    private static string GetEditorText(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.UseLf, out string text);
        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static void SetEditorText(RichEditBox editor, Annotation annotation)
    {
        editor.Document.SetText(TextSetOptions.None, annotation.Text.Replace('\n', '\r'));
        ApplyEditorCharacterStyles(editor, annotation);
    }

    private static void ApplyEditorCharacterStyles(RichEditBox editor, Annotation annotation)
    {
        string text = GetEditorText(editor);
        int selectionStart = editor.Document.Selection.StartPosition;
        int selectionEnd = editor.Document.Selection.EndPosition;
        ITextCharacterFormat defaults = editor.Document.GetDefaultCharacterFormat();
        defaults.Weight = annotation.FontWeight;
        defaults.FontStyle = annotation.Italic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
        defaults.Underline = UnderlineType.None;
        defaults.Bold = annotation.FontWeight >= 700 ? FormatEffect.On : FormatEffect.Off;
        defaults.Italic = annotation.Italic ? FormatEffect.On : FormatEffect.Off;
        editor.Document.SetDefaultCharacterFormat(defaults);

        if (text.Length > 0)
        {
            var allRange = editor.Document.GetRange(0, text.Length);
            ITextCharacterFormat all = allRange.CharacterFormat;
            all.Weight = annotation.FontWeight;
            all.FontStyle = annotation.Italic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
            all.Underline = UnderlineType.None;
            all.Bold = annotation.FontWeight >= 700 ? FormatEffect.On : FormatEffect.Off;
            all.Italic = annotation.Italic ? FormatEffect.On : FormatEffect.Off;
            allRange.CharacterFormat = all;
        }

        if (!annotation.TextStyleRuns.IsDefaultOrEmpty)
        {
            foreach (TextStyleRun run in annotation.TextStyleRuns)
            {
                int count = Math.Min(run.Length, text.Length - run.Start);
                if (count <= 0)
                {
                    continue;
                }

                var range = editor.Document.GetRange(run.Start, run.Start + count);
                ITextCharacterFormat format = range.CharacterFormat;
                format.Weight = run.Bold ? Math.Max(700, annotation.FontWeight) : Math.Min(600, annotation.FontWeight);
                format.FontStyle = run.Italic ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
                format.Underline = run.Underline ? UnderlineType.Single : UnderlineType.None;
                format.Bold = run.Bold ? FormatEffect.On : FormatEffect.Off;
                format.Italic = run.Italic ? FormatEffect.On : FormatEffect.Off;
                range.CharacterFormat = format;
            }
        }

        editor.Document.Selection.SetRange(Math.Min(selectionStart, text.Length), Math.Min(selectionEnd, text.Length));
    }

    private static ImmutableArray<TextStyleRun> CaptureTextStyleRuns(RichEditBox editor,
        Annotation annotation, string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        bool baseBold = annotation.FontWeight >= 700;
        bool baseItalic = annotation.Italic;
        var result = ImmutableArray.CreateBuilder<TextStyleRun>();
        int start = -1;
        bool runBold = false;
        bool runItalic = false;
        bool runUnderline = false;

        for (int index = 0; index < text.Length; index++)
        {
            ITextCharacterFormat format = editor.Document.GetRange(index, index + 1).CharacterFormat;
            bool bold = format.Weight >= 700;
            bool italic = format.FontStyle == global::Windows.UI.Text.FontStyle.Italic;
            bool underline = format.Underline == UnderlineType.Single;
            bool differs = bold != baseBold || italic != baseItalic || underline;
            bool matchesRun = start >= 0 && bold == runBold && italic == runItalic && underline == runUnderline;
            if (matchesRun)
            {
                continue;
            }

            if (start >= 0)
            {
                result.Add(new TextStyleRun(start, index - start, runBold, runItalic, runUnderline));
                start = -1;
            }

            if (differs)
            {
                start = index;
                runBold = bold;
                runItalic = italic;
                runUnderline = underline;
            }
        }

        if (start >= 0)
        {
            result.Add(new TextStyleRun(start, text.Length - start, runBold, runItalic, runUnderline));
        }

        return result.ToImmutable();
    }

    private Annotation SyncTextDraftFromEditor()
    {
        string text = GetEditorText(_textBox!);
        // RichEditBox can report the control's base weight after focus moves.
        // Keep saved character runs until the text itself changes.
        if (text != _textDraft!.Text)
        {
            _textDraft = _textDraft with
            {
                Text = text,
                TextStyleRuns = CaptureTextStyleRuns(_textBox!, _textDraft, text),
            };
        }

        return _textDraft;
    }

    private void SetEditorFontFamilyPreservingStyles(Microsoft.UI.Xaml.Media.FontFamily family)
    {
        if (_textBox is null || _textDraft is null)
        {
            return;
        }

        Annotation formatting = SyncTextDraftFromEditor();
        _textBox.FontFamily = family;
        ApplyEditorCharacterStyles(_textBox, formatting);
    }

    private void InlineTextStyle_Click(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton button && _textBox is not null && _textDraft is not null)
        {
            ApplyInlineTextStyle(button, button.IsChecked == true);
        }
    }

    private void ToggleInlineTextShortcut(VirtualKey key)
    {
        ToggleButton button = key switch
        {
            VirtualKey.B => InlineBold,
            VirtualKey.I => InlineItalic,
            _ => InlineUnderline,
        };
        if (_textBox is not null && _textDraft is not null)
        {
            bool next = button.IsChecked != true;
            button.IsChecked = next;
            ApplyInlineTextStyle(button, next);
        }
    }

    private void ApplyInlineTextStyle(ToggleButton button, bool enabled)
    {
        if (_textBox is null || _textDraft is null)
        {
            return;
        }

        Annotation draft = SyncTextDraftFromEditor();
        ApplyEditorCharacterStyles(_textBox, draft);
        var selection = _textBox.Document.Selection;
        ITextCharacterFormat format = selection.CharacterFormat;
        if (ReferenceEquals(button, InlineBold))
        {
            format.Weight = enabled ? Math.Max(700, _textDraft.FontWeight) : Math.Min(600, _textDraft.FontWeight);
            format.Bold = enabled ? FormatEffect.On : FormatEffect.Off;
        }
        else if (ReferenceEquals(button, InlineItalic))
        {
            format.FontStyle = enabled ? global::Windows.UI.Text.FontStyle.Italic : global::Windows.UI.Text.FontStyle.Normal;
            format.Italic = enabled ? FormatEffect.On : FormatEffect.Off;
        }
        else if (ReferenceEquals(button, InlineUnderline))
        {
            format.Underline = enabled ? UnderlineType.Single : UnderlineType.None;
        }

        selection.CharacterFormat = format;
        string text = GetEditorText(_textBox);
        _textDraft = draft with { TextStyleRuns = CaptureTextStyleRuns(_textBox, draft, text) };
        _textBox.Focus(FocusState.Programmatic);
        UpdateTextEditorSize();
        RefreshInlineTextStyleButtons();
    }

    private void RefreshInlineTextStyleButtons()
    {
        if (InlineBold is null || InlineItalic is null || InlineUnderline is null)
        {
            return;
        }

        if (_textBox is null)
        {
            InlineBold.IsChecked = false;
            InlineItalic.IsChecked = false;
            InlineUnderline.IsChecked = false;
            return;
        }

        ITextCharacterFormat format = _textBox.Document.Selection.CharacterFormat;
        InlineBold.IsChecked = format.Weight >= 700;
        InlineItalic.IsChecked = format.FontStyle == global::Windows.UI.Text.FontStyle.Italic;
        InlineUnderline.IsChecked = format.Underline == UnderlineType.Single;
    }

    private void EditSelectedText_Click(object sender, RoutedEventArgs args)
    {
        if (_textBox is null && SelectedAnnotation() is { Kind: AnnotationKind.Text, IsFlattened: false } text)
        {
            StartText(text.Start, text);
        }
    }
}
