using Velopack;
using Velopack.Locators;
using Shnapp.App.Windows;

namespace Shnapp.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if SHNAPP_VELOPACK
        // Install/update hooks must run before WinUI creates its application object.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => VelopackStartupCleanup.RemoveCurrentInstallRunEntry(
                VelopackLocator.Current.AppId, VelopackLocator.Current.RootAppDir))
            .Run();
#endif
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
