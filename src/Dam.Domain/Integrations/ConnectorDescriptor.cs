namespace Dam.Domain.Integrations;

/// <summary>How far a connector can be used (ADR-0040 decision 1).</summary>
public static class ConnectorStatuses
{
    /// <summary>Works now.</summary>
    public const string Available = "available";
    /// <summary>Can be configured and tested; its actions are still to come.</summary>
    public const string Preview = "preview";
    /// <summary>Listed so people can see it is coming; a connection cannot be created.</summary>
    public const string Planned = "planned";
    public static readonly IReadOnlyList<string> All = [Available, Preview, Planned];
}

public static class FieldTypes
{
    public const string Text = "text";
    public const string Url = "url";
    public const string Secret = "secret";
    public const string Select = "select";
    public const string Boolean = "boolean";
    public const string Number = "number";
    public const string Textarea = "textarea";
    public static readonly IReadOnlyList<string> All = [Text, Url, Secret, Select, Boolean, Number, Textarea];
}

public static class FieldGroups
{
    public const string Connection = "connection";
    public const string Authentication = "authentication";
    public const string Advanced = "advanced";
    public static readonly IReadOnlyList<string> All = [Connection, Authentication, Advanced];
}

public sealed record FieldOption(string Value, string Label);

/// <summary>Shows an authentication field only while <paramref name="Field"/> (a select such as the authentication strategy) holds one of <paramref name="Values"/>.
/// On the wire the list is called <c>equals</c>.</summary>
public sealed record ShowWhen(string Field, IReadOnlyList<string> Values)
{
    public bool Matches(string? current) => current is not null && Values.Contains(current);
}

/// <summary>
/// One setting of a connector. The console draws its form from these, and the server validates a connection's configuration against them.
/// </summary>
/// <param name="Required">Needed whatever the authentication strategy is. Fields that only some strategies need are checked by the strategy rules.</param>
/// <param name="Default">Applied when a configuration leaves the field out. A JSON value of the field's type.</param>
/// <param name="MaxLength">Longest text accepted; null uses the type's default (see <see cref="ConnectionRules"/>).</param>
/// <param name="Min">Smallest number accepted. Used for validation only, so the help text should say it.</param>
/// <param name="Max">Largest number accepted. Used for validation only.</param>
/// <param name="Verbatim">Keep the text exactly as typed (a token prefix such as <c>Bearer </c> ends in a space that must survive).</param>
public sealed record ConnectorField(
    string Key, string Label, string Type, bool Required = false, string? Help = null, string? Placeholder = null,
    string Group = FieldGroups.Connection, IReadOnlyList<FieldOption>? Options = null, System.Text.Json.JsonElement? Default = null,
    ShowWhen? ShowWhen = null, int? MaxLength = null, long? Min = null, long? Max = null, bool Verbatim = false)
{
    public bool IsSecret => Type == FieldTypes.Secret;
}

/// <summary>What a connector is, independent of any tenant's use of it (ADR-0040 decision 1).</summary>
public sealed record ConnectorDescriptor(
    string Key, string Name, string Description, string Category, string Status,
    IReadOnlyList<string> AuthStrategies, string? DefaultAuthStrategy, IReadOnlyList<string> Capabilities, IReadOnlyList<ConnectorField> Fields)
{
    public ConnectorField? Field(string key) => Fields.FirstOrDefault(f => f.Key == key);
    public bool CanBeCreated => Status != ConnectorStatuses.Planned;
}
