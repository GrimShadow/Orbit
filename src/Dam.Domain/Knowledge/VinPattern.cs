using System.Collections;

namespace Dam.Domain.Knowledge;

/// <summary>
/// A VIN pattern (ADR-0039), anchored at the first character and matched without regard to case. A letter or digit matches itself; <c>?</c> matches any one
/// character; <c>[ABC]</c>, <c>[A-C]</c> or a mixture (<c>[A-CX7]</c>) matches one character of a set; a final <c>*</c> matches whatever follows. Without the
/// <c>*</c> the pattern has to describe all 17 characters. That covers prefixes (<c>ACM*</c>), manufacturer-code plus descriptor patterns (<c>ACMRS1????*</c>)
/// and full-length patterns. Pure: no I/O, so every rule here is unit-tested.
/// </summary>
public sealed class VinPattern
{
    /// <summary>The longest pattern text a rule may carry.</summary>
    public const int MaxTextLength = 100;

    private readonly PatternPosition[] _positions;

    private VinPattern(string text, PatternPosition[] positions, bool open)
    {
        Text = text;
        _positions = positions;
        IsOpen = open;
        Specificity = positions.Count(p => p.IsLiteral);
    }

    /// <summary>The pattern as stored: trimmed and upper-cased.</summary>
    public string Text { get; }

    /// <summary>It ends in <c>*</c>: the rest of the VIN is not looked at.</summary>
    public bool IsOpen { get; }

    /// <summary>The number of characters the pattern names (a set or <c>?</c> counts as one).</summary>
    public int Length => _positions.Length;

    /// <summary>How many of those are plain letters or digits. The more, the more specific the pattern.</summary>
    public int Specificity { get; }

    public static bool TryParse(string? text, out VinPattern pattern, out string error)
    {
        pattern = null!;
        error = "";
        var t = text?.Trim().ToUpperInvariant() ?? "";
        if (t.Length == 0) { error = "A pattern is required."; return false; }
        if (t.Length > MaxTextLength) { error = $"A pattern is at most {MaxTextLength} characters."; return false; }

        var positions = new List<PatternPosition>();
        var open = false;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (open) { error = "Nothing can follow the final *."; return false; }
            switch (c)
            {
                case '*':
                    if (i != t.Length - 1) { error = "A * can only be the last character of a pattern."; return false; }
                    open = true;
                    break;
                case '?':
                    positions.Add(PatternPosition.Any);
                    break;
                case '[':
                    {
                        var end = t.IndexOf(']', i + 1);
                        if (end < 0) { error = "A [ is never closed with ]."; return false; }
                        if (!TryParseSet(t.AsSpan(i + 1, end - i - 1), out var set, out error)) return false;
                        positions.Add(set);
                        i = end;
                        break;
                    }
                case ']':
                    error = "A ] has no matching [.";
                    return false;
                default:
                    if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
                    {
                        error = $"'{c}' is not allowed in a pattern; use letters, digits, ?, [..] sets and a final *.";
                        return false;
                    }
                    if (!Vin.IsAllowedChar(c)) { error = $"'{c}' can never occur in a VIN (the letters I, O and Q are not used)."; return false; }
                    positions.Add(PatternPosition.Literal(c));
                    break;
            }
        }

        if (positions.Count == 0) { error = "A pattern needs at least one character before the *."; return false; }
        if (positions.Count > Vin.Length) { error = $"A pattern describes at most {Vin.Length} characters; this one describes {positions.Count}."; return false; }
        if (!open && positions.Count != Vin.Length)
        {
            error = $"Without a final * a pattern has to describe all {Vin.Length} characters of the VIN; this one describes {positions.Count}. Add a * to match a prefix.";
            return false;
        }
        pattern = new VinPattern(t, positions.ToArray(), open);
        return true;
    }

    /// <summary>Parses the text between <c>[</c> and <c>]</c>: single characters and ranges (A-C, 1-4), no negation.</summary>
    private static bool TryParseSet(ReadOnlySpan<char> body, out PatternPosition set, out string error)
    {
        set = default;
        error = "";
        if (body.Length == 0) { error = "A set [] cannot be empty."; return false; }
        if (body[0] == '^') { error = "Negated sets ([^..]) are not supported."; return false; }

        var bits = new BitArray(128);
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c is '[' or '?' or '*') { error = $"'{c}' cannot be used inside a set."; return false; }
            if (i + 2 < body.Length && body[i + 1] == '-')
            {
                var to = body[i + 2];
                if (!IsPlain(c) || !IsPlain(to)) { error = $"'{c}-{to}' is not a valid range; use letters or digits."; return false; }
                if (char.IsAsciiDigit(c) != char.IsAsciiDigit(to)) { error = $"The range '{c}-{to}' mixes letters and digits."; return false; }
                if (c > to) { error = $"The range '{c}-{to}' runs backwards."; return false; }
                for (var x = c; x <= to; x++) if (Vin.IsAllowedChar(x)) bits[x] = true;
                i += 2;
                continue;
            }
            if (c == '-') { error = "A '-' in a set has to sit between two characters, like [A-C]."; return false; }
            if (!IsPlain(c)) { error = $"'{c}' is not allowed in a set; use letters and digits."; return false; }
            if (!Vin.IsAllowedChar(c)) { error = $"'{c}' can never occur in a VIN (the letters I, O and Q are not used)."; return false; }
            bits[c] = true;
        }

        var any = false;
        foreach (bool b in bits) if (b) { any = true; break; }
        if (!any) { error = "This set can match no character a VIN has."; return false; }
        set = PatternPosition.Set(bits);
        return true;
    }

    private static bool IsPlain(char c) => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c);

    /// <summary>Does the VIN fit the pattern? The VIN is compared as written, upper-cased; pass a normalised VIN (<see cref="Vin.TryNormalize"/>).</summary>
    public bool Matches(string vin)
    {
        if (vin.Length != Vin.Length) return false;
        if (!IsOpen && _positions.Length != vin.Length) return false;
        for (var i = 0; i < _positions.Length; i++)
            if (!_positions[i].Accepts(char.ToUpperInvariant(vin[i]))) return false;
        return true;
    }

    /// <summary>Why a VIN does not fit: the first position that disagrees, in words. Null when it fits.</summary>
    public string? WhyNot(string vin)
    {
        if (vin.Length != Vin.Length) return $"The VIN does not have {Vin.Length} characters.";
        for (var i = 0; i < _positions.Length; i++)
            if (!_positions[i].Accepts(char.ToUpperInvariant(vin[i])))
                return $"Character {i + 1} is '{char.ToUpperInvariant(vin[i])}' but the pattern needs {_positions[i].Describe()}.";
        return null;
    }

    public override string ToString() => Text;

    private readonly struct PatternPosition
    {
        private readonly char _literal;
        private readonly BitArray? _set;
        private readonly bool _any;

        private PatternPosition(char literal, BitArray? set, bool any) { _literal = literal; _set = set; _any = any; }

        public static PatternPosition Any => new('\0', null, true);
        public static PatternPosition Literal(char c) => new(c, null, false);
        public static PatternPosition Set(BitArray bits) => new('\0', bits, false);

        public bool IsLiteral => !_any && _set is null;

        public bool Accepts(char c) => _any || (_set is not null ? c < 128 && _set[c] : c == _literal);

        public string Describe()
        {
            if (_any) return "any character";
            if (_set is null) return $"'{_literal}'";
            var chars = new List<char>();
            for (var i = 0; i < 128; i++) if (_set[i]) chars.Add((char)i);
            return "one of " + string.Concat(chars);
        }
    }
}
