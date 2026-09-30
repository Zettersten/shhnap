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
using Windows.UI.Text;

namespace Shnapp.App;

/// <summary>The native editor and local library hosted by the main window.</summary>
public sealed partial class MainPage : Page
{
    private AppController? _controller;
    private DocumentEditor? _editor;
    private CanvasBitmap? _original;
    private CanvasRenderTarget? _flattened;
    private CanvasRenderTarget? _dragBase;
    private EditorTool _tool;
    private Guid? _selectedId;
    private ImagePoint? _dragStart;
    private Annotation? _moving;
    private Annotation? _draft;
    private ResizeHandle _resizeHandle;
    private ImageRect? _crop;
    private TextBox? _textBox;
    private Guid? _textEditId;
    private ImagePoint _textOrigin;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _updatingOptions;
    private bool? _narrowInspector;
    private readonly Dictionary<EditorTool, ToolStyle> _toolStyles =
        Enum.GetValues<EditorTool>().ToDictionary(tool => tool, ToolStyle.Defaults);
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
        InitializeFontFamilies();
        EditorSplitView.PaneOpened += (_, _) => InspectorToggle.IsChecked = true;
        EditorSplitView.PaneClosed += (_, _) => InspectorToggle.IsChecked = false;
        ActualThemeChanged += (_, _) => UpdateShapeButtonAppearance();
    }

    internal void Configure(AppController controller) => _controller = controller;

    internal void OpenDocument(ShnappDocument document, CanvasBitmap original)
    {
        ReleaseDocument();
        ResetCanvasView();
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
        RebuildLibraryEntries();
        FocusLibrarySearchIfRequested();
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
        UpdateInspector();
        UpdateCropInspector();
        DrawingCanvas.Invalidate();
    }

    private void UpdateTransform()
    {
        if (_flattened is null)
        {
            return;
        }

        double fit = Math.Max(0.01, Math.Min(1,
            Math.Min(Math.Max(1, DrawingCanvas.ActualWidth - 48) / _flattened.Size.Width,
                Math.Max(1, DrawingCanvas.ActualHeight - 48) / _flattened.Size.Height)));
        if (_viewCanvasWidth == DrawingCanvas.ActualWidth && _viewCanvasHeight == DrawingCanvas.ActualHeight &&
            _viewImageWidth == _flattened.Size.Width && _viewImageHeight == _flattened.Size.Height)
        {
            return;
        }

        _fitScale = fit;
        _scale = _fitScale * _zoomFactor;
        _offsetX = (DrawingCanvas.ActualWidth - _flattened.Size.Width * _scale) / 2;
        _offsetY = (DrawingCanvas.ActualHeight - _flattened.Size.Height * _scale) / 2;
        _viewCanvasWidth = DrawingCanvas.ActualWidth;
        _viewCanvasHeight = DrawingCanvas.ActualHeight;
        _viewImageWidth = _flattened.Size.Width;
        _viewImageHeight = _flattened.Size.Height;
        ClampCanvasPan();
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
        drawing.DrawImage(_dragBase ?? _flattened);
        drawing.Transform = ImageTransform();
        if (_draft is not null)
        {
            if (_draft.Kind == AnnotationKind.Redaction)
            {
                ImageRect viewport = _editor.Current.Viewport;
                int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
                _controller!.Renderer.DrawRedactionPreview(drawing, _dragBase ?? _flattened, _draft,
                    new ImagePoint(viewport.X - padding, viewport.Y - padding));
            }
            else
            {
                ShnappRenderer.DrawAnnotation(drawing, _draft);
            }
        }

        Annotation? selected = _editor.Current.Annotations.FirstOrDefault(a => a.Id == _selectedId);
        if (selected is not null)
        {
            DrawSelection(drawing, _moving is not null ? _draft ?? selected : selected);
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

        if (IsSpaceHeld())
        {
            BeginCanvasPan(args);
            return;
        }

        StopCanvasPan();
        CommitText();
        Point canvasPosition = args.GetCurrentPoint(DrawingCanvas).Position;
        if (SelectedAnnotation() is { } current &&
            HitResizeHandle(current, canvasPosition) is ResizeHandle handle and not ResizeHandle.None)
        {
            _moving = current;
            _resizeHandle = handle;
            _dragStart = ImagePosition(canvasPosition, clamp: true);
            _crop = null;
            DrawingCanvas.Focus(FocusState.Programmatic);
            DrawingCanvas.CapturePointer(args.Pointer);
            args.Handled = true;
            DrawingCanvas.Invalidate();
            return;
        }

        ImagePoint? position = ImagePosition(canvasPosition);
        if (position is not ImagePoint point)
        {
            return;
        }

        DrawingCanvas.Focus(FocusState.Programmatic);
        if (_tool != EditorTool.Crop)
        {
            _crop = null;
        }
        _selectedId = null;
        switch (_tool)
        {
            case EditorTool.Text:
                StartText(point);
                break;
            case EditorTool.Step:
                Annotation step = NewAnnotation(AnnotationKind.Step, point) with
                {
                    StepNumber = _editor.Current.Annotations.Count(a => a.Kind == AnnotationKind.Step) + 1,
                };
                _selectedId = step.Id;
                _editor.AddAnnotation(step);
                break;
            case EditorTool.Select:
                _moving = _editor.Current.Annotations.Reverse().FirstOrDefault(a => HitAnnotation(a, point));
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
                    AnnotationKind kind = _tool switch
                    {
                        EditorTool.Square => AnnotationKind.Rectangle,
                        EditorTool.Circle => AnnotationKind.Ellipse,
                        EditorTool.Arrow => AnnotationKind.Line,
                        _ => Enum.Parse<AnnotationKind>(_tool.ToString()),
                    };
                    _draft = NewAnnotation(kind, point);
                }

                DrawingCanvas.CapturePointer(args.Pointer);
                break;
        }

        UpdateInspector();
        UpdateCropInspector();
        args.Handled = true;
        DrawingCanvas.Invalidate();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_panning)
        {
            MoveCanvasPan(args);
            return;
        }

        if (_dragStart is not ImagePoint start || _editor is null)
        {
            return;
        }

        ImagePoint point = ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position, clamp: true)!.Value;
        if (_moving is not null)
        {
            if (_resizeHandle != ResizeHandle.None)
            {
                _draft = ResizeAnnotation(_moving, _resizeHandle, point, IsShiftHeld());
            }
            else
            {
                Rect bounds = SelectionBounds(_moving);
                ImageRect viewport = _editor.Current.Viewport;
                double dx = point.X - start.X;
                double dy = point.Y - start.Y;
                if (IsShiftHeld())
                {
                    if (_axisLockHorizontal is null && Math.Max(Math.Abs(dx), Math.Abs(dy)) >= 3 / _scale)
                    {
                        _axisLockHorizontal = Math.Abs(dx) >= Math.Abs(dy);
                    }

                    if (_axisLockHorizontal is true) dy = 0;
                    else if (_axisLockHorizontal is false) dx = 0;
                    else dx = dy = 0;
                }
                else
                {
                    _axisLockHorizontal = null;
                }

                dx = ClampMovement(dx, viewport.X - bounds.X, viewport.Right - bounds.Right);
                dy = ClampMovement(dy, viewport.Y - bounds.Y, viewport.Bottom - bounds.Bottom);
                _draft = _moving with
                {
                    Start = new(_moving.Start.X + dx, _moving.Start.Y + dy),
                    End = new(_moving.End.X + dx, _moving.End.Y + dy),
                };
            }

            if (_draft != _moving)
            {
                EnsureDragBase();
            }
        }
        else if (_tool == EditorTool.Crop)
        {
            if (Math.Max(Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y)) * _scale >= 4)
            {
                _crop = CropRectFromDrag(start, point);
                UpdateCropInspector();
            }
        }
        else if (_draft is not null)
        {
            _draft = _draft with { End = _tool is EditorTool.Square or EditorTool.Circle
                ? ConstrainSquare(start, point)
                : point };
        }

        DrawingCanvas.Invalidate();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_panning)
        {
            EndCanvasPan(args);
            return;
        }

        if (_editor is null)
        {
            return;
        }

        DisposeDragBase();
        if (_draft is Annotation annotation)
        {
            if (_moving is not null)
            {
                _editor.UpdateAnnotation(annotation);
            }
            else if (Math.Abs(annotation.End.X - annotation.Start.X) + Math.Abs(annotation.End.Y - annotation.Start.Y) >= 2)
            {
                _selectedId = annotation.Id;
                _editor.AddAnnotation(annotation);
            }
        }

        _draft = null;
        _moving = null;
        _axisLockHorizontal = null;
        _resizeHandle = ResizeHandle.None;
        _dragStart = null;
        DrawingCanvas.ReleasePointerCapture(args.Pointer);
        DrawingCanvas.Invalidate();
        args.Handled = true;
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs args) => CancelInteraction();

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_panning || _dragStart is not null)
        {
            CancelInteraction();
        }
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (_editor is null || _tool != EditorTool.Select || ImagePosition(args.GetPosition(DrawingCanvas)) is not ImagePoint point)
        {
            return;
        }

        Annotation? text = _editor.Current.Annotations.Reverse().FirstOrDefault(annotation =>
            annotation.Kind == AnnotationKind.Text && HitBounds(annotation).Contains(new Point(point.X, point.Y)));
        if (text is null)
        {
            return;
        }

        CancelInteraction();
        _selectedId = text.Id;
        StartText(text.Start, text);
        UpdateInspector();
        args.Handled = true;
    }

    private ImagePoint ConstrainSquare(ImagePoint start, ImagePoint point)
    {
        ImageRect viewport = _editor!.Current.Viewport;
        double dx = point.X - start.X;
        double dy = point.Y - start.Y;
        double directionX = dx < 0 ? -1 : 1;
        double directionY = dy < 0 ? -1 : 1;
        double edgeX = directionX < 0 ? start.X - viewport.X : viewport.Right - start.X;
        double edgeY = directionY < 0 ? start.Y - viewport.Y : viewport.Bottom - start.Y;
        double side = Math.Min(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Min(edgeX, edgeY));
        return new(start.X + directionX * side, start.Y + directionY * side);
    }

    private void EditorHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 860;
        if (_narrowInspector == narrow)
        {
            return;
        }

        _narrowInspector = narrow;
        EditorSplitView.DisplayMode = narrow ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        EditorSplitView.IsPaneOpen = !narrow;
        InspectorToggle.IsChecked = !narrow;
    }

    private Annotation NewAnnotation(AnnotationKind kind, ImagePoint point)
    {
        ToolStyle style = _toolStyles[_tool];
        return new Annotation
        {
            Kind = kind,
            Start = point,
            End = point,
            StrokeArgb = style.Primary,
            FillArgb = kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse && style.FillShape
                ? WithOpacity(style.Secondary, style.FillOpacity)
                : 0,
            StrokeWidth = style.StrokeWidth,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            StepDiameter = style.StepDiameter,
            StepTextArgb = style.Secondary,
            StartArrow = style.StartArrow,
            EndArrow = style.EndArrow,
            StartCap = style.StartCap,
            EndCap = style.EndCap,
            LinePattern = style.LinePattern,
            StepLabelFormat = style.StepLabelFormat,
            RedactionMode = style.RedactionMode,
        };
    }

    private static uint WithOpacity(uint color, double percent) =>
        ((uint)Math.Clamp(Math.Round(percent * 255 / 100), 0, 255) << 24) | (color & 0x00FFFFFF);

    private static double FiniteValue(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private Rect HitBounds(Annotation annotation)
    {
        Rect bounds = SelectionBounds(annotation);
        double tolerance = 5 / _scale;
        return new(bounds.X - tolerance, bounds.Y - tolerance, Math.Max(1, bounds.Width) + tolerance * 2,
            Math.Max(1, bounds.Height) + tolerance * 2);
    }

    private void StartText(ImagePoint point, Annotation? existing = null)
    {
        _textOrigin = point;
        _textEditId = existing?.Id;
        Vector2 position = Vector2.Transform(new((float)point.X, (float)point.Y), ImageTransform());
        ToolStyle style = StyleFor(existing, EditorTool.Text);
        _textBox = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            PlaceholderText = "Type here",
            MinWidth = 160,
            MaxWidth = Math.Max(180, DrawingCanvas.ActualWidth - position.X - 24),
            FontFamily = new FontFamily(style.FontFamily),
            FontSize = Math.Max(12, style.FontSize * _scale),
            FontWeight = new FontWeight { Weight = (ushort)style.FontWeight },
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = new SolidColorBrush(ShnappRenderer.FromArgb(style.Primary)),
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
        if (existing is not null)
        {
            _textBox.SelectAll();
        }
    }

    internal void CommitText()
    {
        if (_textBox is null)
        {
            return;
        }

        string text = _textBox.Text.Trim();
        Guid? editId = _textEditId;
        CancelText();
        if (_editor is null)
        {
            return;
        }

        if (editId is Guid id && _editor.Current.Annotations.FirstOrDefault(annotation => annotation.Id == id) is { } existing)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                _selectedId = null;
                _editor.RemoveAnnotation(id);
            }
            else
            {
                _editor.UpdateAnnotation(existing with { Text = text });
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            Annotation annotation = NewAnnotation(AnnotationKind.Text, _textOrigin) with { Text = text };
            _selectedId = annotation.Id;
            _editor.AddAnnotation(annotation);
        }
    }

    private void CancelText()
    {
        TextBox? textBox = _textBox;
        _textBox = null;
        _textEditId = null;
        if (textBox is not null)
        {
            TextOverlay.Children.Remove(textBox);
        }
    }

    private void CancelInteraction()
    {
        StopCanvasPan();
        CancelText();
        _draft = null;
        DisposeDragBase();
        _moving = null;
        _axisLockHorizontal = null;
        _resizeHandle = ResizeHandle.None;
        _crop = null;
        _dragStart = null;
        DrawingCanvas.ReleasePointerCaptures();
        DrawingCanvas.Invalidate();
    }

    private void SetTool(EditorTool tool)
    {
        if (tool == EditorTool.Arrow)
        {
            _toolStyles[EditorTool.Line].StartCap = LineEndCap.None;
            _toolStyles[EditorTool.Line].EndCap = LineEndCap.Triangle;
            _toolStyles[EditorTool.Line].LinePattern = LinePattern.Solid;
            tool = EditorTool.Line;
        }

        CommitText();
        CancelInteraction();
        _tool = tool;
        if (tool == EditorTool.Crop && _editor is not null)
        {
            _crop = _editor.Current.Viewport;
            if (ActiveCropRatio() > 0)
            {
                FitCurrentCropToRatio();
            }
        }
        if (tool != EditorTool.Select)
        {
            _selectedId = null;
        }
        foreach (AppBarToggleButton button in Tools.PrimaryCommands.OfType<AppBarToggleButton>())
        {
            button.IsChecked = string.Equals(button.Tag as string, tool.ToString(), StringComparison.Ordinal);
        }

        ShapesTool.Label = tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle
            ? tool.ToString()
            : "Shapes";
        UpdateShapeButtonAppearance();
        UpdateInspector();
        UpdateCropInspector();
        ViewModel.Status = ToolHint();
        DrawingCanvas.Invalidate();
    }

    private string ToolHint() => _tool switch
    {
        EditorTool.Select => "Select to move · Drag handles to resize · Delete removes",
        EditorTool.Text => "Click to type · Enter finishes",
        EditorTool.Step => "Click to place the next numbered step",
        EditorTool.Redaction => "Drag to obscure an area · Solid fully masks; blur and pixelate soften detail",
        EditorTool.Crop => "Drag or enter a crop · Pick a ratio · Enter confirms · Esc cancels",
        _ => $"Drag to draw a {_tool.ToString().ToLowerInvariant()}",
    };

    private void Tool_Click(object sender, RoutedEventArgs args) => SetTool(Enum.Parse<EditorTool>((string)((AppBarToggleButton)sender).Tag));

    private void ToolMenu_Click(object sender, RoutedEventArgs args) =>
        SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)sender).Tag));

    private void InspectorToggle_Click(object sender, RoutedEventArgs args) =>
        EditorSplitView.IsPaneOpen = InspectorToggle.IsChecked == true;

    private void Options_Changed(object sender, RoutedEventArgs args) => HandleOptionChanged(sender);

    private void NumberOption_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) => HandleOptionChanged(sender);

    private void ColorOption_Changed(ColorPicker sender, ColorChangedEventArgs args) =>
        HandleColorChanged(sender, args.NewColor, commitSelected: false);

    private void ColorFlyout_Closed(object sender, object args)
    {
        if (sender is Flyout { Content: ColorPicker picker })
        {
            HandleColorChanged(picker, picker.Color, commitSelected: true);
        }
    }

    private void UpdateShapeButtonAppearance()
    {
        bool selected = _tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle;
        if (selected)
        {
            ShapesTool.Background = (Brush)Application.Current.Resources["ShnappBlueBrush"];
            ShapesTool.Foreground = (Brush)Application.Current.Resources["ShnappOnBlueBrush"];
        }
        else
        {
            ShapesTool.ClearValue(Control.BackgroundProperty);
            ShapesTool.ClearValue(Control.ForegroundProperty);
        }
    }
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

    private bool EditorInputHasFocus()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBox or ComboBox or NumberBox or ColorPicker or CheckBox ||
                ReferenceEquals(focused, EditorSplitView.Pane))
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorInputHasFocus()) { _controller?.Copy(); args.Handled = true; }
    }

    private void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        _controller?.Export();
        args.Handled = true;
    }

    private void UndoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorInputHasFocus()) { Undo_Click(this, new RoutedEventArgs()); args.Handled = true; }
    }

    private void RedoAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (!EditorInputHasFocus()) { Redo_Click(this, new RoutedEventArgs()); args.Handled = true; }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (EditorInputHasFocus() || _editor is null)
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
            ApplyCurrentCrop();
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
        ResetCanvasView();
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
