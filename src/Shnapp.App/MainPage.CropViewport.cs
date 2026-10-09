using System.Globalization;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Shnapp.App.Editor;
using Shnapp.Core;
using Windows.Foundation;
using Windows.System;

namespace Shnapp.App;

public sealed partial class MainPage
{
    private double _fitScale = 1;
    private double _zoomFactor = 1;
    private DispatcherTimer? _zoomTimer;
    private double _zoomTargetScale = 1;
    private double _zoomVelocity;
    private Point _zoomAnchor;
    private double _zoomAnchorImageX;
    private double _zoomAnchorImageY;
    private DateTimeOffset _zoomLastTick;
    private double _viewCanvasWidth = double.NaN;
    private double _viewCanvasHeight = double.NaN;
    private double _viewImageWidth = double.NaN;
    private double _viewImageHeight = double.NaN;
    private ImageRect? _viewViewport;
    private int _viewShadowPadding;
    private Point? _pendingContentAnchor;
    private bool _panning;
    private bool _cropTipVisible;
    private string _cropTipPosition = string.Empty;
    private string _cropTipSize = string.Empty;
    private double _cropTipLeft;
    private double _cropTipTop;
    private Point _panLastPoint;
    private Vector2 _panVelocity;
    private DateTimeOffset _panLastSample;
    private DateTimeOffset _panLastMovement;
    private DispatcherTimer? _panTimer;
    private bool? _axisLockHorizontal;
    private bool _updatingCropOptions;
    private double _cropAspectRatio;
    private bool _cropCanMove;
    private bool _cropCanMoveAtDragStart;
    private bool _movingCrop;
    private bool _cropDragChanged;
    private ImageRect? _cropAtDragStart;

    private static bool IsKeyHeld(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) &
            global::Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private static bool IsSpaceHeld() => IsKeyHeld(VirtualKey.Space);
    private static bool IsShiftHeld() => IsKeyHeld(VirtualKey.Shift);

    private void ResetCanvasView()
    {
        StopZoomAnimation();
        StopCanvasPan();
        _zoomFactor = 1;
        _fitScale = 1;
        _scale = 1;
        _offsetX = _offsetY = 0;
        _viewCanvasWidth = _viewCanvasHeight = _viewImageWidth = _viewImageHeight = double.NaN;
        _viewViewport = null;
        _viewShadowPadding = 0;
        _pendingContentAnchor = null;
        UpdateZoomLabel();
    }

    private void Canvas_PointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (_flattened is null || _editor is null)
        {
            return;
        }

