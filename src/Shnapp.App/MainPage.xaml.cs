using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
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
    private bool _toolbarPointerPressed;
    private ToolbarActionButton? _toolbarRefreshSource;
    private bool _canvasTextRefreshPending;
    private bool _canvasInspectorOpenPending;
    private bool _escapeHandledUntilKeyUp;
    private Guid? _selectedId;
    private ImagePoint? _dragStart;
    private Annotation? _moving;
    private Annotation? _draft;
    private Annotation[] _groupMoving = [];
    private double _groupDragDx;
    private double _groupDragDy;
    private ResizeHandle _resizeHandle;
    private ImageRect? _crop;
    private RichEditBox? _textBox;
    private Guid? _textEditId;
    private ImagePoint _textOrigin;
    private ImagePoint? _textDragStart;
    private ImagePoint? _textDragEnd;
    private bool _placementShiftHeld;
    private Annotation? _textDraft;
    private double _scale = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _updatingOptions;
    private bool? _narrowInspector;
    private readonly AccessibilitySettings _accessibility = new();
    private Microsoft.UI.System.ThemeSettings? _themeSettings;
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
        Tools.AddHandler(PointerEnteredEvent,
            new PointerEventHandler((_, _) => CanvasHost.SuspendCursor()), true);
        Tools.AddHandler(PointerMovedEvent,
            new PointerEventHandler((_, _) => CanvasHost.SuspendCursor()), true);
        Tools.AddHandler(PointerPressedEvent,
            new PointerEventHandler((_, _) => _toolbarPointerPressed = true), true);
        Tools.AddHandler(PointerReleasedEvent,
            new PointerEventHandler((_, _) => _toolbarPointerPressed = false), true);
        Tools.AddHandler(PointerCanceledEvent,
            new PointerEventHandler((_, _) => _toolbarPointerPressed = false), true);
        Tools.AddHandler(PointerExitedEvent,
            new PointerEventHandler(Tools_PointerExited), true);
        foreach (ToolbarActionButton button in Tools.Children.OfType<ToolbarActionButton>())
        {
            button.PointerExited += ToolbarButton_PointerExited;
        }
        InitializeCaptureButtonRims();
        // Page-level shortcuts remain active, but the first one (Ctrl+C) must not
        // appear as a tooltip when the pointer rests on the annotation canvas.
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        DrawingCanvas.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        InitializeNavigationInput();
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(Page_PreviewKeyDown), true);
        AddHandler(KeyUpEvent, new KeyEventHandler(Page_KeyUp), true);
        InitializeFontFamilies();
        ActualThemeChanged += (_, _) =>
        {
            UpdateShapeButtonAppearance();
            DrawingCanvas.Invalidate();
            LayoutLibraryEmptyScene();
            LibraryEmptySparkleHost.Refresh();
        };
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs args)
    {
        if (_themeSettings is null)
        {
            _themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(App.Window.AppWindow.Id);
            _themeSettings.Changed += ThemeSettings_Changed;
        }
        LayoutLibraryEmptyScene();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs args)
    {
        if (_themeSettings is not null)
        {
            _themeSettings.Changed -= ThemeSettings_Changed;
            _themeSettings = null;
        }
    }

    private void ThemeSettings_Changed(Microsoft.UI.System.ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(LayoutLibraryEmptyScene);

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
        AnimateSurfaceEntry(EditorHost);
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
        AnimateSurfaceEntry(LibraryRoot);
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

        RefreshFlattenedImage();
        ViewModel.CanUndo = _editor.CanUndo;
        ViewModel.CanRedo = _editor.CanRedo;
        SetSelectedAnnotations(_selectedIds);
        UpdateTransform();
        DrawingCanvas.Invalidate();
        if (_deferDocumentChromeRefresh)
        {
            _documentChromeRefreshPending = true;
        }
        else
        {
            RefreshDocumentChrome();
        }
    }

    private void RefreshFlattenedImage()
    {
        if (_editor is null || _original is null || _controller is null)
        {
            return;
        }

        ShnappDocument display = _editor.Current;
        if (_textEditId is Guid editId &&
            display.Annotations.FirstOrDefault(annotation => annotation.Id == editId) is { } editing)
        {
            display = display with { Annotations = display.Annotations.RemoveAt(display.Annotations.IndexOf(editing)) };
        }

        CanvasRenderTarget rendered = _controller.Renderer.Flatten(_original, display);
        _flattened?.Dispose();
        _flattened = rendered;
    }

    private void UpdateTransform()
    {
        if (_flattened is null)
        {
            return;
        }

        ImageRect viewport = _editor!.Current.Viewport;
        int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
        Rect viewingArea = CanvasViewingArea();
        double fit = Math.Max(0.01, Math.Min(1,
            Math.Min(viewingArea.Width / _flattened.Size.Width,
                viewingArea.Height / _flattened.Size.Height)));
        if (_viewCanvasWidth == DrawingCanvas.ActualWidth && _viewCanvasHeight == DrawingCanvas.ActualHeight &&
            _viewImageWidth == _flattened.Size.Width && _viewImageHeight == _flattened.Size.Height)
        {
            _viewViewport = viewport;
            _viewShadowPadding = padding;
            UpdateZoomLabel();
            return;
        }

        StopZoomAnimation();
        bool preserveAnchor = _viewViewport is ImageRect &&
            _viewCanvasWidth > 0 && _viewCanvasHeight > 0 &&
            _viewImageWidth > 0 && _viewImageHeight > 0 && _scale > 0;
        Point anchor = _pendingContentAnchor ?? CanvasViewingCenter();
        Rect previousViewingArea = CanvasViewingArea(_viewCanvasWidth, _viewCanvasHeight);
        Point previousAnchor = _pendingContentAnchor ??
            new Point(previousViewingArea.X + previousViewingArea.Width / 2,
                previousViewingArea.Y + previousViewingArea.Height / 2);
        double sourceX = preserveAnchor
            ? (previousAnchor.X - _offsetX) / _scale - _viewShadowPadding + _viewViewport!.Value.X : 0;
        double sourceY = preserveAnchor
            ? (previousAnchor.Y - _offsetY) / _scale - _viewShadowPadding + _viewViewport!.Value.Y : 0;
        _fitScale = fit;
        _scale = _fitScale * _zoomFactor;
        _offsetX = preserveAnchor
            ? anchor.X - (sourceX - viewport.X + padding) * _scale
            : viewingArea.X + (viewingArea.Width - _flattened.Size.Width * _scale) / 2;
        _offsetY = preserveAnchor
            ? anchor.Y - (sourceY - viewport.Y + padding) * _scale
            : viewingArea.Y + (viewingArea.Height - _flattened.Size.Height * _scale) / 2;
        _viewCanvasWidth = DrawingCanvas.ActualWidth;
        _viewCanvasHeight = DrawingCanvas.ActualHeight;
        _viewImageWidth = _flattened.Size.Width;
        _viewImageHeight = _flattened.Size.Height;
        _viewViewport = viewport;
        _viewShadowPadding = padding;
        UpdateRecenterCanvasButton();
        UpdateZoomLabel();
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
        drawing.DrawImage(_dragBase ?? _fontPreviewImage ?? _flattened);
        drawing.Transform = Matrix3x2.Identity;
        Color canvasEdge = CanvasThemeColor("ShnappCanvasEdgeBrush");
        drawing.DrawRectangle(new Rect(_offsetX, _offsetY,
            _flattened.Size.Width * _scale, _flattened.Size.Height * _scale), canvasEdge, 1);
        drawing.Transform = ImageTransform();
        bool groupPreview = _groupMoving.Length > 1 && _dragBase is not null;
        if (groupPreview)
        {
            foreach (Annotation member in _groupMoving)
            {
                DrawMovingPreview(drawing, PreviewGroupMember(member));
            }
        }

        Annotation? preview = null;
        if (_draft is not null && _groupMoving.Length <= 1)
        {
            preview = _moving is null ? _draft : _editor.PreviewAnnotation(_draft);
            DrawMovingPreview(drawing, preview);
        }

        Annotation? selected = _editor.Current.Annotations.FirstOrDefault(a => a.Id == _selectedId);
        DrawSmartGuides(drawing);
        if (HasMultipleSelectedAnnotations)
        {
            DrawGroupSelection(drawing, groupPreview
                ? _groupMoving.Select(PreviewGroupMember).ToArray()
                : SelectedAnnotations(), groupPreview);
        }
        else if (selected is not null)
        {
            DrawSelection(drawing, _moving is not null ? preview ?? selected : selected);
        }

        DrawPhotoCropOverlay(drawing);

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
            ImageRect bounds = TextFrameFromDrag(textStart, textEnd, IsShiftHeld());
            Rect textFrame = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            drawing.FillRectangle(textFrame, Color.FromArgb(36, 42, 138, 245));
            drawing.DrawRectangle(textFrame, Colors.DodgerBlue, (float)(2 / _scale));
        }

        DrawCropTip(drawing);
    }

    private void DrawMovingPreview(CanvasDrawingSession drawing, Annotation preview)
    {
        if (preview.HiddenByCrop || _editor is null)
        {
            return;
        }

        using var clipLayer = preview.VisibilityClip is ImageRect clip
            ? drawing.CreateLayer(1, ShnappRenderer.ToRect(clip))
            : null;
        if (preview.Kind == AnnotationKind.Image)
        {
            _controller!.Renderer.DrawImageAnnotation(drawing, preview);
        }
        else if (preview.Kind == AnnotationKind.Magnify)
        {
            _controller!.Renderer.DrawMagnifyAnnotation(drawing, _original!, preview, _editor!.Current);
        }
        else if (preview.Kind == AnnotationKind.Redaction)
        {
            ImageRect viewport = _editor.Current.Viewport;
            int padding = _editor.Current.HasWindowShadow ? ShnappRenderer.ShadowPadding : 0;
            _controller!.Renderer.DrawRedactionPreview(drawing, _dragBase ?? _flattened!, preview,
                new ImagePoint(viewport.X - padding, viewport.Y - padding));
        }
        else
        {
            ShnappRenderer.DrawAnnotation(drawing, preview);
        }
    }

    private Annotation PreviewGroupMember(Annotation member)
    {
        // A crop masks marks without a stored clip too. Carry that visible region
        // during the drag so the preview matches the committed group movement.
        Annotation visible = _editor?.Current.Crop is ImageRect crop && member.VisibilityClip is null
            ? member with { VisibilityClip = crop }
            : member;
        return ShiftAnnotation(visible, _groupDragDx, _groupDragDy);
    }

    private void DrawCropTip(CanvasDrawingSession drawing)
    {
        if (!_cropTipVisible || _tool != EditorTool.Crop)
        {
            return;
        }

        const float width = 190;
        const float height = 52;
        float left = (float)_cropTipLeft;
        float top = (float)_cropTipTop;
        Color panel = CanvasThemeColor("ShnappPanelBrush");
        Color border = CanvasThemeColor("ShnappCanvasEdgeBrush");
        // Use the panel's actual theme color so the text stays legible in high contrast.
        Color text = _accessibility.HighContrast ? border
            : panel.R * 299 + panel.G * 587 + panel.B * 114 > 128000
                ? Colors.Black : Colors.White;
        Color secondary = _accessibility.HighContrast
            ? text : Color.FromArgb(184, text.R, text.G, text.B);

        drawing.Transform = Matrix3x2.Identity;
        drawing.FillRoundedRectangle(left, top, width, height, 8, 8, panel);
        drawing.DrawRoundedRectangle(left, top, width, height, 8, 8, border, 1);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Variable Text",
            FontSize = 12,
            FontWeight = new FontWeight { Weight = 600 },
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        drawing.DrawText(_cropTipPosition, new Rect(left + 12, top + 7, width - 24, 18), text, format);
        format.FontWeight = new FontWeight { Weight = 400 };
        drawing.DrawText(_cropTipSize, new Rect(left + 12, top + 27, width - 24, 18), secondary, format);
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

        Point canvasPosition = args.GetCurrentPoint(DrawingCanvas).Position;
        StopZoomAnimation();
        ClearSmartGuides();
        if (IsSpaceHeld())
        {
            BeginCanvasPan(args);
            return;
        }

        StopCanvasPan();
        CommitText();
        if (TryStartPhotoCrop(args, canvasPosition)) return;
        if (!IsKeyHeld(VirtualKey.Control) && !HasMultipleSelectedAnnotations &&
            SelectedAnnotation() is { } current &&
            HitResizeHandle(current, canvasPosition) is ResizeHandle handle and not ResizeHandle.None)
        {
            _moving = current;
            _resizeHandle = handle;
            _dragStart = ImagePosition(canvasPosition, allowOutside: true);
            BeginSmartGuideDrag(current);
            if (InspectorToggle.IsChecked != true || InspectorPanel.Visibility != Visibility.Visible)
            {
                QueueCanvasTextRefresh(openInspector: true);
            }
            _crop = null;
            CanvasHost.Focus(FocusState.Programmatic);
            CanvasHost.CapturePointer(args.Pointer);
            args.Handled = true;
            UpdateCanvasElementCursor(canvasPosition);
            DrawingCanvas.Invalidate();
            return;
        }

        ImagePoint? position = ImagePosition(canvasPosition,
            allowOutside: _tool is not (EditorTool.Select or EditorTool.Crop));
        if (position is not ImagePoint point)
        {
            return;
        }

        CanvasHost.Focus(FocusState.Programmatic);
        if (_tool != EditorTool.Crop)
        {
            _crop = null;
        }
        // Picking an existing mark takes priority over placing a new mark, even
        // while a drawing tool is active. The chosen tool stays active so the
        // next press on empty canvas can still create a new annotation.
        Annotation? hit = _editor.Current.Viewport.Contains(point) ? HitTopAnnotation(point) : null;
        if (hit is not null && IsKeyHeld(VirtualKey.Control))
        {
            ToggleAnnotationSelection(hit.Id);
            QueueCanvasTextRefresh();
            UpdateCanvasElementCursor(canvasPosition);
            DrawingCanvas.Invalidate();
            args.Handled = true;
            return;
        }

        bool needsTextRefresh = false;
        bool openInspectorOnExit = false;
        if (hit is not null)
        {
            if (!IsAnnotationSelected(hit.Id))
            {
                SelectOnlyAnnotation(hit.Id);
                needsTextRefresh = true;
            }

            _moving = hit;
            _groupMoving = HasMultipleSelectedAnnotations ? SelectedAnnotations() : [];
            _groupDragDx = _groupDragDy = 0;
            _dragStart = point;
            if (_groupMoving.Length <= 1)
            {
                BeginSmartGuideDrag(hit);
                openInspectorOnExit = InspectorToggle.IsChecked != true ||
                    InspectorPanel.Visibility != Visibility.Visible;
            }
            CanvasHost.CapturePointer(args.Pointer);
        }
        else
        {
            needsTextRefresh = _selectedIds.Count > 0;
            ClearAnnotationSelection();
            switch (_tool)
            {
                case EditorTool.Text:
                    _textDragStart = point;
                    _textDragEnd = point;
                    _placementShiftHeld = IsShiftHeld();
                    CanvasHost.CapturePointer(args.Pointer);
                    break;
                case EditorTool.Step:
                    Annotation step = NewAnnotation(AnnotationKind.Step, point) with
                    {
                        StepNumber = _editor.Current.Annotations.Count(a => a.Kind == AnnotationKind.Step) + 1,
                    };
                    try
                    {
                        RunWithDeferredDocumentChromeRefresh(() => _editor.AddAnnotation(step));
                        SelectOnlyAnnotation(step.Id);
                        needsTextRefresh = true;
                    }
                    catch (ArgumentException exception)
                    {
                        ShowAnnotationEditError(exception);
                    }
                    break;
                case EditorTool.Callout:
                    _ = AddCalloutAtAsync(point);
                    break;
                case EditorTool.Select:
                    break;
                case EditorTool.Crop:
                    _dragStart = point;
                    _cropAtDragStart = _crop;
                    _cropCanMoveAtDragStart = _cropCanMove;
                    _movingCrop = ShouldMoveCrop(point);
                    _cropDragChanged = false;
                    CanvasHost.CapturePointer(args.Pointer);
                    UpdateCropHover(canvasPosition, point);
                    break;
                default:
                    _dragStart = point;
                    _placementShiftHeld = IsShiftHeld();
                    AnnotationKind kind = _tool switch
                    {
                        EditorTool.Square => AnnotationKind.Rectangle,
                        EditorTool.Circle => AnnotationKind.Ellipse,
                        EditorTool.Arrow => AnnotationKind.Line,
                        EditorTool.Shape => AnnotationKind.Shape,
                        _ => Enum.Parse<AnnotationKind>(_tool.ToString()),
                    };
                    _draft = NewAnnotation(kind, point);
                    if (kind is AnnotationKind.Line or AnnotationKind.Arrow)
                    {
                        _lineTrace.Clear();
                        _lineTrace.Add(point);
                    }
                    CanvasHost.CapturePointer(args.Pointer);
                    break;
            }
        }

        if (needsTextRefresh || openInspectorOnExit)
        {
            QueueCanvasTextRefresh(openInspectorOnExit);
        }
        if (_tool == EditorTool.Crop) UpdateCropInspector();
        UpdateCanvasElementCursor(canvasPosition);
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

        if (TryUpdatePhotoCrop(canvasPosition))
        {
            args.Handled = true;
            return;
        }

        if (IsSpaceHeld())
        {
            UpdatePanCursor();
        }

        UpdateCanvasElementCursor(canvasPosition);
        if (_textDragStart is not null && _editor is not null)
        {
            _placementShiftHeld = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0 || IsShiftHeld();
            _textDragEnd = ImagePosition(canvasPosition, allowOutside: true);
            DrawingCanvas.Invalidate();
            return;
        }

        if (_dragStart is not ImagePoint start || _editor is null)
        {
            UpdateCropHover(canvasPosition);
            return;
        }

        bool cropping = _tool == EditorTool.Crop && _moving is null;
        ImagePoint point = ImagePosition(canvasPosition,
            clamp: cropping, allowOutside: !cropping)!.Value;
        if (_moving is not null)
        {
            if (_resizeHandle != ResizeHandle.None)
            {
                _draft = ResizeWithSmartGuides(_moving, _resizeHandle, point, IsShiftHeld());
            }
            else
            {
                double dx = point.X - start.X;
                double dy = point.Y - start.Y;
                bool shiftHeld = IsShiftHeld();
                if (shiftHeld)
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

                if (_groupMoving.Length > 1)
                {
                    _groupDragDx = dx;
                    _groupDragDy = dy;
                    _draft = ShiftAnnotation(_moving, dx, dy);
                }
                else
                {
                    (dx, dy) = SnapMovingAnnotation(_moving, dx, dy,
                        !shiftHeld || _axisLockHorizontal is true,
                        !shiftHeld || _axisLockHorizontal is false);
                    _draft = _moving.Kind is AnnotationKind.Line or AnnotationKind.Arrow
                        ? Shnapp.Core.LineGeometry.Translate(_moving, dx, dy)
                        : _moving with
                        {
                            Start = new(_moving.Start.X + dx, _moving.Start.Y + dy),
                            End = new(_moving.End.X + dx, _moving.End.Y + dy),
                        };
                }
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
            _placementShiftHeld = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0 || IsShiftHeld();
            if (_draft.Kind is AnnotationKind.Line or AnnotationKind.Arrow)
            {
                UpdateLineDraftFromPointer(args, point, _placementShiftHeld);
            }
            else
            {
                _draft = _draft with { End = PlacementEnd(start, point, _placementShiftHeld) };
            }
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

        if (TryCompletePhotoCrop(args)) return;

        // Keep the last placement preview when Shift is released immediately after
        // mouse-up but before WinUI dispatches this pointer event.
        bool shiftAtRelease = (args.KeyModifiers & VirtualKeyModifiers.Shift) != 0 ||
            IsShiftHeld() || _placementShiftHeld;
        if (_textDragStart is ImagePoint textStart)
        {
            ImagePoint end = ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position,
                allowOutside: true) ?? textStart;
            _textDragStart = null;
            _textDragEnd = null;
            _placementShiftHeld = false;
            CanvasHost.ReleasePointerCapture(args.Pointer);
            bool bounded = Math.Max(Math.Abs(end.X - textStart.X),
                Math.Abs(end.Y - textStart.Y)) * _scale >= 8;
            ImageRect frame = bounded
                ? TextFrameFromDrag(textStart, end, shiftAtRelease)
                : new ImageRect(textStart.X, textStart.Y, 0, 0);
            StartText(new ImagePoint(frame.X, frame.Y), width: frame.Width, height: frame.Height);
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

        if (_moving is null && _draft is { Kind: AnnotationKind.Line or AnnotationKind.Arrow } &&
            ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position, allowOutside: true) is ImagePoint lineEnd)
        {
            UpdateLineDraftFromPointer(args, lineEnd, shiftAtRelease);
        }
        else if (_moving is null && _draft is not null && _dragStart is ImagePoint placementStart &&
            ImagePosition(args.GetCurrentPoint(DrawingCanvas).Position, allowOutside: true) is ImagePoint placementEnd)
        {
            // Re-evaluate the modifier at release so the committed dimensions match
            // the last preview even if Shift changed after the final pointer move.
            bool dragged = Math.Max(Math.Abs(placementEnd.X - placementStart.X),
                Math.Abs(placementEnd.Y - placementStart.Y)) * _scale >= 2;
            _draft = _draft with
            {
                End = dragged ? PlacementEnd(placementStart, placementEnd, shiftAtRelease) : placementStart,
            };
        }

        DisposeDragBase();
        ClearSmartGuides();
        bool needsTextRefresh = false;
        if (_draft is Annotation annotation)
        {
            if (_moving is not null)
            {
                bool updated = false;
                try
                {
                    _pendingContentAnchor = args.GetCurrentPoint(DrawingCanvas).Position;
                    if (_groupMoving.Length > 1)
                    {
                        if (_groupDragDx != 0 || _groupDragDy != 0)
                        {
                            RunWithDeferredDocumentChromeRefresh(() =>
                                _editor.MoveAnnotations(_groupMoving.Select(member => member.Id).ToArray(),
                                    _groupDragDx, _groupDragDy));
                            updated = true;
                        }
                    }
                    else if (annotation != _moving)
                    {
                        RunWithDeferredDocumentChromeRefresh(() => _editor.UpdateAnnotation(annotation));
                        updated = true;
                    }
                }
                catch (ArgumentException exception)
                {
                    ShowAnnotationEditError(exception);
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

                if (updated && _resizeHandle != ResizeHandle.None)
                {
                    needsTextRefresh = InspectorPresentationChanged(_moving, annotation);
                }
            }
            else if (Math.Abs(annotation.End.X - annotation.Start.X) +
                Math.Abs(annotation.End.Y - annotation.Start.Y) >= 2 ||
                (annotation.Kind is AnnotationKind.Line or AnnotationKind.Arrow) &&
                !annotation.LinePoints.IsDefaultOrEmpty &&
                annotation.Bounds.Width + annotation.Bounds.Height >= 2)
            {
                try
                {
                    _pendingContentAnchor = args.GetCurrentPoint(DrawingCanvas).Position;
                    RunWithDeferredDocumentChromeRefresh(() => _editor.AddAnnotation(annotation));
                    SelectOnlyAnnotation(annotation.Id);
                    needsTextRefresh = true;
                }
                catch (ArgumentException exception)
                {
                    ShowAnnotationEditError(exception);
                }
                finally
                {
                    _pendingContentAnchor = null;
                }
            }
        }

        _draft = null;
        _lineTrace.Clear();
        _placementShiftHeld = false;
        _moving = null;
        _groupMoving = [];
        _groupDragDx = _groupDragDy = 0;
        _axisLockHorizontal = null;
        _resizeHandle = ResizeHandle.None;
        _dragStart = null;
        _cropAtDragStart = null;
        _movingCrop = false;
        _cropDragChanged = false;
        CanvasHost.ReleasePointerCapture(args.Pointer);
        if (_tool == EditorTool.Crop) UpdateCropInspector();
        UpdateCropHover(args.GetCurrentPoint(DrawingCanvas).Position);
        UpdateCanvasElementCursor(args.GetCurrentPoint(DrawingCanvas).Position);
        if (needsTextRefresh)
        {
            QueueCanvasTextRefresh();
        }

        // The interaction is complete; show the selected element's options even
        // when the pointer stays over the canvas after release.
        FlushCanvasTextRefresh();
        DrawingCanvas.Invalidate();
        args.Handled = true;
    }

    private static bool InspectorPresentationChanged(Annotation before, Annotation after)
    {
        if (before.Kind != after.Kind) return true;

        return after.Kind switch
        {
            AnnotationKind.Step => before.StepDiameter != after.StepDiameter || before.FontSize != after.FontSize,
            AnnotationKind.Rectangle or AnnotationKind.Ellipse =>
                DisplayShapeTool(before, ToolFor(before)) != DisplayShapeTool(after, ToolFor(after)),
            AnnotationKind.Text =>
                (before.TextBoxWidth > 0 && before.TextBoxHeight > 0) !=
                (after.TextBoxWidth > 0 && after.TextBoxHeight > 0),
            _ => false,
        };
    }

    private void Canvas_PointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_photoCropMode) { CancelPhotoCrop(); return; }
        if (_tool == EditorTool.Crop && _moving is null && _dragStart is not null) RestoreCropDrag();
        else CancelInteraction();
    }

    private void Canvas_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (_photoCropMode) { CancelPhotoCrop(); return; }
        if (_panning || _dragStart is not null || _textDragStart is not null)
        {
            if (_tool == EditorTool.Crop && _moving is null && _dragStart is not null) RestoreCropDrag();
            else CancelInteraction();
        }
    }

    private void Canvas_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (_editor is null || ImagePosition(args.GetPosition(DrawingCanvas)) is not ImagePoint point)
        {
            return;
        }

        Annotation? callout = _editor.Current.OrderedAnnotations.Reverse().FirstOrDefault(annotation =>
            !annotation.IsFlattened && annotation.Kind == AnnotationKind.Callout &&
            HitAnnotation(annotation, point));
        if (callout is not null)
        {
            CancelInteraction();
            SelectOnlyAnnotation(callout.Id);
            _ = EditCalloutAsync(callout);
            args.Handled = true;
            return;
        }

        Annotation? text = _editor.Current.OrderedAnnotations.Reverse().FirstOrDefault(annotation =>
            !annotation.IsFlattened && annotation.Kind == AnnotationKind.Text &&
            HitBounds(annotation).Contains(new Point(point.X, point.Y)));
        if (text is null)
        {
            return;
        }

        CancelInteraction();
        SelectOnlyAnnotation(text.Id);
        StartText(text.Start, text);
        UpdateInspector();
        args.Handled = true;
    }

    private ImagePoint PlacementEnd(ImagePoint start, ImagePoint point, bool shiftHeld) => _tool switch
    {
        EditorTool.Magnify => MagnifyEnd(start, point, shiftHeld),
        EditorTool.Square or EditorTool.Circle => ConstrainSquare(start, point),
        EditorTool.Shape when _activeShape == BasicShapeKind.Circle || shiftHeld =>
            ConstrainSquare(start, point),
        EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Redaction when shiftHeld =>
            ConstrainSquare(start, point),
        _ => point,
    };

    private static ImageRect TextFrameFromDrag(ImagePoint start, ImagePoint pointer, bool shiftHeld)
    {
        ImagePoint end = shiftHeld ? ConstrainSquare(start, pointer) : pointer;
        double width = Math.Clamp(Math.Abs(end.X - start.X), 48, 12000);
        double height = shiftHeld ? width : Math.Clamp(Math.Abs(end.Y - start.Y), 24, 12000);
        double left = end.X < start.X ? start.X - width : start.X;
        double top = end.Y < start.Y ? start.Y - height : start.Y;
        return new ImageRect(left, top, width, height);
    }

    private static ImagePoint ConstrainSquare(ImagePoint start, ImagePoint point)
    {
        double dx = point.X - start.X;
        double dy = point.Y - start.Y;
        double directionX = dx < 0 ? -1 : 1;
        double directionY = dy < 0 ? -1 : 1;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new(start.X + directionX * side, start.Y + directionY * side);
    }

    private void ShowAnnotationEditError(ArgumentException exception)
    {
        System.Diagnostics.Debug.WriteLine($"Annotation edit rejected: {exception}");
        if (exception.Message.StartsWith("The expanded canvas cannot exceed", StringComparison.Ordinal))
        {
            ShowMessage("Canvas limit reached",
                "Move the element closer and try again. The canvas supports up to 16,384 pixels per side and 64 megapixels.");
        }
        else
        {
            ShowMessage("Element not changed",
                "Adjust the element's properties, size, or position and try again. Your previous edit remains available.");
        }
    }

    private void EditorHost_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < 860;
        if (_narrowInspector == narrow)
        {
            return;
        }

        _narrowInspector = narrow;
        SetInspectorVisibility(!narrow, animate: false);
    }

    private Annotation NewAnnotation(AnnotationKind kind, ImagePoint point)
    {
        ToolStyle style = _toolStyles[_tool];
        bool fillableShape = kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse ||
            kind == AnnotationKind.Shape && BasicShapeGeometry.IsClosed(_activeShape);
        bool openShape = kind == AnnotationKind.Shape && !BasicShapeGeometry.IsClosed(_activeShape);
        return new Annotation
        {
            Kind = kind,
            Start = point,
            End = point,
            StrokeArgb = style.Primary,
            FillArgb = kind == AnnotationKind.Text && style.TextBackgroundEnabled &&
                style.TextBackgroundColor >> 24 != 0
                ? style.TextBackgroundColor
                : fillableShape && style.FillShape && style.Secondary >> 24 != 0
                    ? style.Secondary
                    : 0,
            ShapeKind = kind == AnnotationKind.Shape ? _activeShape : BasicShapeKind.Rectangle,
            TextBorderArgb = kind == AnnotationKind.Text && style.TextBorderEnabled &&
                style.TextBorderColor >> 24 != 0 ? style.TextBorderColor : 0,
            TextBorderWidth = kind == AnnotationKind.Text ? style.TextBorderWidth : 0,
            TextPadding = kind == AnnotationKind.Text ? style.TextPadding : 0,
            StrokeWidth = style.StrokeWidth,
            HideOutline = !openShape && !style.OutlineShape &&
                (!fillableShape || style.FillShape && style.Secondary >> 24 != 0),
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            FontWeight = style.FontWeight,
            Italic = style.Italic,
            TextKerning = style.TextKerning,
            TextLetterSpacing = style.TextLetterSpacing,
            TextTransform = style.TextTransform,
            TextAlignment = style.TextAlignment,
            TextVerticalAlignment = style.TextVerticalAlignment,
            TextTruncation = style.TextTruncation,
            TextLineHeight = style.TextLineHeight,
            StepDiameter = style.StepDiameter,
            StepTextArgb = style.Secondary,
            CalloutTextArgb = style.Secondary,
            TextBoxWidth = kind == AnnotationKind.Callout ? 300 : 0,
            TextBoxHeight = kind == AnnotationKind.Callout ? 76 : 0,
            MagnifyZoom = style.MagnifyZoom,
            MagnifyShape = style.MagnifyShape,
            StartArrow = style.StartArrow,
            EndArrow = style.EndArrow,
            StartCap = style.StartCap,
            EndCap = style.EndCap,
            LinePattern = style.LinePattern,
            StepLabelFormat = style.StepLabelFormat,
            RedactionMode = style.RedactionMode,
        };
    }

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
        if (_fontPreviewImage is not null)
        {
            ClearFontPreview();
        }

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
        // Keep the live editor in the unobstructed viewing area. The annotation
        // itself stays at the clicked point.
        Rect viewingArea = CanvasViewingArea();
        double visibleEditorWidth = fixedBox
            ? Math.Min(_textDraft.TextBoxWidth * editScale, viewingArea.Width)
            : Math.Min(180, viewingArea.Width);
        double visibleEditorHeight = fixedBox
            ? Math.Min(_textDraft.TextBoxHeight * editScale, viewingArea.Height)
            : Math.Min(80, viewingArea.Height);
        double editorLeftLimit = Math.Max(viewingArea.Left, viewingArea.Right - visibleEditorWidth);
        double editorTopLimit = Math.Max(viewingArea.Top, viewingArea.Bottom - visibleEditorHeight);
        double shiftX = position.X < viewingArea.Left
            ? position.X - viewingArea.Left : Math.Max(0, position.X - editorLeftLimit);
        double shiftY = position.Y < viewingArea.Top
            ? position.Y - viewingArea.Top : Math.Max(0, position.Y - editorTopLimit);
        if (shiftX != 0 || shiftY != 0)
        {
            _offsetX -= shiftX;
            _offsetY -= shiftY;
            position -= new Vector2((float)shiftX, (float)shiftY);
            DrawingCanvas.Invalidate();
        }
        _textBox = new RichEditBox
        {
            PlaceholderText = "Type here · Ctrl+Enter to finish",
            AcceptsReturn = true,
            IsSpellCheckEnabled = true,
            // The compact inline toolbar replaces the built-in text selection commands.
            SelectionFlyout = null,
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
            VerticalContentAlignment = fixedBox ? _textDraft.TextVerticalAlignment switch
            {
                TextVerticalAlignment.Middle => VerticalAlignment.Center,
                TextVerticalAlignment.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top,
            } : VerticalAlignment.Top,
        };
        ApplyTextAreaDecoration(_textDraft, editScale);
        SetEditorText(_textBox, _textDraft);
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
            else if (IsKeyHeld(VirtualKey.Control) && args.Key is VirtualKey.B or VirtualKey.I or VirtualKey.U)
            {
                ToggleInlineTextShortcut(args.Key);
                args.Handled = true;
            }
        };
        _textBox.TextChanged += (_, _) =>
        {
            UpdateTextEditorSize();
            RefreshInlineTextStyleButtons();
        };
        _textBox.SelectionChanged += (_, _) => RefreshInlineTextStyleButtons();
        RichEditBox activeTextBox = _textBox;
        _textBox.LostFocus += (_, _) =>
        {
            // Inspector controls take focus while the text is still being styled.
            // Wait for focus to settle so their change events can edit the draft.
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ReferenceEquals(_textBox, activeTextBox) && !FocusIsInTextFormattingControls())
                {
                    CommitText();
                }
            });
        };
        TextOverlay.Children.Add(_textBox);
        if (existing is not null)
        {
            RefreshFlattenedImage();
            DrawingCanvas.Invalidate();
        }

        _textBox.Focus(FocusState.Programmatic);
        // Restore character formatting after the editor joins the visual tree.
        ApplyEditorCharacterStyles(_textBox, _textDraft);
        UpdateTextEditorSize();
        if (existing is not null)
        {
            _textBox.Document.Selection.SetRange(0, existing.Text.Length);
        }

        UpdateInspector();
    }

    private bool FocusIsInTextFormattingControls()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, InspectorPanel) ||
                ReferenceEquals(focused, InlineTextStyleRow) || focused is ColorPicker)
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
        ApplyTextAreaDecoration(draft, editScale);
        _textBox.TextAlignment = (fixedBox ? draft.TextAlignment : TextHorizontalAlignment.Left) switch
        {
            TextHorizontalAlignment.Center => TextAlignment.Center,
            TextHorizontalAlignment.Right => TextAlignment.Right,
            TextHorizontalAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };
        _textBox.TextWrapping = draft.TextBoxWidth > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _textBox.VerticalContentAlignment = fixedBox ? draft.TextVerticalAlignment switch
        {
            TextVerticalAlignment.Middle => VerticalAlignment.Center,
            TextVerticalAlignment.Bottom => VerticalAlignment.Bottom,
            _ => VerticalAlignment.Top,
        } : VerticalAlignment.Top;
        ApplyEditorCharacterStyles(_textBox, draft);
        UpdateTextEditorSize();
    }

    private void ApplyTextAreaDecoration(Annotation text, double editScale)
    {
        if (_textBox is null)
        {
            return;
        }

        _textBox.Background = new SolidColorBrush(ShnappRenderer.FromArgb(text.FillArgb));
        _textBox.BorderBrush = new SolidColorBrush(ShnappRenderer.FromArgb(text.TextBorderArgb));
        _textBox.BorderThickness = text.TextBorderArgb >> 24 == 0
            ? new Thickness(0)
            : new Thickness(Math.Max(0.5, text.TextBorderWidth * editScale));
        double contentInset = ShnappRenderer.TextContentInset(text);
        double borderInset = text.TextBorderArgb >> 24 == 0 ? 0 : text.TextBorderWidth;
        double editorPadding = Math.Max(0, contentInset - borderInset) * editScale;
        _textBox.Padding = new Thickness(editorPadding);
    }

    private void UpdateTextEditorSize()
    {
        if (_textBox is null || _textDraft is null || _controller is null)
        {
            return;
        }

        Annotation preview = SyncTextDraftFromEditor();
        Rect measured = _controller.Renderer.MeasureTextBounds(preview);
        double editScale = _textBox.FontSize / preview.FontSize;
        if (preview.TextBoxWidth > 0 && preview.TextBoxHeight > 0)
        {
            // The live editor keeps the exact box drawn on the canvas. Overflow
            // stays editable here and is clipped or ellipsized in the finished layer.
            _textBox.Width = Math.Max(1, preview.TextBoxWidth * editScale);
            _textBox.Height = Math.Max(1, preview.TextBoxHeight * editScale);
            PositionInlineTextStyleRow();
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
        PositionInlineTextStyleRow();
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

        Guid? editId = _textEditId;
        Annotation draft = SyncTextDraftFromEditor();
        string text = draft.Text;
        CancelText();
        if (_editor is null)
        {
            return;
        }

        if (editId is Guid id && _editor.Current.Annotations.FirstOrDefault(annotation => annotation.Id == id) is { } existing)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                ClearAnnotationSelection();
                _editor.RemoveAnnotation(id);
            }
            else
            {
                try
                {
                    _editor.UpdateAnnotation(MeasureTextAnnotation(draft ?? existing));
                }
                catch (ArgumentException exception)
                {
                    ShowAnnotationEditError(exception);
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            try
            {
                Annotation annotation = MeasureTextAnnotation(draft ?? NewAnnotation(AnnotationKind.Text, _textOrigin)
                    with { Text = text });
                _editor.AddAnnotation(annotation);
                SelectOnlyAnnotation(annotation.Id);
                UpdateInspector();
                DrawingCanvas.Invalidate();
            }
            catch (ArgumentException exception)
            {
                ShowAnnotationEditError(exception);
            }
        }
    }

    private void CancelText()
    {
        RichEditBox? textBox = _textBox;
        bool editedExisting = _textEditId is not null;
        _textBox = null;
        _textEditId = null;
        _textDraft = null;
        if (textBox is not null)
        {
            TextOverlay.Children.Remove(textBox);
            if (editedExisting)
            {
                RefreshFlattenedImage();
            }

            UpdateRecenterCanvasButton();
            DrawingCanvas.Invalidate();
            UpdateInspector();
        }
    }

    private void CancelInteraction()
    {
        _photoCropMode = false;
        _photoCropTargetId = null;
        _photoCropStart = null;
        _photoCropEnd = null;
        StopCanvasPan();
        CancelText();
        _draft = null;
        _lineTrace.Clear();
        _groupMoving = [];
        _groupDragDx = _groupDragDy = 0;
        DisposeDragBase();
        ClearSmartGuides();
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
        _placementShiftHeld = false;
        HideCropTip();
        CanvasHost.ReleasePointerCaptures();
        DrawingCanvas.Invalidate();
    }

    private void SetTool(EditorTool tool, bool deferToolbarChromeRefresh = false,
        bool clearSelectionForUserAction = false)
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
        _canvasTextRefreshPending = false;
        _canvasInspectorOpenPending = false;
        _tool = tool;
        if (!deferToolbarChromeRefresh)
        {
            _toolbarRefreshSource = null;
            FlushDeferredDocumentChromeRefresh();
        }
        if (tool == EditorTool.Crop && _editor is not null)
        {
            _crop = _editor.Current.Viewport;
            if (ActiveCropRatio() > 0)
            {
                FitCurrentCropToRatio();
                _cropCanMove = _crop != _editor.Current.Viewport;
            }
        }
        CanvasHost.SetToolCursor(tool);
        if (clearSelectionForUserAction || tool != EditorTool.Select)
        {
            ClearAnnotationSelection();
        }
        foreach (ToolbarActionButton button in Tools.Children.OfType<ToolbarActionButton>()
            .Where(button => button.Tag is string))
        {
            button.IsSelected = string.Equals(button.Tag as string, tool.ToString(), StringComparison.Ordinal);
        }

        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ShapesTool,
            tool == EditorTool.Shape ? ShapeLabel(_activeShape)
                : tool is EditorTool.Rectangle or EditorTool.Square or EditorTool.Ellipse or EditorTool.Circle
                ? tool.ToString() : "Shapes");
        UpdateShapeButtonAppearance();
        UpdateInspector();
        UpdateCropInspector();
        ViewModel.Status = FooterToolHint();
        DrawingCanvas.Invalidate();
    }

    private void QueueCanvasTextRefresh(bool openInspector = false)
    {
        _canvasTextRefreshPending = true;
        _canvasInspectorOpenPending |= openInspector;
    }

    private void FlushCanvasTextRefresh()
    {
        if (!_canvasTextRefreshPending && !_documentChromeRefreshPending)
        {
            return;
        }

        bool openInspector = _canvasInspectorOpenPending;
        bool updateText = _canvasTextRefreshPending;
        _canvasTextRefreshPending = false;
        _canvasInspectorOpenPending = false;
        if (openInspector)
        {
            OpenInspectorForSelection();
        }

        if (_documentChromeRefreshPending)
        {
            FlushDeferredDocumentChromeRefresh();
        }
        else if (updateText)
        {
            UpdateInspector();
            if (_tool == EditorTool.Crop) UpdateCropInspector();
            else ViewModel.Status = FooterToolHint();
        }
    }

    private string FooterToolHint() => HasMultipleSelectedAnnotations
        ? $"{_selectedIds.Count} elements selected · drag or use arrows to move · Shift constrains drag · Ctrl+D duplicates · Delete removes · Esc clears"
        : _tool switch
    {
        EditorTool.Select => "Select · Ctrl+click multiple · Ctrl+A selects all",
        EditorTool.Text => "Text · click or drag · Shift for a square box",
        EditorTool.Step => "Step · click to place",
        EditorTool.Callout => "Annotate · click a point, then enter text",
        EditorTool.Magnify => "Magnify · drag a lens · Shift for a square lens",
        EditorTool.Redaction => "Redact · drag an area · Shift for square",
        EditorTool.Crop => "Crop · drag a frame",
        _ => $"{_tool} · drag to draw",
    };

    private string ToolHint() => _tool switch
    {
        EditorTool.Select => "Ctrl+click multiple · Ctrl+A selects all · Drag or arrows move · Shift constrains drag · Ctrl+D duplicates · Delete removes · Right-click flattens · Esc clears",
        EditorTool.Text => "Click to type freely · Drag a fixed text box · Shift makes it square · Enter adds a line · Ctrl+Enter finishes",
        EditorTool.Step => "Click to place the next numbered step",
        EditorTool.Callout => "Click a point in the image, then enter text. The numbered note will be arranged outside the image.",
        EditorTool.Magnify => "Drag over the image to draw a lens · Shift makes it square. Adjust zoom and outline in Options.",
        EditorTool.Line or EditorTool.Arrow => "Drag a straight line, hold Shift for horizontal or vertical, or enable Draw freehand. Select a straight line and enable Allow curves to bend it.",
        EditorTool.Shape => $"Drag to draw a {ShapeLabel(_activeShape).ToLowerInvariant()} · Shift locks its proportions",
        EditorTool.Redaction => "Drag to obscure an area · Shift makes it square · Solid fully masks; blur and pixelate soften detail",
        EditorTool.Crop => "Drag or enter a crop · Pick a ratio · Enter confirms · Esc cancels",
        _ => $"Drag to draw a {_tool.ToString().ToLowerInvariant()} · Shift locks its proportions",
    };

    private void Tool_Click(object sender, RoutedEventArgs args)
    {
        bool deferChromeRefresh = _toolbarPointerPressed && sender is ToolbarActionButton;
        if (deferChromeRefresh)
        {
            _toolbarRefreshSource = (ToolbarActionButton)sender;
        }
        SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)sender).Tag), deferChromeRefresh,
            clearSelectionForUserAction: true);
        DrawingCanvas.Focus(FocusState.Programmatic);
    }

    private void ShapesTool_Click(object sender, RoutedEventArgs args)
    {
        if (_selectedIds.Count == 0)
        {
            return;
        }

        ClearAnnotationSelection();
        CanvasHost.SetSelectionCursor(null);
        DrawingCanvas.Invalidate();
        UpdateInspector();
        ViewModel.Status = FooterToolHint();
    }

    private void ToolbarButton_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (!ReferenceEquals(sender, _toolbarRefreshSource) || sender is not ToolbarActionButton button)
        {
            return;
        }

        Point position = args.GetCurrentPoint(button).Position;
        if (position.X >= 0 && position.Y >= 0 &&
            position.X < button.ActualWidth && position.Y < button.ActualHeight)
        {
            return;
        }

        FlushToolbarChromeRefresh();
    }

    private void Tools_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        Point position = args.GetCurrentPoint(Tools).Position;
        if (position.X >= 0 && position.Y >= 0 &&
            position.X < Tools.ActualWidth && position.Y < Tools.ActualHeight)
        {
            return;
        }

        FlushToolbarChromeRefresh();
    }

    private void FlushToolbarChromeRefresh()
    {
        _toolbarRefreshSource = null;
        FlushDeferredDocumentChromeRefresh();
    }

    private void ToolMenu_Click(object sender, RoutedEventArgs args) =>
        SetTool(Enum.Parse<EditorTool>((string)((FrameworkElement)sender).Tag),
            clearSelectionForUserAction: true);

    private void InspectorToggle_Click(object sender, RoutedEventArgs args) =>
        SetInspectorVisibility(InspectorToggle.IsChecked == true, animate: true);

    private void OpenInspectorForSelection()
    {
        if (InspectorToggle.IsChecked != true || InspectorPanel.Visibility != Visibility.Visible)
        {
            SetInspectorVisibility(true, animate: true);
        }
    }

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
        ShapesTool.IsSelected = _tool is EditorTool.Rectangle or EditorTool.Square
            or EditorTool.Ellipse or EditorTool.Circle or EditorTool.Shape;
    }

    private void Library_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        LibraryEntry? entry = args.InRecycleQueue ? null : args.Item as LibraryEntry;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer, entry?.Title ?? string.Empty);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(args.ItemContainer,
            entry is null ? string.Empty : $"Shnapp_{entry.Id:N}");
    }
    private void Capture_Click(object sender, RoutedEventArgs args) => _controller?.Capture(Enum.Parse<CaptureKind>((string)((FrameworkElement)sender).Tag));
    private void Library_Click(object sender, RoutedEventArgs args) => _controller?.OpenLibrary();
    private void Copy_Click(object sender, RoutedEventArgs args) => _controller?.Copy();
    private void Save_Click(object sender, RoutedEventArgs args) => _controller?.Export();
    private void Settings_Click(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, UpdateAvailableButton)) { _controller?.OpenSettingsUpdates(); }
        else { _controller?.OpenSettings(); }
    }
    private void Quit_Click(object sender, RoutedEventArgs args) => _controller?.Quit();
    private void Undo_Click(object sender, RoutedEventArgs args)
    {
        void Undo()
        {
            CommitText();
            CancelInteraction();
            _editor?.Undo();
        }

        if (_toolbarPointerPressed && sender is ToolbarActionButton button)
        {
            _toolbarRefreshSource = button;
            RunWithDeferredDocumentChromeRefresh(Undo);
        }
        else Undo();
    }

    private void Redo_Click(object sender, RoutedEventArgs args)
    {
        void Redo()
        {
            CommitText();
            CancelInteraction();
            _editor?.Redo();
        }

        if (_toolbarPointerPressed && sender is ToolbarActionButton button)
        {
            _toolbarRefreshSource = button;
            RunWithDeferredDocumentChromeRefresh(Redo);
        }
        else Redo();
    }

    private bool EditorInputHasFocus()
    {
        DependencyObject? focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (focused is not null)
        {
            if (focused is TextBox or RichEditBox or ComboBox or NumberBox or ColorPicker or CheckBox ||
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

    private void EscapeAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        HandleEscape();
        args.Handled = true;
    }

    private void Page_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape && !args.KeyStatus.WasKeyDown)
        {
            _escapeHandledUntilKeyUp = false;
        }
    }

    private void HandleEscape()
    {
        if (_escapeHandledUntilKeyUp)
        {
            return;
        }

        if (_editor is null)
        {
            if (_selectedLibraryIds.Count > 0)
            {
                _escapeHandledUntilKeyUp = true;
                RemoveLibrarySelection(_selectedLibraryIds.ToArray());
            }
            else
            {
                _controller?.Hide();
            }
        }
        else if (_tool == EditorTool.Crop)
        {
            _escapeHandledUntilKeyUp = true;
            SetTool(EditorTool.Select);
        }
        else if (_photoCropMode)
        {
            _escapeHandledUntilKeyUp = true;
            CancelPhotoCrop();
        }
        else if (_selectedIds.Count > 0)
        {
            _escapeHandledUntilKeyUp = true;
            CancelInteraction();
            ClearAnnotationSelection();
            UpdateInspector();
            ViewModel.Status = FooterToolHint();
            DrawingCanvas.Invalidate();
        }
        else
        {
            CancelInteraction();
            _controller?.OpenLibrary();
        }
    }

    private void Page_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            HandleEscape();
            args.Handled = true;
            return;
        }

        if (_editor is null && LibraryRoot.Visibility == Visibility.Visible)
        {
            if (args.Key == VirtualKey.A && IsKeyHeld(VirtualKey.Control) &&
                !IsKeyHeld(VirtualKey.Menu) && !LibraryTextInputHasFocus())
            {
                ToggleSelectAllVisibleLibraryEntries();
                args.Handled = true;
            }

            return;
        }

        if (args.Key == VirtualKey.Space && _editor is not null && !EditorInputHasFocus())
        {
            CanvasHost.SetPanCursor(true, _panning);
        }

        if (EditorInputHasFocus() || _editor is null)
        {
            return;
        }

        if (args.Key == VirtualKey.A && IsKeyHeld(VirtualKey.Control) &&
            !IsKeyHeld(VirtualKey.Menu))
        {
            CancelInteraction();
            SetTool(EditorTool.Select);
            SelectAllAnnotations();
            UpdateInspector();
            ViewModel.Status = FooterToolHint();
            DrawingCanvas.Invalidate();
            DrawingCanvas.Focus(FocusState.Programmatic);
            args.Handled = true;
            return;
        }

        if (args.Key == VirtualKey.Enter && _crop is ImageRect crop && crop.Width >= 1 && crop.Height >= 1)
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
                VirtualKey.C => EditorTool.Callout,
                VirtualKey.M => EditorTool.Magnify,
                VirtualKey.A => EditorTool.Arrow,
                VirtualKey.L => EditorTool.Line,
                VirtualKey.R => EditorTool.Shape,
                VirtualKey.E => EditorTool.Ellipse,
                VirtualKey.B => EditorTool.Redaction,
                _ => null,
            };
            if (tool is EditorTool selectedTool)
            {
                SetTool(selectedTool, clearSelectionForUserAction: true);
                args.Handled = true;
            }
        }
    }

    private void Page_KeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.A)
        {
            _librarySelectAllKeyActive = false;
        }

        if (args.Key == VirtualKey.Escape)
        {
            _escapeHandledUntilKeyUp = false;
        }

        if (args.Key == VirtualKey.Space)
        {
            CanvasHost.SetPanCursor(_panning, _panning);
        }
    }

    internal void ShowMessage(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        MessageBar.ActionButton = null;
        MessageBar.Title = title;
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        MessageBar.IsOpen = true;
    }

    internal void ShowAvailableUpdate(string message, bool restartRequired = false)
    {
        MessageBar.Title = restartRequired ? "Shnapp update ready" : "Shnapp update available";
        MessageBar.Message = message;
        MessageBar.Severity = InfoBarSeverity.Informational;
        var action = new Button { Content = "About & Updates" };
        action.Click += (_, _) => _controller?.OpenSettingsUpdates();
        MessageBar.ActionButton = action;
        MessageBar.IsOpen = true;
    }

    internal void SetUpdateAvailability(bool available, bool restartRequired = false, bool checkFailed = false)
    {
        UpdateAvailableButton.IsEnabled = available;
        UpdateAvailableButton.Label = restartRequired ? "Update ready to restart"
            : available ? "Update available"
            : checkFailed ? "Updates unavailable" : "Update Shnapp";
        UpdateOverflowAccessibility();

        if (!available && MessageBar.IsOpen &&
            MessageBar.Title is "Shnapp update available" or "Shnapp update ready")
        {
            MessageBar.IsOpen = false;
            MessageBar.ActionButton = null;
        }
    }

    private void PrimaryCommandBar_Loaded(object sender, RoutedEventArgs args)
    {
        if (FindNamedDescendant(PrimaryCommandBar, "MoreButton") is Button more)
        {
            more.Style = (Style)Resources["QuietOverflowButtonStyle"];
        }

        UpdateOverflowAccessibility();
    }

    private void UpdateOverflowAccessibility()
    {
        if (FindNamedDescendant(PrimaryCommandBar, "MoreButton") is FrameworkElement more)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more,
                UpdateAvailableButton.IsEnabled ? "More options, Shnapp update available" : "More options");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(more,
                UpdateAvailableButton.IsEnabled
                    ? "Open to view the available Shnapp update in About and Updates."
                    : "Open more Shnapp commands.");
        }
    }

    internal void ReleaseDocument()
    {
        ClearFontPreview();
        CancelInteraction();
        ResetCanvasView();
        if (_editor is not null)
        {
            _editor.Changed -= EditorChanged;
        }

        _editor = null;
        ClearAnnotationSelection();
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
