using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shnapp.App.Windows;

/// <summary>Publishes both an exact PNG and a 32-bit DIB with an alpha mask.</summary>
internal static class TransparentClipboard
{
    private const uint CfDibV5 = 17;
    private const uint GmemMoveable = 0x0002;
    private const int DibHeaderLength = 124;

    internal static void SetImage(nint owner, byte[] png, byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (owner == 0 || png.Length == 0 || bgra.Length != checked(width * height * 4))
        {
            throw new ArgumentException("The clipboard image is incomplete.");
        }

        uint pngFormat = RegisterClipboardFormat("PNG");
        if (pngFormat == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        Span<byte> header = stackalloc byte[DibHeaderLength];
        header.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(header, DibHeaderLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], -height); // Top-down BGRA pixels.
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1); // Planes.
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 32); // Bits per pixel.
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 3); // BI_BITFIELDS.
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], checked((uint)bgra.Length));
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780); // 96 DPI.
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);
        BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00ff0000); // Red.
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000ff00); // Green.
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000ff); // Blue.
        BinaryPrimitives.WriteUInt32LittleEndian(header[52..], 0xff000000); // Alpha.
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..], 0x73524742); // sRGB.

        nint pngMemory = 0;
        nint dibMemory = 0;
        bool opened = false;
        try
        {
            pngMemory = Allocate(png.Length);
            CopyToGlobal(pngMemory, png);
            dibMemory = Allocate(checked(DibHeaderLength + bgra.Length));
            CopyToGlobal(dibMemory, header.ToArray(), bgra);

            for (int attempt = 0; attempt < 4 && !opened; attempt++)
            {
                opened = OpenClipboard(owner);
                if (!opened)
                {
                    Thread.Sleep(10);
                }
            }
            if (!opened || !EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (SetClipboardData(pngFormat, pngMemory) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            pngMemory = 0; // The clipboard owns this allocation now.
            if (SetClipboardData(CfDibV5, dibMemory) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            dibMemory = 0;
        }
        finally
        {
            if (opened)
            {
                CloseClipboard();
            }
            if (pngMemory != 0)
            {
                GlobalFree(pngMemory);
            }
            if (dibMemory != 0)
            {
                GlobalFree(dibMemory);
            }
        }
    }

    private static nint Allocate(int size)
    {
        nint memory = GlobalAlloc(GmemMoveable, (nuint)size);
        return memory != 0 ? memory : throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void CopyToGlobal(nint memory, byte[] first, byte[]? second = null)
    {
        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            Marshal.Copy(first, 0, pointer, first.Length);
            if (second is not null)
            {
                Marshal.Copy(second, 0, pointer + first.Length, second.Length);
            }
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
