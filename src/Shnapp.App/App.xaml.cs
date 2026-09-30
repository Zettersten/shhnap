using Microsoft.UI.Xaml;
using Shnapp.App.Windows;

namespace Shnapp.App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Mutex? _instance;
    private AppController? _controller;
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            string directory = LaunchOptions.Parse().DataRoot;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "last-error.log"), args.Exception.ToString());
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        LaunchOptions options = LaunchOptions.Parse();
        _instance = new Mutex(initiallyOwned: true, "Local\\Shnapp-" + options.InstanceKey, out bool firstInstance);
        uint activationMessage = NativeMethods.RegisterWindowMessage("Shnapp.Activate." + options.InstanceKey);
        if (!firstInstance)
        {
            NativeMethods.PostMessage(0xFFFF, activationMessage, 0, 0);
            _instance.Dispose();
            _instance = null;
            Exit();
            return;
        }
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _controller = new AppController((MainWindow)Window, options, activationMessage, () =>
        {
            _instance?.ReleaseMutex();
            _instance?.Dispose();
            _instance = null;
        });
        Window.Activate();
        await _controller.InitializeAsync();
        if (options.Background)
        {
            Window.AppWindow.Hide();
        }
    }
}
