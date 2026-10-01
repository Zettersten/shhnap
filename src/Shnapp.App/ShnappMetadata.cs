namespace Shnapp.App;

/// <summary>Compact, consistent metadata labels for saved shnapps.</summary>
internal static class ShnappMetadata
{
    internal static string FormatFileSize(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        double size = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        do
        {
            size /= 1024;
            unit++;
        }
        while (size >= 1024 && unit < units.Length - 1);

        return $"{size:0.#} {units[unit]}";
    }
}
