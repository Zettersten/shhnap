using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Shnapp.App;

/// <summary>The native editor and local library hosted by the main window.</summary>
public sealed partial class MainPage : Page
{
    private AppController? _controller;
    private DocumentEditor? _editor;
    private CanvasBitmap? _original;
    private CanvasRenderTarget? _flattened;
    private EditorTool _tool;
    private Guid? _selectedId;
    private ImagePoint? _dragStart;
    private Annotation? _moving;
    private Annotation? _draft;
    private ImageRect? _crop;
    private TextBox? _textBox;
    private ImagePoint _textOrigin;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _updatingOptions;
    private uint _strokeColor = 0xFFE5484D;
    private uint _stepColor = 0xFF0A84FF;
    private IReadOnlyList<ShnappDocument> _library = [];

    /// <summary>Gets observable state consumed by compiled XAML bindings.</summary>
    public EditorViewModel ViewModel { get; } = new();

    internal ShnappDocument? Document => _editor?.Current;
    internal CanvasBitmap? Original => _original;
    internal event EventHandler? DocumentChanged;

    /// <summary>Initializes the native page and its compiled controls.</summary>
    public MainPage()
    {
        InitializeComponent();
    }

    internal void Configure(AppController controller) => _controller = controller;

    internal void OpenDocument(ShnappDocument document, CanvasBitmap original)
    {
        ReleaseDocument();
        _editor = new DocumentEditor(document);
        _editor.Changed += EditorChanged;
        _original = original;
        ViewModel.HasDocument = true;
        ViewModel.Title = document.Title;
        MessageBar.IsOpen = false;
        SetTool(EditorTool.Select);
        RefreshDocument();
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    internal void ShowLibrary(IReadOnlyList<ShnappDocument> documents)
    {
        ReleaseDocument();
        _library = documents;
        ViewModel.HasDocument = false;
        ViewModel.Title = "Capture first. Think less.";
        ViewModel.Dimensions = string.Empty;
        FilterLibrary();
    }

    private void FilterLibrary()
    {
        if (_controller is null)
        {
            return;
        }

        string query = LibrarySearch.Text?.Trim() ?? string.Empty;
        ViewModel.Library.Clear();
        foreach (ShnappDocument document in _library.Where(d => d.Title.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            ViewModel.Library.Add(new(document, _controller.Library.GetPreviewPath(document.Id)));
        }

        bool hasSavedShnapps = _library.Count > 0;
        LibraryEmpty.Visibility = ViewModel.Library.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryEmptyTitle.Text = hasSavedShnapps
            ? "No shnapps match your search."
            : "Your shnapps will show up here.";
        LibraryEmptyDescription.Text = hasSavedShnapps
            ? "Try another title."
            : "Capture first. Think less.";
        LibraryEmptyCapture.Visibility = hasSavedShnapps ? Visibility.Collapsed : Visibility.Visible;
        LibraryEmptyShortcuts.Visibility = hasSavedShnapps ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EditorChanged(object? sender, EventArgs args)
    {
        RefreshDocument();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDocument()
    {
        if (_editor is null || _original is null || _controller is null)
        {
            return;
        }

        CanvasRenderTarget rendered = _controller.Renderer.Flatten(_original, _editor.Current);
        _flattened?.Dispose();
        _flattened = rendered;
        ImageRect viewport = _editor.Current.Viewport;
        ViewModel.Dimensions = $"{viewport.Width:0} × {viewport.Height:0} px";
        ViewModel.CanUndo = _editor.CanUndo;
        ViewModel.CanRedo = _editor.CanRedo;
        ViewModel.Status = ToolHint();
        UpdateTransform();
        DrawingCanvas.Invalidate();
    }

    private void UpdateTransform()
    {
        if (_flattened is null)
        {
            return;
        }

        _scale = Math.Max(0.01, Math.Min(1,
            Math.Min(Math.Max(1, DrawingCanvas.ActualWidth - 64) / _flattened.Size.Width,
                Math.Max(1, DrawingCanvas.ActualHeight - 104) / _flattened.Size.Height)));
        _offsetX = (DrawingCanvas.ActualWidth - _flattened.Size.Width * _scale) / 2;
        _offsetY = 48 + (DrawingCanvas.ActualHeight - 48 - _flattened.Size.Height * _scale) / 2;
    }

    private Matrix3x2 ImageTransform()
    {
        ImageRect viewport = _editor!.Current.Viewport;
        int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
        return Matrix3x2.CreateTranslation(padding - (float)viewport.X, padding - (float)viewport.Y)
            * Matrix3x2.CreateScale((float)_scale)
            * Matrix3x2.CreateTranslation((float)_offsetX, (float)_offsetY);
    }

    private ImagePoint? ImagePosition(Point position, bool clamp = false)
    {
        if (_editor is null)
        {
            return null;
        }

        ImageRect viewport = _editor.Current.Viewport;
        int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
        var point = new ImagePoint((position.X - _offsetX) / _scale - padding + viewport.X,
            (position.Y - _offsetY) / _scale - padding + viewport.Y);
        if (clamp)
        {
            return new(Math.Clamp(point.X, viewport.X, viewport.Right), Math.Clamp(point.Y, viewport.Y, viewport.Bottom));
        }

        return viewport.Contains(point) ? point : null;
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_flattened is null || _editor is null)
        {
            return;
        }

        UpdateTransform();
        CanvasDrawingSession drawing = args.DrawingSession;
        drawing.Transform = Matrix3x2.CreateScale((float)_scale)
            * Matrix3x2.CreateTranslation((float)_offsetX, (float)_offsetY);
        drawing.DrawImage(_flattened);
        drawing.Transform = ImageTransform();
        if (_draft is not null)
        {
            ShnappRenderer.DrawAnnotation(drawing, _draft);
        }

        Annotation? selected = _editor.Current.Annotations.FirstOrDefault(a => a.Id == _selectedId);
        if (selected is not null && _moving is null)
        {
            drawing.DrawRectangle(HitBounds(selected), Colors.White, (float)(3 / _scale));
            drawing.DrawRectangle(HitBounds(selected), ShnappRenderer.FromArgb(0xFF0A84FF), (float)(1 / _scale));
        }

        if (_crop is ImageRect crop)
        {
            ImageRect viewport = _editor.Current.Viewport;
            Color mask = Color.FromArgb(107, 0, 0, 0);
            drawing.FillRectangle(new Rect(viewport.X, viewport.Y, viewport.Width, crop.Y - viewport.Y), mask);
            drawing.FillRectangle(new Rect(viewport.X, crop.Bottom, viewport.Width, viewport.Bottom - crop.Bottom), mask);
            drawing.FillRectangle(new Rect(viewport.X, crop.Y, crop.X - viewport.X, crop.Height), mask);
            drawing.FillRectangle(new Rect(crop.Right, crop.Y, viewport.Right - crop.Right, crop.Height), mask);
            drawing.DrawRectangle(ShnappRenderer.ToRect(crop), Colors.White, (float)(2 / _scale));
        }
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        CommitText();
        UpdateTransform();
        DrawingCanvas.Invalidate();
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_editor is null || !args.GetCurrentPoint(DrawingCanvas).Properties.IsLeftButtonPressed)
        {
            return;
        }

        CommitText();
        ImagePoint? position = ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position);
        if (position is not ImagePoint point)
        {
            return;
        }

        DrawingCanvas.Focus(FocusState.Programmatic);
        _crop = null;
        _selectedId = null;
        switch (_tool)
        {
            case EditorTool.Text:
                StartText(point);
                break;
            case EditorTool.Step:
                _editor.AddAnnotation(NewAnnotation(AnnotationKind.Step, point) with
                {
                    StrokeArgb = _stepColor,
                    FontSize = Math.Max(10, FiniteValue(StepSize.Value, 28) * 0.45),
                    StepNumber = _editor.Current.Annotations.Count(a => a.Kind == AnnotationKind.Step) + 1,
                });
                break;
            case EditorTool.Select:
                _moving = _editor.Current.Annotations.Reverse().FirstOrDefault(a => HitBounds(a).Contains(new Point(point.X, point.Y)));
                _selectedId = _moving?.Id;
                _dragStart = _moving is null ? null : point;
                if (_moving is not null)
                {
                    DrawingCanvas.CapturePointer(args.Pointer);
                }

                break;
            default:
                _dragStart = point;
                if (_tool != EditorTool.Crop)
                {
                    _draft = NewAnnotation(Enum.Parse<AnnotationKind>(_tool.ToString()), point);
                }

                DrawingCanvas.CapturePointer(args.Pointer);
                break;
        }

        args.Handled = true;
        DrawingCanvas.Invalidate();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_dragStart is not ImagePoint start || _editor is null)
        {
            return;
        }

        ImagePoint point = ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position, clamp: true)!.Value;
        if (_moving is not null)
        {
            double dx = Math.Clamp(point.X - start.X, -Math.Min(_moving.Start.X, _moving.End.X),
                _editor.Current.PixelWidth - Math.Max(_moving.Start.X, _moving.End.X));
            double dy = Math.Clamp(point.Y - start.Y, -Math.Min(_moving.Start.Y, _moving.End.Y),
                _editor.Current.PixelHeight - Math.Max(_moving.Start.Y, _moving.End.Y));
            _draft = _moving with
            {
                Start = new(_moving.Start.X + dx, _moving.Start.Y + dy),
                End = new(_moving.End.X + dx, _moving.End.Y + dy),
            };
        }
        else if (_tool == EditorTool.Crop)
        {
            _crop = ImageRect.FromPoints(start, point);
            ViewModel.Status = $"{_crop.Value.Width:0} × {_crop.Value.Height:0} px · Enter to crop · Esc to cancel";
        }
        else if (_draft is not null)
        {
            _draft = _draft with { End = point };
        }

