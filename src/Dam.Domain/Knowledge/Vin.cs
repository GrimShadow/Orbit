namespace Dam.Domain.Knowledge;

/// <summary>
/// A vehicle identification number as the knowledge module reads it (ADR-0039): 17 letters and digits, none of them I, O or Q, upper-cased on input.
/// The check digit is deliberately not enforced, because not every market's VINs carry a valid one.
/// </summary>
public static class Vin
{
    public const int Length = 17;

    /// <summary>The letters that never occur in a VIN, because they look like 1 and 0.</summary>
    public const string ForbiddenLetters = "IOQ";

    public static bool IsAllowedChar(char c) => (char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)) && ForbiddenLetters.IndexOf(c, StringComparison.Ordinal) < 0;

    /// <summary>Trims and upper-cases <paramref name="text"/>; false, with a message a person can act on, when it is not a well-formed VIN.</summary>
    public static bool TryNormalize(string? text, out string vin, out string error)
    {
        vin = "";
        error = "";
        var t = text?.Trim().ToUpperInvariant() ?? "";
        if (t.Length == 0) { error = "A VIN is required."; return false; }
        if (t.Length != Length) { error = $"A VIN has exactly {Length} characters; this one has {t.Length}."; return false; }
        var bad = t.Where(c => !IsAllowedChar(c)).Distinct().ToList();
        if (bad.Count > 0)
        {
            error = bad.Any(c => ForbiddenLetters.Contains(c, StringComparison.Ordinal))
                    && bad.All(c => ForbiddenLetters.Contains(c, StringComparison.Ordinal))
                ? "A VIN never contains the letters I, O or Q."
                : "A VIN contains only letters and digits, and never the letters I, O or Q.";
            return false;
        }
        vin = t;
        return true;
    }
}

/// <summary>
/// The model year a VIN's tenth character stands for. The code is the letters A-Y without I, O, Q and U, then the digits 1-9, and it repeats every 30 years,
/// so one character names two possible years (T is 1996 and 2026). The latest year that is not more than one year after the current one is chosen.
/// </summary>
public static class ModelYears
{
    /// <summary>The 30 codes in order; position 0 is 1980 (and 2010).</summary>
    public const string Codes = "ABCDEFGHJKLMNPRSTVWXY123456789";

    public const int FirstYear = 1980;
    public const int Cycle = 30;

    /// <summary>The year the code stands for, or null when it is not a model-year code (the letters I, O, Q, U, Z and the digit 0).</summary>
    public static int? Decode(char code, int currentYear)
    {
        var i = Codes.IndexOf(char.ToUpperInvariant(code), StringComparison.Ordinal);
        if (i < 0) return null;
        var limit = currentYear + 1;
        var year = FirstYear + i;
        if (year > limit) return year;   // a code from the past century can only be the first cycle; an earlier current year than 1980 is not meaningful
        year += (limit - year) / Cycle * Cycle;
        return year;
    }

    /// <summary>The model year of a normalised 17-character VIN (its tenth character), or null when that character is not a year code.</summary>
    public static int? FromVin(string vin, int currentYear) => vin.Length == Vin.Length ? Decode(vin[9], currentYear) : null;

    /// <summary>The lowest and highest model year the tenant may give a document or a rule.</summary>
    public const int MinYear = FirstYear;
    public const int MaxYear = 2100;
}
