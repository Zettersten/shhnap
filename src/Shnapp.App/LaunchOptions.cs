using System.Security.Cryptography;
using System.Text;

namespace Shnapp.App;

internal sealed record LaunchOptions(string DataRoot, bool Background, bool Isolated)
{
    internal string InstanceKey => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(Environment.UserName + "|" + DataRoot.ToUpperInvariant())))[..24];

    internal static LaunchOptions Parse()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shnapp");
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
}