        DrawingCanvas.Invalidate();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        if (_draft is Annotation annotation)
        {
            if (_moving is not null)
            {
                _editor.UpdateAnnotation(annotation);
            }
            else if (Math.Abs(annotation.End.X - annotation.Start.X) + Math.Abs(annotation.End.Y - annotation.Start.Y) >= 2)
            {
                _editor.AddAnnotation(annotation);
            }
        }

        _draft = null;
        _moving = null;
        _dragStart = null;
        DrawingCanvas.ReleasePointerCapture(args.Pointer);
        DrawingCanvas.Invalidate();
        args.Handled = true;
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs args) => CancelInteraction();

    private Annotation NewAnnotation(AnnotationKind kind, ImagePoint point) => new()
    {
        Kind = kind,
        Start = point,
        End = point,
        StrokeArgb = _strokeColor,
        StrokeWidth = FiniteValue(StrokeSize.Value, 3),
        FontSize = FiniteValue(FontSizeChoice.Value, 18),
        FontWeight = BoldText.IsChecked == true ? 600 : 400,
        StepDiameter = FiniteValue(StepSize.Value, 28),
        FillArgb = FillShape.IsChecked == true ? (_strokeColor & 0x00FFFFFF) | 0x40000000 : 0,
    };

    private static double FiniteValue(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private Rect HitBounds(Annotation annotation)
    {
        Rect bounds = annotation.Kind switch
        {
            AnnotationKind.Text => ShnappRenderer.TextBounds(annotation),
            AnnotationKind.Step => new Rect(annotation.Start.X - annotation.StepDiameter / 2,
                annotation.Start.Y - annotation.StepDiameter / 2, annotation.StepDiameter, annotation.StepDiameter),
            _ => ShnappRenderer.ToRect(annotation.Bounds),
        };
        double tolerance = 5 / _scale;
        return new(bounds.X - tolerance, bounds.Y - tolerance, Math.Max(1, bounds.Width) + tolerance * 2,
            Math.Max(1, bounds.Height) + tolerance * 2);
    }

    private void StartText(ImagePoint point)
    {
        _textOrigin = point;
        Vector2 position = Vector2.Transform(new((float)point.X, (float)point.Y), ImageTransform());
        _textBox = new TextBox
        {
            PlaceholderText = "Type here",
            MinWidth = 160,
            MaxWidth = Math.Max(180, DrawingCanvas.ActualWidth - position.X - 24),
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = Math.Max(12, FiniteValue(FontSizeChoice.Value, 18) * _scale),
            Foreground = new SolidColorBrush(ShnappRenderer.FromArgb(_strokeColor)),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_textBox, "Annotation text");
        Canvas.SetLeft(_textBox, position.X);
        Canvas.SetTop(_textBox, position.Y);
        _textBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                CommitText();
                args.Handled = true;
            }
            else if (args.Key == VirtualKey.Escape)
            {
                CancelText();
                args.Handled = true;
            }
        };
        _textBox.LostFocus += (_, _) => CommitText();
        TextOverlay.Children.Add(_textBox);
        _textBox.Focus(FocusState.Programmatic);
    }

    internal void CommitText()
    {
        if (_textBox is null)
        {
            return;
        }

        string text = _textBox.Text.Trim();
        CancelText();
        if (!string.IsNullOrWhiteSpace(text) && _editor is not null)
        {
            _editor.AddAnnotation(NewAnnotation(AnnotationKind.Text, _textOrigin) with { Text = text });
        }
    }

    private void CancelText()
    {
        TextBox? textBox = _textBox;
        _textBox = null;
        if (textBox is not null)
        {
            TextOverlay.Children.Remove(textBox);
        }
    }

    private void CancelInteraction()
    {
        CancelText();
        _draft = null;
        _moving = null;
        _crop = null;
        _dragStart = null;
        DrawingCanvas.ReleasePointerCaptures();
        DrawingCanvas.Invalidate();
    }

    private void SetTool(EditorTool tool)
    {
        CommitText();
        CancelInteraction();
        _tool = tool;
        _selectedId = null;
        foreach (AppBarToggleButton button in Tools.PrimaryCommands.OfType<AppBarToggleButton>())
        {
            button.IsChecked = string.Equals(button.Tag as string, tool.ToString(), StringComparison.Ordinal);
        }

        _updatingOptions = true;
        uint color = tool == EditorTool.Step ? _stepColor : _strokeColor;
        ColorChoice.SelectedIndex = ColorChoice.Items.Cast<ComboBoxItem>().ToList().FindIndex(item =>
            Convert.ToUInt32(item.Tag as string, 16) == color);
        _updatingOptions = false;
        ViewModel.Status = ToolHint();
        DrawingCanvas.Invalidate();
    }

    private string ToolHint() => _tool switch
    {
        EditorTool.Select => "Select to move · Delete removes · Ctrl+Z undoes",
        EditorTool.Text => "Click to type · Enter finishes",
        EditorTool.Step => "Click to place the next numbered step",
        EditorTool.Redaction => "Drag an opaque redaction · Flattened exports hide covered pixels",
        EditorTool.Crop => "Drag a crop · Enter confirms · Esc cancels",
        _ => $"Drag to draw a {_tool.ToString().ToLowerInvariant()}",
    };

    private void Tool_Click(object sender, RoutedEventArgs args) => SetTool(Enum.Parse<EditorTool>((string)((AppBarToggleButton)sender).Tag));

    private void Options_Changed(object sender, RoutedEventArgs args)
    {
        if (_updatingOptions || ColorChoice?.SelectedItem is not ComboBoxItem choice)
        {
            return;
        }

        uint color = Convert.ToUInt32(choice.Tag as string, 16);
        if (_tool == EditorTool.Step)
        {
            _stepColor = color;
        }
        else
        {
            _strokeColor = color;
        }

        Annotation? selected = _editor?.Current.Annotations.FirstOrDefault(a => a.Id == _selectedId);
        if (selected is not null)
        {
            _editor!.UpdateAnnotation(selected with
            {
                StrokeArgb = color,
                StrokeWidth = FiniteValue(StrokeSize.Value, 3),
                FontSize = FiniteValue(FontSizeChoice.Value, 18),
                StepDiameter = FiniteValue(StepSize.Value, 28),
                FontWeight = BoldText.IsChecked == true ? 600 : 400,
                FillArgb = FillShape.IsChecked == true ? (color & 0x00FFFFFF) | 0x40000000 : 0,
            });
        }
    }

    private void NumberOption_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => Options_Changed(sender, new RoutedEventArgs());
    private void LibrarySearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => FilterLibrary();
    private void Library_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        LibraryEntry? entry = args.InRecycleQueue ? null : args.Item as LibraryEntry;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, entry?.Title ?? string.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer,
            entry is null ? string.Empty : $"Shnapp_{entry.Id:N}");
    }
    private void Library_ItemClick(object sender, ItemClickEventArgs args) => _controller?.OpenDocument(((LibraryEntry)args.ClickedItem).Id);
    private void Capture_Click(object sender, RoutedEventArgs args) => _controller?.Capture(Enum.Parse<CaptureKind>((string)((FrameworkElement)sender).Tag));
    private void Library_Click(object sender, RoutedEventArgs args) => _controller?.OpenLibrary();
    private void Copy_Click(object sender, RoutedEventArgs args) => _controller?.Copy();
    private void Save_Click(object sender, RoutedEventArgs args) => _controller?.Export();
    private void Done_Click(object sender, RoutedEventArgs args) => _controller?.Hide();
    private void Settings_Click(object sender, RoutedEventArgs args) => _controller?.OpenSettings();
    private void Quit_Click(object sender, RoutedEventArgs args) => _controller?.Quit();
    private void Undo_Click(object sender, RoutedEventArgs args) { CancelInteraction(); _editor?.Undo(); }
    private void Redo_Click(object sender, RoutedEventArgs args) { CancelInteraction(); _editor?.Redo(); }

    private bool TextHasFocus() => FocusManager.GetFocusedElement(XamlRoot) is TextBox;

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!TextHasFocus()) { _controller?.Copy(); args.Handled = true; }
    }

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _controller?.Export();
        args.Handled = true;
    }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!TextHasFocus()) { Undo_Click(this, new RoutedEventArgs()); args.Handled = true; }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!TextHasFocus()) { Redo_Click(this, new RoutedEventArgs()); args.Handled = true; }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (TextHasFocus() || _editor is null)
        {
            return;
        }

        if (args.Key == VirtualKey.Escape)
        {
            CancelInteraction();
            SetTool(EditorTool.Select);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Enter && _crop is ImageRect crop && crop.Width >= 1 && crop.Height >= 1)
        {
            _crop = null;
            _editor.ApplyCrop(crop);
            SetTool(EditorTool.Select);
            args.Handled = true;
        }
        else if (args.Key == VirtualKey.Delete && _selectedId is Guid id)
        {
            _selectedId = null;
            _editor.RemoveAnnotation(id);
            args.Handled = true;
        }
        else if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & global::Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
        {
            EditorTool? tool = args.Key switch
            {
                VirtualKey.V => EditorTool.Select,
                VirtualKey.T => EditorTool.Text,
                VirtualKey.N => EditorTool.Step,
                VirtualKey.A => EditorTool.Arrow,
                VirtualKey.L => EditorTool.Line,
                VirtualKey.R => EditorTool.Rectangle,
                VirtualKey.E => EditorTool.Ellipse,
                VirtualKey.B => EditorTool.Redaction,
                _ => null,
            };
            if (tool is EditorTool selectedTool)
            {
                SetTool(selectedTool);
                args.Handled = true;
            }
        }
    }

    internal void ShowMessage(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }

    internal void ReleaseDocument()
    {
        CancelInteraction();
        if (_editor is not null)
        {
            _editor.Changed -= EditorChanged;
        }

        _editor = null;
        _selectedId = null;
        _flattened?.Dispose();
        _flattened = null;
        _original?.Dispose();
        _original = null;
        ViewModel.CanUndo = false;
        ViewModel.CanRedo = false;
    }

    internal void DisposeCanvas()
    {
        ReleaseDocument();
        DrawingCanvas.RemoveFromVisualTree();
    }
}
