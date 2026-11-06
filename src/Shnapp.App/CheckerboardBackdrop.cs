using System.Numerics;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;

namespace Shnapp.App;

/// <summary>Draws the shared canvas backdrop behind image and video content.</summary>
internal static class CheckerboardBackdrop
{
    private const int TileSize = 18;

    internal static void Draw(CanvasDrawingSession drawing, double width, double height,
        Color baseColor, Color alternateColor, bool highContrast)
    {
        drawing.Transform = Matrix3x2.Identity;
        drawing.FillRectangle(new Rect(0, 0, width, height), baseColor);
        if (highContrast) return;

        int columns = (int)Math.Ceiling(width / TileSize);
        int rows = (int)Math.Ceiling(height / TileSize);
        for (int row = 0; row < rows; row++)
        {
            for (int column = row & 1; column < columns; column += 2)
            {
                drawing.FillRectangle(column * TileSize, row * TileSize,
                    TileSize, TileSize, alternateColor);
            }
        }
    }
}
