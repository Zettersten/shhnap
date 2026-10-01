namespace Shnapp.Core;

/// <summary>Rules for user-assigned shnapp titles.</summary>
public static class ShnappTitles
{
    public const int MaximumLength = 120;

    /// <summary>Trims and validates a title before it is saved.</summary>
    public static string Normalize(string title)
    {
        ArgumentNullException.ThrowIfNull(title);
        string normalized = title.Trim();
        if (normalized.Length is 0 or > MaximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Use a title of 1–120 characters without line breaks or control characters.", nameof(title));
        }

        return normalized;
    }
}
