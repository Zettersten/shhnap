using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media.Effects;
using Windows.Media.MediaProperties;
using Windows.UI;

namespace Shnapp.VideoEffects;

/// <summary>GPU mask for an independently editable video overlay.</summary>
/// <remarks>The effect runs on the overlay clip before the native compositor positions it.</remarks>
public sealed class VideoShapeMaskEffect : IBasicVideoEffect
{
    private CanvasDevice? _device;
    private CanvasGeometry? _mask;
    private float _maskWidth;
    private float _maskHeight;
    private int _shape;
    private double _cropX;
    private double _cropY;
    private double _cropWidth = 1;
    private double _cropHeight = 1;

    public bool IsReadOnly => false;
    public bool TimeIndependent => true;
    public MediaMemoryTypes SupportedMemoryTypes => MediaMemoryTypes.Gpu;

    public IReadOnlyList<VideoEncodingProperties> SupportedEncodingProperties =>
        new[] { new VideoEncodingProperties { Subtype = MediaEncodingSubtypes.Argb32 } };

    public void SetEncodingProperties(VideoEncodingProperties encodingProperties,
        IDirect3DDevice device)
    {
        _mask?.Dispose();
        _mask = null;
        _device?.Dispose();
        _device = CanvasDevice.CreateFromDirect3D11Device(device);
    }

    public void SetProperties(IPropertySet configuration)
    {
        int next = configuration.TryGetValue("Shape", out object? value) &&
            value is int shape && shape is >= 1 and <= 5 ? shape : 0;
        _cropX = GetFraction(configuration, "CropX", 0);
        _cropY = GetFraction(configuration, "CropY", 0);
        _cropWidth = GetFraction(configuration, "CropWidth", 1);
        _cropHeight = GetFraction(configuration, "CropHeight", 1);
        if (next == _shape) return;
        _shape = next;
        _mask?.Dispose();
        _mask = null;
    }

    public void ProcessFrame(ProcessVideoFrameContext context)
    {
        if (_device is null) return;
        using CanvasBitmap input = CanvasBitmap.CreateFromDirect3D11Surface(
            _device, context.InputFrame.Direct3DSurface);
        using CanvasRenderTarget output = CanvasRenderTarget.CreateFromDirect3D11Surface(
            _device, context.OutputFrame.Direct3DSurface);
        using CanvasDrawingSession drawing = output.CreateDrawingSession();
        drawing.Clear(Color.FromArgb(0, 0, 0, 0));
        float width = (float)output.Size.Width;
        float height = (float)output.Size.Height;
        if (_mask is null || _maskWidth != width || _maskHeight != height)
        {
            _mask?.Dispose();
            _mask = _shape switch
            {
                2 or 4 => CanvasGeometry.CreateEllipse(_device,
                    width / 2, height / 2, width / 2, height / 2),
                3 => CreateSquircle(_device, width, height),
                5 => CanvasGeometry.CreateRoundedRectangle(_device,
                    new Rect(0, 0, width, height),
                    Math.Min(width, height) * .12f,
                    Math.Min(width, height) * .12f),
                _ => CanvasGeometry.CreateRectangle(_device,
                    new Rect(0, 0, width, height)),
            };
            _maskWidth = width;
            _maskHeight = height;
        }
        using (drawing.CreateLayer(1, _mask))
        {
            if (_cropX == 0 && _cropY == 0 && _cropWidth == 1 &&
                _cropHeight == 1) drawing.DrawImage(input);
            else drawing.DrawImage(input, new Rect(0, 0, width, height),
                new Rect(_cropX * input.Size.Width,
                    _cropY * input.Size.Height,
                    _cropWidth * input.Size.Width,
                    _cropHeight * input.Size.Height));
        }
    }

    public void DiscardQueuedFrames() { }

    public void Close(MediaEffectClosedReason reason)
    {
        _mask?.Dispose();
        _mask = null;
        _device?.Dispose();
        _device = null;
    }

    private static double GetFraction(IPropertySet properties, string name,
        double fallback) => properties.TryGetValue(name, out object? value) &&
            value is double fraction && double.IsFinite(fraction) &&
            fraction is >= 0 and <= 1 ? fraction : fallback;

    private static CanvasGeometry CreateSquircle(CanvasDevice device,
        float width, float height)
    {
        using var path = new CanvasPathBuilder(device);
        for (int index = 0; index <= 64; index++)
        {
            double angle = 2 * Math.PI * index / 64;
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);
            float x = width / 2 + width / 2 * (float)(Math.Sign(cosine) *
                Math.Sqrt(Math.Abs(cosine)));
            float y = height / 2 + height / 2 * (float)(Math.Sign(sine) *
                Math.Sqrt(Math.Abs(sine)));
            var point = new System.Numerics.Vector2(x, y);
            if (index == 0) path.BeginFigure(point);
            else path.AddLine(point);
        }
        path.EndFigure(CanvasFigureLoop.Closed);
        return CanvasGeometry.CreatePath(path);
    }
}
