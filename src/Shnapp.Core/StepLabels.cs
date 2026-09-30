using System.Globalization;
using System.Text;

namespace Shnapp.Core;

/// <summary>Formats a step's sequence position for display and export.</summary>
public static class StepLabels
{
    /// <summary>Formats a positive step number as digits, letters, or Roman numerals.</summary>
    public static string Format(int number, StepLabelFormat style)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
        return style switch
        {
            StepLabelFormat.Decimal => number.ToString(CultureInfo.InvariantCulture),
            StepLabelFormat.UpperLetters => Letters(number),
            StepLabelFormat.LowerLetters => Letters(number).ToLowerInvariant(),
            StepLabelFormat.UpperRoman => Roman(number),
            StepLabelFormat.LowerRoman => Roman(number).ToLowerInvariant(),
            _ => throw new ArgumentOutOfRangeException(nameof(style)),
        };
    }

    private static string Letters(int number)
    {
        var characters = new List<char>();
        while (number > 0)
        {
            number--;
            characters.Add((char)('A' + number % 26));
            number /= 26;
        }

        characters.Reverse();
        return new string([.. characters]);
    }

    private static string Roman(int number)
    {
        ReadOnlySpan<(int Value, string Digits)> numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];
        var output = new StringBuilder();
        foreach ((int value, string digits) in numerals)
        {
            while (number >= value)
            {
                output.Append(digits);
                number -= value;
            }
        }

        return output.ToString();
    }
}