        StopCanvasPan();
        CommitText();
        UpdateTransform();
        double delta = args.GetCurrentPoint(DrawingCanvas).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        Point pointer = args.GetCurrentPoint(DrawingCanvas).Position;
        double baseScale = _zoomTimer?.IsEnabled == true ? _zoomTargetScale : _scale;
        QueueCanvasZoom(baseScale * Math.Pow(1.2, delta / 120), pointer);
        args.Handled = true;
    }

    private void ZoomPreset_Click(object sender, RoutedEventArgs args)
    {
        if (_flattened is null || _editor is null || sender is not MenuFlyoutItem item)
        {
            return;
        }

        UpdateTransform();
        string? preset = item.Tag?.ToString();
        double target = preset == "fit" ? _fitScale :
            double.TryParse(preset, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : _scale;
        QueueCanvasZoom(target, new Point(DrawingCanvas.ActualWidth / 2, DrawingCanvas.ActualHeight / 2));
    }

    private double MinimumZoomScale() => Math.Max(0.005, Math.Min(0.1, _fitScale * 0.2));
    private double MaximumZoomScale() => Math.Max(8, _fitScale * 16);

    private void QueueCanvasZoom(double target, Point anchor)
    {
        target = Math.Clamp(target, MinimumZoomScale(), MaximumZoomScale());
        _zoomTargetScale = target;
        _zoomAnchor = anchor;
        _zoomAnchorImageX = (anchor.X - _offsetX) / _scale;
        _zoomAnchorImageY = (anchor.Y - _offsetY) / _scale;
        if (_zoomTimer?.IsEnabled != true)
        {
            _zoomVelocity = 0;
            _zoomLastTick = DateTimeOffset.UtcNow;
            _zoomTimer ??= CreateZoomTimer();
            _zoomTimer.Start();
        }

        ViewModel.Status = "Wheel to zoom · Space to pan";
    }

    private DispatcherTimer CreateZoomTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            if (_flattened is null || _editor is null)
            {
                StopZoomAnimation();
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            double elapsed = Math.Clamp((now - _zoomLastTick).TotalSeconds, 0.001, 0.04);
            _zoomLastTick = now;
            double currentLog = Math.Log(_scale);
            double targetLog = Math.Log(_zoomTargetScale);
            const double frequency = 18;
            const double damping = 0.85;
            _zoomVelocity += (frequency * frequency * (targetLog - currentLog) -
                2 * damping * frequency * _zoomVelocity) * elapsed;
            double next = Math.Clamp(Math.Exp(currentLog + _zoomVelocity * elapsed),
                MinimumZoomScale(), MaximumZoomScale());
            if (Math.Abs(targetLog - Math.Log(next)) < 0.001 && Math.Abs(_zoomVelocity) < 0.02)
            {
                next = _zoomTargetScale;
                StopZoomAnimation();
            }

            _scale = next;
            _zoomFactor = _scale / _fitScale;
            _offsetX = _zoomAnchor.X - _zoomAnchorImageX * _scale;
            _offsetY = _zoomAnchor.Y - _zoomAnchorImageY * _scale;
            ClampCanvasPan();
            UpdateZoomLabel();
            DrawingCanvas.Invalidate();
        };
        return timer;
    }

    private void StopZoomAnimation()
    {
        _zoomTimer?.Stop();
        _zoomVelocity = 0;
    }

    private void UpdateZoomLabel()
    {
        if (ZoomPresetButton is null)
        {
            return;
        }

        string label = $"{_scale * 100:0}%";
        if (!Equals(ZoomPresetButton.Content, label))
        {
            ZoomPresetButton.Content = label;
        }
    }

    private void BeginCanvasPan(PointerRoutedEventArgs args)
    {
        StopZoomAnimation();
        StopCanvasPan();
        CommitText();
        _panning = true;
        UpdatePanCursor();
        _panLastPoint = args.GetCurrentPoint(DrawingCanvas).Position;
        _panLastSample = DateTimeOffset.UtcNow;
        _panLastMovement = _panLastSample;
        _panVelocity = Vector2.Zero;
        HideCropTip();
        DrawingCanvas.Focus(FocusState.Programmatic);
        CanvasHost.CapturePointer(args.Pointer);
        args.Handled = true;
    }

    private void MoveCanvasPan(PointerRoutedEventArgs args)
    {
        Point current = args.GetCurrentPoint(DrawingCanvas).Position;
        double dx = current.X - _panLastPoint.X;
        double dy = current.Y - _panLastPoint.Y;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        double elapsed = Math.Max(1, (now - _panLastSample).TotalMilliseconds);
        _panLastPoint = current;
        _panLastSample = now;
        if (dx != 0 || dy != 0)
        {
            _offsetX += dx;
            _offsetY += dy;
            ClampCanvasPan();
            _panLastMovement = now;
            _panVelocity = new Vector2((float)Math.Clamp(dx / elapsed, -0.9, 0.9),
                (float)Math.Clamp(dy / elapsed, -0.9, 0.9));
            DrawingCanvas.Invalidate();
        }

        args.Handled = true;
    }

    private void EndCanvasPan(PointerRoutedEventArgs args)
    {
        _panning = false;
        UpdatePanCursor();
        CanvasHost.ReleasePointerCapture(args.Pointer);
        if ((DateTimeOffset.UtcNow - _panLastMovement).TotalMilliseconds < 65 &&
            _panVelocity.Length() > 0.12f)
        {
            _panLastSample = DateTimeOffset.UtcNow;
            _panTimer ??= CreatePanTimer();
            _panTimer.Start();
        }

        args.Handled = true;
    }

    private DispatcherTimer CreatePanTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            double elapsed = Math.Clamp((now - _panLastSample).TotalMilliseconds, 1, 50);
            _panLastSample = now;
            double beforeX = _offsetX;
            double beforeY = _offsetY;
            _offsetX += _panVelocity.X * elapsed;
            _offsetY += _panVelocity.Y * elapsed;
            ClampCanvasPan();
            _panVelocity *= (float)Math.Exp(-elapsed / 60);
            DrawingCanvas.Invalidate();
            if (_panVelocity.Length() < 0.08f ||
                (Math.Abs(_offsetX - beforeX) < 0.01 && Math.Abs(_offsetY - beforeY) < 0.01))
            {
                StopCanvasPan();
            }
        };
        return timer;
    }

    private void StopCanvasPan()
    {
        _panTimer?.Stop();
        _panning = false;
        _panVelocity = Vector2.Zero;
        UpdatePanCursor();
    }

    private void UpdatePanCursor() =>
        CanvasHost.SetPanCursor(_editor is not null && (_panning || IsSpaceHeld()) && !EditorInputHasFocus(),
            dragging: _panning);

    private void Canvas_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        Point position = args.GetCurrentPoint(DrawingCanvas).Position;
        UpdatePanCursor();
        UpdateCanvasElementCursor(position);
        UpdateCropHover(position);
    }

    private void Canvas_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        if (_dragStart is null)
        {
            HideCropTip();
        }
    }

    private void HideCropTip()
    {
        if (_cropTipVisible)
        {
            _cropTipVisible = false;
            DrawingCanvas.Invalidate();
        }
    }

    private void UpdateCropHover(Point canvasPosition, ImagePoint? knownPoint = null)
    {
        if (_tool != EditorTool.Crop || _editor is null || _panning || _moving is not null)
        {
            HideCropTip();
            return;
        }

        ImagePoint? imagePoint = knownPoint ?? ImagePosition(canvasPosition);
        if (imagePoint is not ImagePoint point)
        {
            HideCropTip();
            return;
        }

        if (HitTopAnnotation(point) is not null)
        {
            HideCropTip();
            return;
        }

        ImageRect? preview = _dragStart is ImagePoint dragStart && !_movingCrop && !_cropDragChanged
            ? new ImageRect(Math.Round(dragStart.X), Math.Round(dragStart.Y), 0, 0)
            : _cropCanMove ? _crop : null;
        ImagePoint origin = preview is ImageRect crop ? new(crop.X, crop.Y) : point;
        string position = $"X {origin.X:0}   Y {origin.Y:0}";
        string size = preview is ImageRect area
            ? $"W {area.Width:0}   H {area.Height:0} px"
            : "W —   H — px";
        const double width = 190;
        const double height = 52;
        double left = canvasPosition.X + 16;
        double top = canvasPosition.Y + 18;
        if (left + width > DrawingCanvas.ActualWidth - 8) left = canvasPosition.X - width - 16;
        if (top + height > DrawingCanvas.ActualHeight - 8) top = canvasPosition.Y - height - 16;
        left = Math.Clamp(left, 8, Math.Max(8, DrawingCanvas.ActualWidth - width - 8));
        top = Math.Clamp(top, 8, Math.Max(8, DrawingCanvas.ActualHeight - height - 8));
        if (_cropTipVisible && _cropTipPosition == position && _cropTipSize == size &&
            _cropTipLeft == left && _cropTipTop == top)
        {
            return;
        }

        _cropTipVisible = true;
        _cropTipPosition = position;
        _cropTipSize = size;
        _cropTipLeft = left;
        _cropTipTop = top;
        DrawingCanvas.Invalidate();
    }

    private bool ShouldMoveCrop(ImagePoint point) =>
        _cropCanMove && _crop is ImageRect area && area.Contains(point);

    private void MoveCropFromDrag(ImagePoint start, ImagePoint point)
    {
        if (_editor is null || _cropAtDragStart is not ImageRect original)
        {
            return;
        }

        ImageRect viewport = _editor.Current.Viewport;
        double x = Math.Clamp(Math.Round(original.X + point.X - start.X),
            viewport.X, viewport.Right - original.Width);
        double y = Math.Clamp(Math.Round(original.Y + point.Y - start.Y),
            viewport.Y, viewport.Bottom - original.Height);
        _crop = original with { X = x, Y = y };
        UpdateCropInspector();
    }

    private void RestoreCropDrag()
    {
        _crop = _cropAtDragStart;
        _cropCanMove = _cropCanMoveAtDragStart;
        _cropAtDragStart = null;
        _movingCrop = false;
        _cropDragChanged = false;
        _dragStart = null;
        HideCropTip();
        CanvasHost.ReleasePointerCaptures();
        UpdateCropInspector();
        DrawingCanvas.Invalidate();
    }

    private void ClampCanvasPan()
    {
        if (_flattened is null)
        {
            return;
        }

        double imageWidth = _flattened.Size.Width * _scale;
        double imageHeight = _flattened.Size.Height * _scale;
        _offsetX = imageWidth <= DrawingCanvas.ActualWidth
            ? Math.Clamp(_offsetX, 0, DrawingCanvas.ActualWidth - imageWidth)
            : Math.Clamp(_offsetX, DrawingCanvas.ActualWidth - imageWidth, 0);
        _offsetY = imageHeight <= DrawingCanvas.ActualHeight
            ? Math.Clamp(_offsetY, 0, DrawingCanvas.ActualHeight - imageHeight)
            : Math.Clamp(_offsetY, DrawingCanvas.ActualHeight - imageHeight, 0);
    }

    private void UpdateCropInspector()
    {
        // Keep NumberBox layout stable while the crop follows the pointer. The
        // floating readout above shows live values; sync fields on release.
        if (_tool == EditorTool.Crop && _dragStart is not null && _moving is null)
        {
            return;
        }

        if (CropOptionsRow is null)
        {
            return;
        }

        bool visible = _editor is not null && _tool == EditorTool.Crop;
        CropOptionsRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
        {
            return;
        }

        ImageRect viewport = _editor!.Current.Viewport;
        ImageRect crop = _crop ?? viewport;
        _updatingCropOptions = true;
        CropXChoice.Minimum = viewport.X;
        CropYChoice.Minimum = viewport.Y;
        CropXChoice.Maximum = viewport.Right - 1;
        CropYChoice.Maximum = viewport.Bottom - 1;
        CropWidthChoice.Maximum = Math.Max(1, viewport.Right - crop.X);
        CropHeightChoice.Maximum = Math.Max(1, viewport.Bottom - crop.Y);
        CropXChoice.Value = Math.Round(crop.X);
        CropYChoice.Value = Math.Round(crop.Y);
        CropWidthChoice.Value = Math.Max(1, Math.Round(crop.Width));
        CropHeightChoice.Value = Math.Max(1, Math.Round(crop.Height));
        _updatingCropOptions = false;
        ViewModel.Status = $"Crop {crop.X:0}, {crop.Y:0} · {crop.Width:0} × {crop.Height:0} px · Enter to apply";
    }

    private double ActiveCropRatio() => CropLockProportions?.IsChecked == true && _cropAspectRatio > 0
        ? _cropAspectRatio : 0;

    private ImageRect CropRectFromDrag(ImagePoint start, ImagePoint end)
    {
        ImageRect viewport = _editor!.Current.Viewport;
        double anchorX = Math.Round(Math.Clamp(start.X, viewport.X, viewport.Right));
        double anchorY = Math.Round(Math.Clamp(start.Y, viewport.Y, viewport.Bottom));
        double dx = Math.Round(end.X - anchorX);
        double dy = Math.Round(end.Y - anchorY);
        double ratio = ActiveCropRatio();
        if (ratio > 0)
        {
            double signX = dx < 0 ? -1 : 1;
            double signY = dy < 0 ? -1 : 1;
            double width = Math.Abs(dx);
            double height = Math.Abs(dy);
            if (width / ratio >= height) height = width / ratio;
            else width = height * ratio;
            double maxWidth = signX < 0 ? anchorX - viewport.X : viewport.Right - anchorX;
            double maxHeight = signY < 0 ? anchorY - viewport.Y : viewport.Bottom - anchorY;
            double factor = Math.Min(1, Math.Min(maxWidth / Math.Max(1, width), maxHeight / Math.Max(1, height)));
            dx = signX * Math.Round(width * factor);
            dy = signY * Math.Round(height * factor);
        }

        ImageRect crop = ImageRect.FromPoints(new(anchorX, anchorY), new(anchorX + dx, anchorY + dy));
        return ClampCrop(crop, viewport);
    }

    private static ImageRect ClampCrop(ImageRect crop, ImageRect viewport)
    {
        double x = Math.Clamp(Math.Round(crop.X), viewport.X, viewport.Right - 1);
        double y = Math.Clamp(Math.Round(crop.Y), viewport.Y, viewport.Bottom - 1);
        double width = Math.Clamp(Math.Round(crop.Width), 1, viewport.Right - x);
        double height = Math.Clamp(Math.Round(crop.Height), 1, viewport.Bottom - y);
        return new(x, y, width, height);
    }

    private void CropNumber_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingCropOptions || _editor is null || _tool != EditorTool.Crop ||
            CropWidthChoice is null || CropHeightChoice is null || !double.IsFinite(sender.Value))
        {
            return;
        }

        ImageRect viewport = _editor.Current.Viewport;
        ImageRect previous = _crop ?? viewport;
        double x = Math.Clamp(Math.Round(FiniteValue(CropXChoice.Value, previous.X)), viewport.X, viewport.Right - 1);
        double y = Math.Clamp(Math.Round(FiniteValue(CropYChoice.Value, previous.Y)), viewport.Y, viewport.Bottom - 1);
        double width = Math.Max(1, Math.Round(FiniteValue(CropWidthChoice.Value, previous.Width)));
        double height = Math.Max(1, Math.Round(FiniteValue(CropHeightChoice.Value, previous.Height)));
        double ratio = ActiveCropRatio();
        if (ratio > 0)
        {
            if (ReferenceEquals(sender, CropHeightChoice)) width = Math.Max(1, Math.Round(height * ratio));
            else height = Math.Max(1, Math.Round(width / ratio));
            if (width > viewport.Right - x || height > viewport.Bottom - y)
            {
                double factor = Math.Min((viewport.Right - x) / width, (viewport.Bottom - y) / height);
                width = Math.Max(1, Math.Round(width * factor));
                height = Math.Max(1, Math.Round(height * factor));
            }
        }

        _crop = ClampCrop(new(x, y, width, height), viewport);
        _cropCanMove = true;
        UpdateCropInspector();
        DrawingCanvas.Invalidate();
    }

    private void CropLock_Click(object sender, RoutedEventArgs args)
    {
        if (_editor is null)
        {
            return;
        }

        if (CropLockProportions.IsChecked == true)
        {
            ImageRect crop = _crop ?? _editor.Current.Viewport;
            _cropAspectRatio = PresetRatio() is double preset and > 0
                ? preset : crop.Width / Math.Max(1, crop.Height);
        }
        else
        {
            _cropAspectRatio = 0;
            _updatingCropOptions = true;
            CropRatioChoice.SelectedIndex = 0;
            _updatingCropOptions = false;
        }
    }

    private double PresetRatio()
    {
        string? tag = (CropRatioChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        string[]? parts = tag?.Split(':');
        return parts is { Length: 2 } &&
            double.TryParse(parts[0], out double numerator) &&
            double.TryParse(parts[1], out double denominator) && denominator > 0
            ? numerator / denominator : 0;
    }

    private void CropRatio_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingCropOptions || _editor is null || _tool != EditorTool.Crop)
        {
            return;
        }

        double ratio = PresetRatio();
        if (ratio <= 0)
        {
            CropLockProportions.IsChecked = false;
            _cropAspectRatio = 0;
            return;
        }

        CropLockProportions.IsChecked = true;
        _cropAspectRatio = ratio;
        FitCurrentCropToRatio();
        _cropCanMove = _crop != _editor.Current.Viewport;
        UpdateCropInspector();
        DrawingCanvas.Invalidate();
    }

    private void FitCurrentCropToRatio()
    {
        if (_editor is null || _cropAspectRatio <= 0)
        {
            return;
        }

        double ratio = _cropAspectRatio;
        ImageRect viewport = _editor.Current.Viewport;
        ImageRect crop = _crop ?? viewport;
        double width = Math.Max(1, Math.Floor(Math.Min(crop.Width, crop.Height * ratio)));
        double height = Math.Max(1, Math.Floor(width / ratio));
        double x = Math.Round(crop.X + (crop.Width - width) / 2);
        double y = Math.Round(crop.Y + (crop.Height - height) / 2);
        _crop = ClampCrop(new(x, y, width, height), viewport);
    }

    private void ApplyCrop_Click(object sender, RoutedEventArgs args) => ApplyCurrentCrop();

    private void ApplyCurrentCrop()
    {
        if (_editor is null || _crop is not ImageRect crop || crop.Width < 1 || crop.Height < 1)
        {
            return;
        }

        ResetCanvasView();
        _editor.ApplyCrop(crop);
        SetTool(EditorTool.Select);
    }
}
