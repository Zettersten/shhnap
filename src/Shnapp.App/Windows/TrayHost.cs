using System.ComponentModel;
using System.Runtime.InteropServices;
using Shnapp.Core;

namespace Shnapp.App.Windows;

internal sealed class TrayHost : IDisposable
{
    private readonly nint _hwnd;
    private readonly NativeMethods.SubclassProc _callback;
    private readonly uint _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private readonly uint _activationMessage;
    private readonly List<int> _registeredHotkeys = [];
    private readonly List<string> _conflicts = [];
    private NativeMethods.NotifyIconData _icon;
    private bool _disposed;

    internal event Action<CaptureKind>? CaptureRequested;
    internal event Action? LibraryRequested;
    internal event Action? SettingsRequested;
    internal event Action? UpdateNotificationClicked;
    internal event Action? QuitRequested;

    internal IReadOnlyList<string> Conflicts => _conflicts;
    internal bool IsPresent { get; private set; }

    internal void ShowUpdateNotification(string version)
    {
        if (!IsPresent || _disposed)
        {
            return;
        }

        _icon.InfoTitle = "Shnapp update available";
        _icon.Info = $"Version {version} is available. Click to open About & Updates.";
        _icon.InfoFlags = 0x1; // NIIF_INFO
        _icon.Flags |= 0x10; // NIF_INFO
        NativeMethods.ShellNotifyIcon(1, ref _icon); // NIM_MODIFY
        _icon.Flags &= ~0x10u;
    }

    internal TrayHost(nint hwnd, uint activationMessage)
    {
        _hwnd = hwnd;
        _activationMessage = activationMessage;
        _callback = ProcessMessage;
        if (!NativeMethods.SetWindowSubclass(hwnd, _callback, 1, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach Shnapp's tray message handler.");
        }

        int iconSize = (int)Math.Max(16, 16 * NativeMethods.GetDpiForWindow(hwnd) / 96);
        _icon = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
            Window = hwnd,
            Id = 1,
            Flags = 0x1 | 0x2 | 0x4,
            CallbackMessage = NativeMethods.TrayCallback,
            Icon = NativeMethods.LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"),
                1, iconSize, iconSize, 0x10),
            Tip = "Shnapp — capture first, think less",
            Info = string.Empty,
            InfoTitle = string.Empty,
        };
        AddIcon();
        Register(1, 0x34, "Ctrl+Shift+4");
        Register(2, 0x33, "Ctrl+Shift+3");
        Register(3, 0x32, "Ctrl+Shift+2");
    }

    private void Register(int id, uint key, string label)
    {
        if (NativeMethods.RegisterHotKey(_hwnd, id, NativeMethods.Modifiers, key))
        {
            _registeredHotkeys.Add(id);
        }
        else
        {
            _conflicts.Add(label);
        }
    }

    private void AddIcon()
    {
        IsPresent = NativeMethods.ShellNotifyIcon(0, ref _icon);
        _icon.TimeoutOrVersion = 4;
        NativeMethods.ShellNotifyIcon(4, ref _icon);
    }

    private nint ProcessMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        if (_disposed)
        {
            return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
        }

        if (message == _activationMessage)
        {
            LibraryRequested?.Invoke();
            return 0;
        }
        else if (message == _taskbarCreated)
        {
            AddIcon();
        }
        else if (message == NativeMethods.WmHotkey)
        {
            CaptureRequested?.Invoke((int)wParam switch
            {
                1 => CaptureKind.Window,
                2 => CaptureKind.FullScreen,
                _ => CaptureKind.Region,
            });
            return 0;
        }
        else if (message == NativeMethods.TrayCallback)
        {
            uint action = (uint)((long)lParam & 0xFFFF);
            if (action == 0x0405) // NIN_BALLOONUSERCLICK
            {
                UpdateNotificationClicked?.Invoke();
            }
            else if (action is 0x0205 or 0x007B)
            {
                ShowMenu();
            }
            else if (action is 0x0202 or 0x0400 or 0x0401)
            {
                LibraryRequested?.Invoke();
            }

            return 0;
        }

        return NativeMethods.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        NativeMethods.GetCursorPos(out NativeMethods.Point pointer);
        nint menu = NativeMethods.CreatePopupMenu();
        try
        {
            NativeMethods.AppendMenu(menu, 0, 1, "New window shnapp\tCtrl+Shift+4");
            NativeMethods.AppendMenu(menu, 0, 2, "New full-screen shnapp\tCtrl+Shift+3");
            NativeMethods.AppendMenu(menu, 0, 3, "New free-form shnapp\tCtrl+Shift+2");
            NativeMethods.AppendMenu(menu, 0x800, 0, null);
            NativeMethods.AppendMenu(menu, 0, 4, "Open Library");
            NativeMethods.AppendMenu(menu, 0, 5, "Settings");
            NativeMethods.AppendMenu(menu, 0x800, 0, null);
            NativeMethods.AppendMenu(menu, 0, 6, "Quit Shnapp");
            NativeMethods.SetForegroundWindow(_hwnd);
            uint command = NativeMethods.TrackPopupMenuEx(menu, 0x100 | 0x80 | 0x2, pointer.X, pointer.Y, _hwnd, 0);
            switch (command)
            {
                case 1: CaptureRequested?.Invoke(CaptureKind.Window); break;
                case 2: CaptureRequested?.Invoke(CaptureKind.FullScreen); break;
                case 3: CaptureRequested?.Invoke(CaptureKind.Region); break;
                case 4: LibraryRequested?.Invoke(); break;
                case 5: SettingsRequested?.Invoke(); break;
                case 6: QuitRequested?.Invoke(); break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (int id in _registeredHotkeys)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
        }

        NativeMethods.ShellNotifyIcon(2, ref _icon);
        NativeMethods.RemoveWindowSubclass(_hwnd, _callback, 1);
        if (_icon.Icon != 0)
        {
            NativeMethods.DestroyIcon(_icon.Icon);
        }

        IsPresent = false;
    }
}
