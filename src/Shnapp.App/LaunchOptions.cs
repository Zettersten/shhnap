using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Shnapp.App;

internal sealed record LaunchOptions(string DataRoot, bool Background, bool Isolated)
{
    internal static string PortableDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shnapp");

    internal string InstanceKey => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserName + "|" + DataRoot.ToUpperInvariant())))[..24];

    internal static LaunchOptions Parse()
    {
        string root = DefaultDataRoot();
        bool background = false;
        bool isolated = false;
        string[] arguments = Environment.GetCommandLineArgs();
        for (int index = 1; index < arguments.Length; index++)
        {
            if (arguments[index] == "--background")
            {
                background = true;
            }
            else if (arguments[index] == "--data-root" && index + 1 < arguments.Length)
            {
                root = Path.GetFullPath(arguments[++index]);
                isolated = true;
            }
        }

        return new(Path.TrimEndingDirectorySeparator(root), background, isolated);
    }

    private static string DefaultDataRoot()
    {
        try
        {
            if (Package.Current.Id is not null)
            {
                // LocalState persists across MSIX updates and avoids AppData virtualization.
                return ApplicationData.Current.LocalFolder.Path;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            // Portable builds have no package identity.
        }

        return PortableDataRoot;
    }
}
