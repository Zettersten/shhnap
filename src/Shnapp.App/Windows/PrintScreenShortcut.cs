using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shnapp.App.Windows;

/// <summary>Intercepts plain Print Screen on a dedicated Windows message-loop thread.</summary>
internal sealed class PrintScreenShortcut : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const uint VkPrintScreen = 0x2C;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint AltDown = 0x20;
    private const uint Injected = 0x10;
    private readonly HookProc _callback;
    private readonly nint _targetWindow;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _initialized = new(false);
    private Exception? _startupError;
    private uint _threadId;
    private nint _hook;
    private bool _pressed;
    private bool _passedThrough;
    private volatile bool _stop;

    internal PrintScreenShortcut(nint targetWindow)
    {
        _targetWindow = targetWindow;
        _callback = OnKeyboard;
        _thread = new Thread(RunHookLoop)
        {
            IsBackground = true,
            Name = "Shnapp Print Screen shortcut",
        };
        _thread.Start();
        if (!_initialized.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new Win32Exception("Windows did not start the Print Screen shortcut.");
        }

        if (_startupError is not null)
        {
            Dispose();
            throw new Win32Exception("Windows could not assign Print Screen to Shnapp.", _startupError);
        }
    }

    private void RunHookLoop()
    {
        try
        {
            _threadId = GetCurrentThreadId();
            // Thread messages need a queue even before the first GetMessage call.
            PeekMessage(out _, 0, 0, 0, 0);
            if (!_stop)
            {
                _hook = SetWindowsHookEx(WhKeyboardLowLevel, _callback, GetModuleHandle(null), 0);
                if (_hook == 0)
                {
                    _startupError = new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }
        catch (Exception exception)
        {
            _startupError = exception;
        }
        finally
        {
            _initialized.Set();
        }

        if (_hook == 0) { return; }
        try
        {
            while (GetMessage(out _, 0, 0, 0) > 0) { }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        if (code != 0 || _hook == 0)
        {
            return CallNextHookEx(_hook, code, message, data);
        }

        try
        {
            KeyboardEvent key = Marshal.PtrToStructure<KeyboardEvent>(data);
            if (key.VirtualKey != VkPrintScreen || (key.Flags & Injected) != 0)
            {
                return CallNextHookEx(_hook, code, message, data);
            }

            uint keyboardMessage = (uint)message;
            bool released = keyboardMessage is WmKeyUp or WmSysKeyUp;
            if (released && _pressed)
            {
                _pressed = false;
                return 1;
            }

            if (released && _passedThrough)
            {
                _passedThrough = false;
                return CallNextHookEx(_hook, code, message, data);
            }

            bool pressed = keyboardMessage is WmKeyDown or WmSysKeyDown;
            if (pressed && _pressed)
            {
                return 1;
            }

            if (pressed && _passedThrough)
            {
                return CallNextHookEx(_hook, code, message, data);
            }

            bool plainKey = (key.Flags & AltDown) == 0 &&
                !IsDown(0x10) && !IsDown(0x11) && !IsDown(0x5B) && !IsDown(0x5C);
            if (!plainKey)
            {
                // Keep a modified press with Windows even if its modifiers are
                // released before Print Screen is released.
                if (pressed) _passedThrough = true;
                return CallNextHookEx(_hook, code, message, data);
            }

            if (released)
            {
                // Some keyboards report Print Screen only when the key is released.
                return QueueCapture() ? 1 : CallNextHookEx(_hook, code, message, data);
            }

            if (pressed)
            {
                if (!_pressed)
                {
                    if (!QueueCapture())
                    {
                        _passedThrough = true;
                        return CallNextHookEx(_hook, code, message, data);
                    }
                    _pressed = true;
                }
                return 1;
            }
        }
        catch
        {
            // An exception must never cross the native callback boundary.
        }

        return CallNextHookEx(_hook, code, message, data);
    }

    private bool QueueCapture() =>
        NativeMethods.PostMessage(_targetWindow, NativeMethods.WmPrintScreen, 0, 0);

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    public void Dispose()
    {
        if (_stop) { return; }
        _stop = true;
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        }
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _initialized.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal NativeMethods.Point Position;
        internal uint Private;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint HookProc(int code, nuint message, nint data);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out Message message, nint window, uint min, uint max);

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
