using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Storage;

namespace Shnapp.App;

internal sealed record LaunchOptions(string DataRoot, bool Background, bool Isolated)
{
    private static readonly Guid LocalAppDataId = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");
    private const uint NoPackageRedirection = 0x00010000;

    internal static string PortableDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shnapp");

    internal static string UnredirectedPortableDataRoot
    {
        get
        {
            Guid folderId = LocalAppDataId;
            int result = SHGetKnownFolderPath(in folderId, NoPackageRedirection, 0, out nint pointer);
            if (result < 0)
            {
                throw new IOException("Windows could not locate the portable Shnapp library.",
                    Marshal.GetExceptionForHR(result));
            }

            try
            {
                string localAppData = Marshal.PtrToStringUni(pointer)
                    ?? throw new IOException("Windows returned an empty local AppData path.");
                return Path.Combine(localAppData, "Shnapp");
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

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
                // LocalCache persists across MSIX updates without cloud device backup.
                return ApplicationData.Current.LocalCacheFolder.Path;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.Runtime.InteropServices.COMException)
        {
            // Portable builds have no package identity.
        }

        return PortableDataRoot;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token,
        out nint path);
}
