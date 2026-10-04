using Velopack;

namespace Shnapp.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if SHNAPP_VELOPACK
        // Install/update hooks must run before WinUI creates its application object.
        VelopackApp.Build().Run();
#endif
        XamlGeneratedProgram.XamlGeneratedMain();
    }
}
