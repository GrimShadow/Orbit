using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Dam.Domain.Integrations;

public static class LoginBodyTypes
{
    public const string Json = "json";
    public const string Form = "form";
    public const string None = "none";
    public static readonly IReadOnlyList<string> All = [Json, Form, None];
}

/// <summary>
/// The body template of a <c>login_call</c> (ADR-0040 decision 4): text with placeholders <c>{{username}}</c>, <c>{{password}}</c>, <c>{{apiKey}}</c>,
/// <c>{{clientId}}</c> and <c>{{clientSecret}}</c>. Substitution is strict and happens once: an unknown placeholder is refused when the connection is saved, a
/// placeholder without a value fails the call (it is never sent as literal text), and a value that itself contains <c>{{...}}</c> is inserted as text, never
/// substituted again. In a JSON body every value is JSON-escaped and a placeholder must sit inside a string literal, so a value can never add keys or break
/// out of its string; in a form body every value is percent-encoded.
/// </summary>
public static class LoginTemplate
{
    public const int MaxLength = 4000;

    public static readonly IReadOnlyList<string> Placeholders = [FieldKeys.Username, FieldKeys.Password, FieldKeys.ApiKey, FieldKeys.ClientId, FieldKeys.ClientSecret];

    /// <summary>Which placeholders carry a secret (the others are plain settings).</summary>
    public static bool IsSecretPlaceholder(string name) => name is FieldKeys.Password or FieldKeys.ApiKey or FieldKeys.ClientSecret;

    private readonly record struct Piece(string Text, bool IsPlaceholder, bool InJsonString);

    private static (List<Piece>? Pieces, string? Error) Tokenize(string template, string bodyType)
    {
        var pieces = new List<Piece>();
        var literal = new StringBuilder();
        var inString = false;
        var escaped = false;
        var i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{' && i + 1 < template.Length && template[i + 1] == '{')
            {
                var end = template.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end < 0) return (null, "A placeholder is not closed: every {{ needs a matching }}.");
                var name = template[(i + 2)..end].Trim();
                if (!Placeholders.Contains(name))
                    return (null, $"Unknown placeholder '{{{{{Clip(name)}}}}}'. Use {string.Join(", ", Placeholders.Select(p => "{{" + p + "}}"))}.");
                if (literal.Length > 0) { pieces.Add(new Piece(literal.ToString(), false, false)); literal.Clear(); }
                pieces.Add(new Piece(name, true, inString));
                i = end + 2;
                continue;
            }
            var c = template[i];
            literal.Append(c);
            if (bodyType == LoginBodyTypes.Json)
            {
                if (escaped) escaped = false;
                else if (inString && c == '\\') escaped = true;
                else if (c == '"') inString = !inString;
            }
            i++;
        }
        if (literal.Length > 0) pieces.Add(new Piece(literal.ToString(), false, false));
        return (pieces, null);
    }

    private static string Clip(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= 30 ? clean : clean[..30] + "...";
    }

    /// <summary>Problems with a template, empty when it is acceptable. Checks the placeholders, that JSON placeholders sit inside strings, and that a JSON body is JSON once filled in.</summary>
    public static IReadOnlyList<string> Validate(string? template, string bodyType)
    {
        var errors = new List<string>();
        if (bodyType == LoginBodyTypes.None) return errors;
        if (string.IsNullOrWhiteSpace(template)) { errors.Add("A body is required unless the body type is 'none'."); return errors; }
        if (template.Length > MaxLength) { errors.Add($"The body is at most {MaxLength} characters."); return errors; }

        var (pieces, error) = Tokenize(template, bodyType);
        if (pieces is null) { errors.Add(error!); return errors; }
        if (bodyType == LoginBodyTypes.Json)
        {
            foreach (var p in pieces.Where(p => p.IsPlaceholder && !p.InJsonString).Select(p => p.Text).Distinct())
                errors.Add($"The placeholder '{{{{{p}}}}}' must be inside a JSON string, for example \"{p}\": \"{{{{{p}}}}}\".");
            if (errors.Count > 0) return errors;
            try
            {
                using var _ = JsonDocument.Parse(Fill(pieces, _ => "x", escapeJson: true), new JsonDocumentOptions { MaxDepth = 32 });
            }
            catch (JsonException)
            {
                errors.Add("The body is not valid JSON once the placeholders are filled in.");
            }
        }
        return errors;
    }

    /// <summary>The placeholders a template uses, in order of first use. Empty for a template that cannot be read.</summary>
    public static IReadOnlyList<string> Used(string? template, string bodyType)
    {
        if (string.IsNullOrEmpty(template) || bodyType == LoginBodyTypes.None) return [];
        var (pieces, _) = Tokenize(template, bodyType);
        return pieces?.Where(p => p.IsPlaceholder).Select(p => p.Text).Distinct().ToList() ?? [];
    }

    /// <summary>The body to send. Fails (with a message that names the placeholder, never a value) when a placeholder has no value.</summary>
    public static (string? Body, string? Error) Render(string? template, string bodyType, Func<string, string?> value)
    {
        if (bodyType == LoginBodyTypes.None) return (null, null);
        if (string.IsNullOrWhiteSpace(template)) return (null, "The sign-in body is empty.");
        var (pieces, error) = Tokenize(template, bodyType);
        if (pieces is null) return (null, error);
        if (bodyType == LoginBodyTypes.Json && pieces.Any(p => p.IsPlaceholder && !p.InJsonString))
            return (null, "A placeholder in the sign-in body is outside a JSON string.");
        foreach (var name in pieces.Where(p => p.IsPlaceholder).Select(p => p.Text).Distinct())
            if (string.IsNullOrEmpty(value(name))) return (null, $"The sign-in body needs a value for {{{{{name}}}}} but it is not set.");
        return (Fill(pieces, name => value(name)!, escapeJson: bodyType == LoginBodyTypes.Json), null);
    }

    private static string Fill(List<Piece> pieces, Func<string, string> value, bool escapeJson)
    {
        var sb = new StringBuilder();
        foreach (var p in pieces)
        {
            if (!p.IsPlaceholder) { sb.Append(p.Text); continue; }
            var v = value(p.Text);
            sb.Append(escapeJson ? JsonEncodedText.Encode(v, JavaScriptEncoder.Default).ToString() : Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
