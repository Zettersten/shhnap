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
using Windows.UI.ViewManagement;

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
    private ImagePoint? _textDragStart;
    private ImagePoint? _textDragEnd;
    private Annotation? _textDraft;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _updatingOptions;
    private bool? _narrowInspector;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly Dictionary<EditorTool, ToolStyle> _toolStyles =
        Enum.GetValues<EditorTool>().ToDictionary(tool => tool, ToolStyle.Defaults);
    private IReadOnlyList<ShnappSummary> _library = [];

    /// <summary>Gets observable state consumed by compiled XAML bindings.</summary>
    public EditorViewModel ViewModel { get; } = new();

    internal ShnappDocument? Document => _editor?.Current;
    internal CanvasBitmap? Original => _original;
    internal event EventHandler? DocumentChanged;

    /// <summary>Initializes the native page and its compiled controls.</summary>
    public MainPage()
    {
        InitializeComponent();
        InitializeNavigationInput();
        AddHandler(KeyUpEvent, new KeyEventHandler(Page_KeyUp), true);
        InitializeFontFamilies();
        ActualThemeChanged += (_, _) =>
        {
            UpdateShapeButtonAppearance();
            DrawingCanvas.Invalidate();
        };
    }

    internal void Configure(AppController controller) => _controller = controller;

    internal void OpenDocument(ShnappDocument document, CanvasBitmap original)
    {
        CancelTitleRename();
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

    internal void ShowLibrary(IReadOnlyList<ShnappSummary> documents)
    {
        CancelTitleRename();
        ReleaseDocument();
        _library = documents;
        ViewModel.HasDocument = false;
        ViewModel.Title = string.Empty;
        ViewModel.Dimensions = string.Empty;
        UpdateDocumentMetadata();
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

        ViewModel.Title = _editor.Current.Title;
        CanvasRenderTarget rendered = _controller.Renderer.Flatten(_original, _editor.Current);
        _flattened?.Dispose();
        _flattened = rendered;
        ImageRect viewport = _editor.Current.Viewport;
        ViewModel.Dimensions = $"{viewport.Width:0} × {viewport.Height:0} px";
        UpdateDocumentMetadata();
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

        ImageRect viewport = _editor!.Current.Viewport;
        int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
        const double toolbarInset = 64;
        const double bottomInset = 24;
        double availableHeight = Math.Max(1, DrawingCanvas.ActualHeight - toolbarInset - bottomInset);
        double fit = Math.Max(0.01, Math.Min(1,
            Math.Min(Math.Max(1, DrawingCanvas.ActualWidth - 48) / _flattened.Size.Width,
                availableHeight / _flattened.Size.Height)));
        if (_viewCanvasWidth == DrawingCanvas.ActualWidth && _viewCanvasHeight == DrawingCanvas.ActualHeight &&
            _viewImageWidth == _flattened.Size.Width && _viewImageHeight == _flattened.Size.Height)
        {
            _viewViewport = viewport;
            _viewShadowPadding = padding;
            return;
        }

        bool preserveAnchor = _viewViewport is ImageRect &&
            _viewCanvasWidth == DrawingCanvas.ActualWidth &&
            _viewCanvasHeight == DrawingCanvas.ActualHeight &&
            _viewImageWidth > 0 && _viewImageHeight > 0 && _scale > 0;
        Point anchor = _pendingContentAnchor ??
            new Point(DrawingCanvas.ActualWidth / 2, toolbarInset + availableHeight / 2);
        double sourceX = preserveAnchor
            ? (anchor.X - _offsetX) / _scale - _viewShadowPadding + _viewViewport!.Value.X : 0;
        double sourceY = preserveAnchor
            ? (anchor.Y - _offsetY) / _scale - _viewShadowPadding + _viewViewport!.Value.Y : 0;
        _fitScale = fit;
        _scale = _fitScale * _zoomFactor;
        _offsetX = preserveAnchor
            ? anchor.X - (sourceX - viewport.X + padding) * _scale
            : (DrawingCanvas.ActualWidth - _flattened.Size.Width * _scale) / 2;
        _offsetY = preserveAnchor
            ? anchor.Y - (sourceY - viewport.Y + padding) * _scale
            : toolbarInset + (availableHeight - _flattened.Size.Height * _scale) / 2;
        _viewCanvasWidth = DrawingCanvas.ActualWidth;
        _viewCanvasHeight = DrawingCanvas.ActualHeight;
        _viewImageWidth = _flattened.Size.Width;
        _viewImageHeight = _flattened.Size.Height;
        _viewViewport = viewport;
        _viewShadowPadding = padding;
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

    private ImagePoint? ImagePosition(Point position, bool clamp = false, bool allowOutside = false)
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

        return allowOutside || viewport.Contains(point) ? point : null;
    }

    private void Canvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_flattened is null || _editor is null)
        {
            return;
        }

        UpdateTransform();
        CanvasDrawingSession drawing = args.DrawingSession;
        DrawCanvasBackdrop(drawing, sender.ActualWidth, sender.ActualHeight);
        drawing.Transform = Matrix3x2.CreateScale((float)_scale)
            * Matrix3x2.CreateTranslation((float)_offsetX, (float)_offsetY);
        drawing.DrawImage(_dragBase ?? _flattened);
        drawing.Transform = Matrix3x2.Identity;
        Color canvasEdge = CanvasThemeColor("ShnappCanvasEdgeBrush");
        drawing.DrawRectangle(new Rect(_offsetX, _offsetY,
            _flattened.Size.Width * _scale, _flattened.Size.Height * _scale), canvasEdge, 1);
        drawing.Transform = ImageTransform();
        Annotation? preview = null;
        if (_draft is not null)
        {
            preview = _moving is null ? _draft : _editor.PreviewAnnotation(_draft);
            if (!preview.HiddenByCrop)
            {
                using var clipLayer = preview.VisibilityClip is ImageRect clip
                    ? drawing.CreateLayer(1, ShnappRenderer.ToRect(clip))
                    : null;
                if (preview.Kind == AnnotationKind.Image)
                {
                    _controller!.Renderer.DrawImageAnnotation(drawing, preview);
                }
                else if (preview.Kind == AnnotationKind.Redaction)
                {
                    ImageRect viewport = _editor.Current.Viewport;
                    int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
                    _controller!.Renderer.DrawRedactionPreview(drawing, _dragBase ?? _flattened, preview,
                        new ImagePoint(viewport.X - padding, viewport.Y - padding));
                }
                else
                {
                    ShnappRenderer.DrawAnnotation(drawing, preview);
                }
            }
        }

        Annotation? selected = _editor.Current.Annotations.FirstOrDefault(a => a.Id == _selectedId);
        if (selected is not null)
        {
            DrawSelection(drawing, _moving is not null ? preview ?? selected : selected);
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

        if (_textDragStart is ImagePoint textStart && _textDragEnd is ImagePoint textEnd &&
            Math.Max(Math.Abs(textEnd.X - textStart.X), Math.Abs(textEnd.Y - textStart.Y)) * _scale >= 8)
        {
            double left = Math.Min(textStart.X, textEnd.X);
            double top = Math.Min(textStart.Y, textEnd.Y);
            double width = Math.Clamp(Math.Abs(textEnd.X - textStart.X), 48, 12000);
            double height = Math.Clamp(Math.Abs(textEnd.Y - textStart.Y), 24, 12000);
            Rect textFrame = new(left, top, width, height);
            drawing.FillRectangle(textFrame, Color.FromArgb(36, 42, 138, 245));
            drawing.DrawRectangle(textFrame, Colors.DodgerBlue, (float)(2 / _scale));
        }
    }

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        InspectorPanel.MaxHeight = Math.Min(560, Math.Max(0, args.NewSize.Height - 88));
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

        ImagePoint? position = ImagePosition(canvasPosition, allowOutside: _tool == EditorTool.Text);
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
                _textDragStart = point;
                _textDragEnd = point;
                DrawingCanvas.CapturePointer(args.Pointer);
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
                _moving = _editor.Current.OrderedAnnotations.Reverse().FirstOrDefault(a => HitAnnotation(a, point));
                _selectedId = _moving?.Id;
                _dragStart = _moving is null ? null : point;
                if (_moving is not null)
                {
                    DrawingCanvas.CapturePointer(args.Pointer);
                }

                break;
            case EditorTool.Crop:
                _dragStart = point;
                _cropAtDragStart = _crop;
                _cropCanMoveAtDragStart = _cropCanMove;
                _movingCrop = ShouldMoveCrop(point);
                _cropDragChanged = false;
                DrawingCanvas.CapturePointer(args.Pointer);
                UpdateCropHover(canvasPosition, point);
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
        Point canvasPosition = args.GetCurrentPoint(DrawingCanvas).Position;
        if (_panning)
        {
            MoveCanvasPan(args);
            return;
        }

        if (_textDragStart is not null && _editor is not null)
        {
            _textDragEnd = ImagePosition(canvasPosition, allowOutside: true);
            DrawingCanvas.Invalidate();
            return;
        }

        if (_dragStart is not ImagePoint start || _editor is null)
        {
            UpdateCropHover(canvasPosition);
            return;
        }

        ImagePoint point = (_moving?.Kind is AnnotationKind.Image or AnnotationKind.Text
            ? ImagePosition(canvasPosition, allowOutside: true)
            : ImagePosition(canvasPosition, clamp: true))!.Value;
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

                if (_moving.Kind is not (AnnotationKind.Image or AnnotationKind.Text))
                {
                    dx = ClampMovement(dx, viewport.X - bounds.X, viewport.Right - bounds.Right);
                    dy = ClampMovement(dy, viewport.Y - bounds.Y, viewport.Bottom - bounds.Bottom);
                }
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
                _cropDragChanged = true;
                if (_movingCrop)
                {
                    MoveCropFromDrag(start, point);
                }
                else
                {
                    _crop = CropRectFromDrag(start, point);
                    _cropCanMove = true;
                    UpdateCropInspector();
                }
            }
        }
        else if (_draft is not null)
        {
            _draft = _draft with { End = _tool is EditorTool.Square or EditorTool.Circle
                ? ConstrainSquare(start, point)
                : point };
        }

        UpdateCropHover(canvasPosition, point);
        DrawingCanvas.Invalidate();
    }

    private Color CanvasThemeColor(string resourceKey)
    {
        string themeKey = _accessibility.HighContrast ? "HighContrast" :
            ActualTheme == ElementTheme.Light ? "Light" : "Default";
        var theme = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[themeKey];
        return ((SolidColorBrush)theme[resourceKey]).Color;
    }

    private void DrawCanvasBackdrop(CanvasDrawingSession drawing, double width, double height)
    {
        drawing.Transform = Matrix3x2.Identity;
        drawing.FillRectangle(new Rect(0, 0, width, height), CanvasThemeColor("ShnappCanvasBrush"));
        if (_accessibility.HighContrast)
        {
            return;
        }

        const int tileSize = 18;
        Color alternate = CanvasThemeColor("ShnappCheckerAccentBrush");
        int columns = (int)Math.Ceiling(width / tileSize);
        int rows = (int)Math.Ceiling(height / tileSize);
        for (int row = 0; row < rows; row++)
        {
            for (int column = row & 1; column < columns; column += 2)
            {
                drawing.FillRectangle(column * tileSize, row * tileSize,
                    tileSize, tileSize, alternate);
            }
        }
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

        if (_textDragStart is ImagePoint textStart)
        {
            ImagePoint end = ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position,
                allowOutside: true) ?? textStart;
            _textDragStart = null;
            _textDragEnd = null;
            DrawingCanvas.ReleasePointerCapture(args.Pointer);
            bool bounded = Math.Max(Math.Abs(end.X - textStart.X),
                Math.Abs(end.Y - textStart.Y)) * _scale >= 8;
            ImagePoint origin = bounded
                ? new(Math.Min(textStart.X, end.X), Math.Min(textStart.Y, end.Y))
                : textStart;
            double width = bounded ? Math.Clamp(Math.Abs(end.X - textStart.X), 48, 12000) : 0;
            double height = bounded ? Math.Clamp(Math.Abs(end.Y - textStart.Y), 24, 12000) : 0;
            StartText(origin, width: width, height: height);
            DrawingCanvas.Invalidate();
            args.Handled = true;
            return;
        }

        if (_tool == EditorTool.Crop && _dragStart is ImagePoint cropStart &&
            !_movingCrop && _cropDragChanged &&
            ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position, clamp: true) is ImagePoint cropEnd &&
            Math.Max(Math.Abs(cropEnd.X - cropStart.X), Math.Abs(cropEnd.Y - cropStart.Y)) * _scale < 4)
        {
            RestoreCropDrag();
            args.Handled = true;
            return;
        }

        DisposeDragBase();
        if (_draft is Annotation annotation)
        {
            if (_moving is not null)
            {
                bool updated = false;
                try
                {
                    if (annotation.Kind is AnnotationKind.Image or AnnotationKind.Text)
                    {
                        _pendingContentAnchor = args.GetCurrentPoint(DrawingCanvas).Position;
                    }
                    _editor.UpdateAnnotation(annotation);
                    updated = true;
                }
                catch (ArgumentException exception) when (annotation.Kind is AnnotationKind.Image or AnnotationKind.Text)
                {
                    ShowMessage("Annotation cannot expand the canvas", exception.Message);
                }
                finally
                {
                    _pendingContentAnchor = null;
                }

                if (updated && _resizeHandle != ResizeHandle.None && annotation.Kind == AnnotationKind.Step)
                {
                    ToolStyle stepStyle = _toolStyles[EditorTool.Step];
                    stepStyle.StepDiameter = annotation.StepDiameter;
                    stepStyle.FontSize = annotation.FontSize;
                }
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
        _cropAtDragStart = null;
        _movingCrop = false;
        _cropDragChanged = false;
        DrawingCanvas.ReleasePointerCapture(args.Pointer);
        UpdateCropHover(args.GetCurrentPoint(DrawingCanvas).Position);
        DrawingCanvas.Invalidate();
        args.Handled = true;
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_tool == EditorTool.Crop && _dragStart is not null) RestoreCropDrag();
        else CancelInteraction();
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_panning || _dragStart is not null || _textDragStart is not null)
        {
            if (_tool == EditorTool.Crop && _dragStart is not null) RestoreCropDrag();
            else CancelInteraction();
        }
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (_editor is null || _tool != EditorTool.Select || ImagePosition(args.GetPosition(DrawingCanvas)) is not ImagePoint point)
        {
            return;
        }

        Annotation? text = _editor.Current.OrderedAnnotations.Reverse().FirstOrDefault(annotation =>
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
        InspectorPanel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
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
            HideOutline = !style.OutlineShape,
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            TextKerning = style.TextKerning,
            TextLetterSpacing = style.TextLetterSpacing,
            TextTransform = style.TextTransform,
            TextAlignment = style.TextAlignment,
            TextTruncation = style.TextTruncation,
            TextLineHeight = style.TextLineHeight,
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

    private void StartText(ImagePoint point, Annotation? existing = null, double width = 0, double height = 0)
    {
        _textOrigin = point;
        _textEditId = existing?.Id;
        _textDraft = existing ?? NewAnnotation(AnnotationKind.Text, point) with
        {
            TextBoxWidth = width,
            TextBoxHeight = height,
        };
        bool fixedBox = _textDraft.TextBoxWidth > 0 && _textDraft.TextBoxHeight > 0;
        ToolStyle style = StyleFor(existing, EditorTool.Text);
        double editScale = fixedBox ? _scale : Math.Max(_scale, 12 / Math.Max(8, style.FontSize));
        Vector2 position = Vector2.Transform(new((float)point.X, (float)point.Y), ImageTransform());
        // Pan just enough to keep the live editor on screen when text begins at
        // the right or bottom edge. The annotation itself stays at the clicked point.
        double visibleEditorWidth = fixedBox
            ? Math.Min(_textDraft.TextBoxWidth * editScale, Math.Max(0, DrawingCanvas.ActualWidth - 12))
            : 180;
        double visibleEditorHeight = fixedBox
            ? Math.Min(_textDraft.TextBoxHeight * editScale, Math.Max(0, DrawingCanvas.ActualHeight - 12))
            : 80;
        double editorLeftLimit = Math.Max(0, DrawingCanvas.ActualWidth - visibleEditorWidth);
        double editorTopLimit = Math.Max(0, DrawingCanvas.ActualHeight - visibleEditorHeight);
        double shiftX = Math.Max(0, position.X - editorLeftLimit);
        double shiftY = Math.Max(0, position.Y - editorTopLimit);
        if (shiftX > 0 || shiftY > 0)
        {
            _offsetX -= shiftX;
            _offsetY -= shiftY;
            position -= new Vector2((float)shiftX, (float)shiftY);
            DrawingCanvas.Invalidate();
        }
        _textBox = new TextBox
        {
            Text = existing?.Text ?? string.Empty,
            PlaceholderText = "Type here · Ctrl+Enter to finish",
            AcceptsReturn = true,
            TextWrapping = _textDraft.TextBoxWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = (fixedBox ? _textDraft.TextAlignment : TextHorizontalAlignment.Left) switch
            {
                TextHorizontalAlignment.Center => TextAlignment.Center,
                TextHorizontalAlignment.Right => TextAlignment.Right,
                TextHorizontalAlignment.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left,
            },
            MinWidth = fixedBox ? 0 : 64,
            MinHeight = fixedBox ? 0 : 40,
            FontFamily = new FontFamily(style.FontFamily),
            FontSize = style.FontSize * editScale,
            FontWeight = new FontWeight { Weight = (ushort)style.FontWeight },
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = new SolidColorBrush(ShnappRenderer.FromArgb(style.Primary)),
            Padding = fixedBox ? new Thickness(0) : new Thickness(8, 5, 8, 5),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_textBox, "Annotation text");
        Canvas.SetLeft(_textBox, position.X);
        Canvas.SetTop(_textBox, position.Y);
        var finishText = new KeyboardAccelerator
        {
            Key = VirtualKey.Enter,
            Modifiers = VirtualKeyModifiers.Control,
        };
        finishText.Invoked += (_, args) =>
        {
            CommitText();
            args.Handled = true;
        };
        _textBox.KeyboardAccelerators.Add(finishText);
        _textBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && IsKeyHeld(VirtualKey.Control))
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
        _textBox.TextChanged += (_, _) => UpdateTextEditorSize();
        TextBox activeTextBox = _textBox;
        _textBox.LostFocus += (_, _) =>
        {
            // Inspector controls take focus while the text is still being styled.
            // Wait for focus to settle so their change events can edit the draft.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_textBox, activeTextBox) && !FocusIsInTextInspector())
                {
                    CommitText();
                }
            });
        };
        TextOverlay.Children.Add(_textBox);
        UpdateTextEditorSize();
        _textBox.Focus(FocusState.Programmatic);
        if (existing is not null)
        {
            _textBox.SelectAll();
        }

        UpdateInspector();
    }

    private bool FocusIsInTextInspector()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, InspectorPanel) || focused is ColorPicker)
            {
                return true;
            }

            focused = VisualTreeHelper.GetParent(focused);
        }

        return false;
    }

    private void RefreshActiveTextEditorStyle()
    {
        if (_textBox is null || _textDraft is null)
        {
            return;
        }

        Annotation draft = _textDraft;
        bool fixedBox = draft.TextBoxWidth > 0 && draft.TextBoxHeight > 0;
        double editScale = fixedBox ? _scale : Math.Max(_scale, 12 / Math.Max(8, draft.FontSize));
        _textBox.FontFamily = new FontFamily(draft.FontFamily);
        _textBox.FontSize = draft.FontSize * editScale;
        _textBox.FontWeight = new FontWeight { Weight = (ushort)draft.FontWeight };
        _textBox.FontStyle = draft.Italic ? FontStyle.Italic : FontStyle.Normal;
        _textBox.Foreground = new SolidColorBrush(ShnappRenderer.FromArgb(draft.StrokeArgb));
        _textBox.TextAlignment = (fixedBox ? draft.TextAlignment : TextHorizontalAlignment.Left) switch
        {
            TextHorizontalAlignment.Center => TextAlignment.Center,
            TextHorizontalAlignment.Right => TextAlignment.Right,
            TextHorizontalAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        _textBox.TextWrapping = draft.TextBoxWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        UpdateTextEditorSize();
    }

    private void UpdateTextEditorSize()
    {
        if (_textBox is null || _textDraft is null || _controller is null)
        {
            return;
        }

        Annotation preview = _textDraft with { Text = _textBox.Text };
        Rect measured = _controller.Renderer.MeasureTextBounds(preview);
        double editScale = _textBox.FontSize / preview.FontSize;
        if (preview.TextBoxWidth > 0 && preview.TextBoxHeight > 0)
        {
            // The live editor keeps the exact box drawn on the canvas. Overflow
            // stays editable here and is clipped or ellipsized in the finished layer.
            _textBox.Width = Math.Max(1, preview.TextBoxWidth * editScale);
            _textBox.Height = Math.Max(1, preview.TextBoxHeight * editScale);
            return;
        }

        double left = Canvas.GetLeft(_textBox);
        double top = Canvas.GetTop(_textBox);
        double availableWidth = Math.Max(80, DrawingCanvas.ActualWidth - left - 12);
        double availableHeight = Math.Max(60, DrawingCanvas.ActualHeight - top - 12);
        double desiredWidth = (preview.TextBoxWidth > 0 ? preview.TextBoxWidth : measured.Width) * editScale + 24;
        _textBox.Width = Math.Clamp(desiredWidth,
            Math.Min(preview.TextBoxWidth > 0 ? 64 : 120, availableWidth), availableWidth);
        _textBox.Height = Math.Clamp(measured.Height * editScale + 20, 40, availableHeight);
    }

    private Annotation MeasureTextAnnotation(Annotation annotation)
    {
        if (annotation.TextBoxWidth > 0 && annotation.TextBoxHeight > 0)
        {
            return annotation with
            {
                End = new ImagePoint(annotation.Start.X + annotation.TextBoxWidth,
                    annotation.Start.Y + annotation.TextBoxHeight),
            };
        }

        Rect bounds = _controller!.Renderer.MeasureTextBounds(annotation);
        return annotation with
        {
            End = new ImagePoint(Math.Ceiling(bounds.Right), Math.Ceiling(bounds.Bottom)),
        };
    }

    internal void CommitText()
    {
        if (_textBox is null)
        {
            return;
        }

        string text = _textBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        Guid? editId = _textEditId;
        Annotation? draft = _textDraft;
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
                try
                {
                    _editor.UpdateAnnotation(MeasureTextAnnotation((draft ?? existing) with { Text = text }));
                }
                catch (ArgumentException exception)
                {
                    ShowMessage("Text exceeds canvas limits", exception.Message);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            Annotation annotation = MeasureTextAnnotation((draft ?? NewAnnotation(AnnotationKind.Text, _textOrigin))
                with { Text = text });
            try
            {
                _editor.AddAnnotation(annotation);
                _selectedId = annotation.Id;
                UpdateInspector();
                DrawingCanvas.Invalidate();
            }
            catch (ArgumentException exception)
            {
                ShowMessage("Text exceeds canvas limits", exception.Message);
            }
        }
    }

    private void CancelText()
    {
        TextBox? textBox = _textBox;
        _textBox = null;
        _textEditId = null;
        _textDraft = null;
        if (textBox is not null)
        {
            TextOverlay.Children.Remove(textBox);
            ClampCanvasPan();
            DrawingCanvas.Invalidate();
            UpdateInspector();
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
        _cropCanMove = false;
        _cropCanMoveAtDragStart = false;
        _cropAtDragStart = null;
        _movingCrop = false;
        _cropDragChanged = false;
        _dragStart = null;
        _textDragStart = null;
        _textDragEnd = null;
        HideCropTip();
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
                _cropCanMove = _crop != _editor.Current.Viewport;
            }
        }
        UpdateCropCursor(false);
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
        EditorTool.Select => "Select to move · Drag handles to resize · Ctrl+V pastes text or images · Arrow keys nudge 1 px (Shift: 10 px) · Delete removes",
        EditorTool.Text => "Click to type freely · Drag a fixed text box · Enter adds a line · Ctrl+Enter finishes",
        EditorTool.Step => "Click to place the next numbered step",
        EditorTool.Redaction => "Drag to obscure an area · Solid fully masks; blur and pixelate soften detail",
        EditorTool.Crop => "Drag or enter a crop · Pick a ratio · Enter confirms · Esc cancels",
        _ => $"Drag to draw a {_tool.ToString().ToLowerInvariant()}",
    };

    private void Tool_Click(object sender, RoutedEventArgs args) => SetTool(Enum.Parse<EditorTool>((string)((AppBarToggleButton)sender).Tag));

    private void ToolMenu_Click(object sender, RoutedEventArgs args) =>
        SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)sender).Tag));

    private void InspectorToggle_Click(object sender, RoutedEventArgs args) =>
        InspectorPanel.Visibility = InspectorToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

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
    private void Library_ItemClick(object sender, ItemClickEventArgs args)
    {
        if (!_librarySelectionMode && args.ClickedItem is LibraryEntry entry)
        {
            _controller?.OpenDocument(entry.Id);
        }
    }
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
                ReferenceEquals(focused, InspectorPanel))
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
        if (args.Key == VirtualKey.Space && _editor is not null && !EditorInputHasFocus())
        {
            CanvasHost.SetPanCursor(true);
        }

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
        else if (HandleElementActionKeyDown(args))
        {
            return;
        }
        else if (args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down &&
            _dragStart is null && !_panning && !IsKeyHeld(VirtualKey.Menu) &&
            ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), DrawingCanvas) &&
            (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) &
                global::Windows.UI.Core.CoreVirtualKeyStates.Down) == 0)
        {
            int distance = IsShiftHeld() ? 10 : 1;
            (int dx, int dy) = args.Key switch
            {
                VirtualKey.Left => (-distance, 0),
                VirtualKey.Right => (distance, 0),
                VirtualKey.Up => (0, -distance),
                _ => (0, distance),
            };
            args.Handled = NudgeSelected(dx, dy);
        }
        else if (!IsKeyHeld(VirtualKey.Control) && !IsKeyHeld(VirtualKey.Menu))
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

    private void Page_KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Space)
        {
            CanvasHost.SetPanCursor(_panning);
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
