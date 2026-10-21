using System.Text.Json;
using Dam.Application.Integrations;
using Dam.Domain.Integrations;

namespace Dam.UnitTests;

/// <summary>The pure parts of the integrations framework (ADR-0040): the login template engine, JSON pointers, tokens and their lifetime, header rules, the strategies' requests, validation against descriptors, secret hints and error sanitising.</summary>
public sealed class IntegrationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static JsonElement S(string value) => JsonSerializer.SerializeToElement(value);
    private static Func<string, string?> Values(params (string Key, string Value)[] pairs) => k => pairs.FirstOrDefault(p => p.Key == k).Value;

    // ---- login template ------------------------------------------------------------------------------------------

    [Fact]
    public void A_json_body_is_filled_in_with_every_value_json_escaped()
    {
        var (body, error) = LoginTemplate.Render("""{"user":"{{username}}","pass":"{{password}}"}""", LoginBodyTypes.Json,
            Values(("username", "ann@example.test"), ("password", "pa\"ss\\w\nordé")));
        Assert.Null(error);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("ann@example.test", doc.RootElement.GetProperty("user").GetString());
        Assert.Equal("pa\"ss\\w\nordé", doc.RootElement.GetProperty("pass").GetString());
    }

    [Fact]
    public void A_value_cannot_add_keys_or_leave_its_json_string()
    {
        var (body, error) = LoginTemplate.Render("""{"user":"{{username}}","pass":"{{password}}"}""", LoginBodyTypes.Json,
            Values(("username", "x"), ("password", "\",\"admin\":true,\"z\":\"")));
        Assert.Null(error);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(2, doc.RootElement.EnumerateObject().Count());
        Assert.False(doc.RootElement.TryGetProperty("admin", out _));
        Assert.Equal("\",\"admin\":true,\"z\":\"", doc.RootElement.GetProperty("pass").GetString());
    }

    [Fact]
    public void A_value_that_looks_like_a_placeholder_is_inserted_as_text_and_never_substituted_again()
    {
        var (body, _) = LoginTemplate.Render("""{"a":"{{username}}","b":"{{password}}"}""", LoginBodyTypes.Json, Values(("username", "{{password}}"), ("password", "secret-pw")));
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("{{password}}", doc.RootElement.GetProperty("a").GetString());
        Assert.Equal("secret-pw", doc.RootElement.GetProperty("b").GetString());
    }

    [Fact]
    public void A_form_body_percent_encodes_every_value()
    {
        var (body, error) = LoginTemplate.Render("grant=login&u={{username}}&p={{password}}", LoginBodyTypes.Form, Values(("username", "a b&c=d"), ("password", "100%+ü")));
        Assert.Null(error);
        Assert.Equal("grant=login&u=a%20b%26c%3Dd&p=100%25%2B%C3%BC", body);
    }

    [Fact]
    public void Whitespace_inside_the_braces_is_allowed()
    {
        var (body, error) = LoginTemplate.Render("""{"p":"{{ password }}"}""", LoginBodyTypes.Json, Values(("password", "x")));
        Assert.Null(error);
        Assert.Equal("""{"p":"x"}""", body);
    }

    [Theory]
    [InlineData("""{"p":"{{secret}}"}""")]
    [InlineData("""{"p":"{{Password}}"}""")]
    [InlineData("""{"p":"{{ }}"}""")]
    [InlineData("""{"p":"{{password"}""")]
    [InlineData("""{"p":"{{ {{password}} }}"}""")]
    [InlineData("""{"p":"{{password}}", "q": "{{user.name}}"}""")]
    public void Unknown_or_broken_placeholders_are_refused_when_the_template_is_saved(string template)
    {
        Assert.NotEmpty(LoginTemplate.Validate(template, LoginBodyTypes.Json));
        Assert.NotEmpty(LoginTemplate.Validate(template, LoginBodyTypes.Form));
    }

    [Fact]
    public void A_json_placeholder_must_be_inside_a_string_and_the_result_must_be_json()
    {
        Assert.Contains(LoginTemplate.Validate("""{"n": {{clientId}}}""", LoginBodyTypes.Json), e => e.Contains("inside a JSON string", StringComparison.Ordinal));
        Assert.Contains(LoginTemplate.Validate("""{"n": "{{clientId}}" """, LoginBodyTypes.Json), e => e.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.Empty(LoginTemplate.Validate("""{"n": "{{clientId}}", "esc": "a\"{{password}}"}""", LoginBodyTypes.Json));
        // The same text is fine as a form, where there are no strings.
        Assert.Empty(LoginTemplate.Validate("n={{clientId}}", LoginBodyTypes.Form));
    }

    [Fact]
    public void A_missing_value_fails_the_call_and_names_the_placeholder_never_a_value()
    {
        var (body, error) = LoginTemplate.Render("""{"u":"{{username}}","p":"{{password}}"}""", LoginBodyTypes.Json, Values(("username", "ann")));
        Assert.Null(body);
        Assert.Contains("{{password}}", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ann", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_of_type_none_needs_no_template_and_a_template_has_a_length_limit()
    {
        Assert.Empty(LoginTemplate.Validate(null, LoginBodyTypes.None));
        Assert.Equal((null, null), LoginTemplate.Render("ignored", LoginBodyTypes.None, _ => null));
        Assert.NotEmpty(LoginTemplate.Validate("", LoginBodyTypes.Json));
        Assert.NotEmpty(LoginTemplate.Validate(new string('a', LoginTemplate.MaxLength + 1), LoginBodyTypes.Form));
    }

    [Fact]
    public void The_placeholders_a_template_uses_are_listed_once_in_order()
    {
        Assert.Equal(["password", "username"], LoginTemplate.Used("""{"p":"{{password}}","u":"{{username}}","again":"{{password}}"}""", LoginBodyTypes.Json));
        Assert.Empty(LoginTemplate.Used("{{password}}", LoginBodyTypes.None));
    }

    // ---- JSON pointer --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/access_token", true)]
    [InlineData("/data/0/token", true)]
    [InlineData("/a~1b/c~0d", true)]
    [InlineData("", false)]
    [InlineData("access_token", false)]
    [InlineData("/a~2", false)]
    [InlineData("/a~", false)]
    [InlineData("/a\nb", false)]
    public void Pointer_syntax_is_checked(string text, bool valid) => Assert.Equal(valid, JsonPointer.Check(text) is null);

    [Fact]
    public void A_pointer_finds_objects_arrays_and_escaped_names_and_says_no_for_anything_missing()
    {
        var doc = J("""{"access_token":"t1","data":[{"token":"t2"},{"token":"t3"}],"a/b":{"c~d":"t4"},"n":null}""");
        Assert.True(JsonPointer.TryResolve(doc, "/access_token", out var v) && v.GetString() == "t1");
        Assert.True(JsonPointer.TryResolve(doc, "/data/1/token", out v) && v.GetString() == "t3");
        Assert.True(JsonPointer.TryResolve(doc, "/a~1b/c~0d", out v) && v.GetString() == "t4");
        Assert.True(JsonPointer.TryResolve(doc, "/n", out v) && v.ValueKind == JsonValueKind.Null);
        Assert.False(JsonPointer.TryResolve(doc, "/missing", out _));
        Assert.False(JsonPointer.TryResolve(doc, "/data/2/token", out _));
        Assert.False(JsonPointer.TryResolve(doc, "/data/01/token", out _));
        Assert.False(JsonPointer.TryResolve(doc, "/data/x", out _));
        Assert.False(JsonPointer.TryResolve(doc, "/access_token/deeper", out _));
        Assert.False(JsonPointer.TryResolve(doc, "no-slash", out _));
    }

    // ---- tokens and lifetime -------------------------------------------------------------------------------------

    [Fact]
    public void A_token_and_its_lifetime_are_read_by_pointer()
    {
        var r = TokenExtraction.Read("""{"data":{"jwt":"abc.def.ghi"},"ttl":1800}""", "/data/jwt", "/ttl", null);
        Assert.True(r.Ok);
        Assert.Equal(("abc.def.ghi", 1800), (r.Token, r.LifetimeSeconds));
    }

    [Theory]
    [InlineData("""{"other":"x"}""", "has no value at /access_token")]
    [InlineData("""{"access_token":42}""", "is not text")]
    [InlineData("""{"access_token":null}""", "is not text")]
    [InlineData("""{"access_token":{"a":1}}""", "is not text")]
    [InlineData("""{"access_token":""}""", "is empty")]
    [InlineData("""{"access_token":"   "}""", "is empty")]
    [InlineData("""{"access_token":"has space inside"}""", "cannot be sent in a header")]
    [InlineData("""{"access_token":"line\nbreak"}""", "cannot be sent in a header")]
    [InlineData("not json", "not valid JSON")]
    [InlineData("""[1,2]""", "has no value at /access_token")]
    public void A_response_without_a_usable_token_is_a_clear_failure_that_never_echoes_the_response(string body, string expected)
    {
        var r = TokenExtraction.Read(body, "/access_token", null, null);
        Assert.False(r.Ok);
        Assert.Contains(expected, r.Error, StringComparison.Ordinal);
        Assert.Null(r.Token);
        Assert.DoesNotContain("space inside", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_that_is_too_long_is_refused()
    {
        var r = TokenExtraction.Read($$"""{"access_token":"{{new string('a', TokenExtraction.MaxTokenLength + 1)}}"}""", "/access_token", null, null);
        Assert.Contains("too long", r.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"x":3600}""", null, 3600)]            // by pointer
    [InlineData("""{"x":"900"}""", null, 900)]            // a number in text
    [InlineData("""{"x":3600.9}""", null, 3600)]          // whole seconds
    [InlineData("""{"x":0}""", 600, 600)]                 // unusable: falls back to the fixed lifetime
    [InlineData("""{"x":-5}""", null, 300)]               // ... and without one, the default
    [InlineData("""{"x":"soon"}""", null, 300)]
    [InlineData("""{"x":null}""", 120, 120)]
    [InlineData("""{"y":1}""", 7200, 7200)]               // not in the response
    [InlineData("""{"x":99999999}""", null, 86400)]       // capped
    [InlineData("""{"x":1e30}""", null, 86400)]
    public void The_lifetime_comes_from_the_response_else_the_fixed_setting_else_five_minutes_within_bounds(string body, int? fixedSeconds, int expected)
    {
        var r = TokenExtraction.Read(body.Replace("}", ",\"access_token\":\"tok\"}", StringComparison.Ordinal), "/access_token", "/x", fixedSeconds);
        Assert.Equal(expected, r.LifetimeSeconds);
    }

    [Fact]
    public void Without_a_lifetime_pointer_the_fixed_lifetime_applies()
    {
        Assert.Equal(1800, TokenExtraction.Read("""{"access_token":"tok","expires_in":99}""", "/access_token", null, 1800).LifetimeSeconds);
        Assert.Equal(TokenLifetime.DefaultSeconds, TokenExtraction.Read("""{"access_token":"tok"}""", "/access_token", null, null).LifetimeSeconds);
    }

    [Fact]
    public void A_token_is_reused_until_sixty_seconds_before_it_ends()
    {
        var ends = Now.AddMinutes(10);
        Assert.True(TokenLifetime.IsUsable(ends, Now));
        Assert.True(TokenLifetime.IsUsable(ends, ends.AddSeconds(-61)));
        Assert.False(TokenLifetime.IsUsable(ends, ends.AddSeconds(-60)));
        Assert.False(TokenLifetime.IsUsable(ends, ends.AddSeconds(-59)));
        Assert.False(TokenLifetime.IsUsable(ends, ends.AddSeconds(1)));
        Assert.False(TokenLifetime.IsUsable(null, Now));
        Assert.Equal(Now.AddSeconds(90), TokenLifetime.ExpiresAt(Now, 90));
    }

    // ---- secrets -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("abcdefgh", "efgh")]
    [InlineData("0123456789", "6789")]
    [InlineData("abcdefg", null)]       // shorter than 8: a four-character hint would give away too much
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_secret_hint_is_the_last_four_characters_or_nothing_for_a_short_secret(string? secret, string? hint) => Assert.Equal(hint, SecretRules.Hint(secret));

    [Fact]
    public void A_secret_may_not_be_empty_too_long_or_contain_control_characters()
    {
        Assert.Null(SecretRules.Check("sk_live a b c"));
        Assert.NotNull(SecretRules.Check(""));
        Assert.NotNull(SecretRules.Check("line1\nline2"));
        Assert.NotNull(SecretRules.Check("tab\there"));
        Assert.NotNull(SecretRules.Check(new string('x', SecretRules.MaxLength + 1)));
    }

    [Fact]
    public void Error_text_loses_secrets_in_every_form_control_characters_and_excess_length()
    {
        var secrets = new[] { "p@ss w0rd/+=", "tok-123-abc" };
        var text = $"failed: p@ss w0rd/+= and {Uri.EscapeDataString("p@ss w0rd/+=")} and {Convert.ToBase64String("p@ss w0rd/+="u8.ToArray())} and tok-123-abc\r\nnext line\u0007";
        var clean = ErrorSanitizer.Clean(text, secrets);
        foreach (var s in new[] { "p@ss w0rd/+=", Uri.EscapeDataString("p@ss w0rd/+="), Convert.ToBase64String("p@ss w0rd/+="u8.ToArray()), "tok-123-abc" })
            Assert.DoesNotContain(s, clean, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', clean);
        Assert.DoesNotContain('\r', clean);
        Assert.DoesNotContain('\u0007', clean);
        Assert.Contains("***", clean, StringComparison.Ordinal);
        Assert.True(ErrorSanitizer.Clean(new string('x', 5000), []).Length <= ErrorSanitizer.MaxLength + 3);
        Assert.Equal("", ErrorSanitizer.Clean(null, secrets));
    }

    [Fact]
    public void Redaction_replaces_the_longer_secret_first_so_one_secret_that_contains_another_leaves_no_remainder()
    {
        var clean = ErrorSanitizer.Clean("value=hunter2-long-secret", ["hunter2", "hunter2-long-secret"]);
        Assert.DoesNotContain("long-secret", clean, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", clean, StringComparison.Ordinal);
    }

    // ---- header, query and path rules ----------------------------------------------------------------------------

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("X-API-Key", true)]
    [InlineData("x_token.v2", true)]
    [InlineData("", false)]
    [InlineData("Bad Header", false)]
    [InlineData("Bad:Header", false)]
    [InlineData("X-Key\r\nX-Evil: 1", false)]
    [InlineData("X-Key\n", false)]
    [InlineData("Host", false)]
    [InlineData("content-length", false)]
    [InlineData("Transfer-Encoding", false)]
    [InlineData("Cookie", false)]
    [InlineData("Proxy-Authorization", false)]
    [InlineData("Sec-Fetch-Mode", false)]
    [InlineData("X-Forwarded-For", false)]
    [InlineData("Ünicode", false)]
    public void Only_safe_header_names_may_carry_a_credential(string name, bool ok) => Assert.Equal(ok, HeaderRules.CheckName(name) is null);

    [Fact]
    public void A_header_name_has_a_length_limit()
    {
        Assert.NotNull(HeaderRules.CheckName(new string('a', HeaderRules.MaxHeaderNameLength + 1)));
        Assert.Null(HeaderRules.CheckName(new string('a', HeaderRules.MaxHeaderNameLength)));
    }

    [Theory]
    [InlineData("Bearer ", true)]
    [InlineData("Token ", true)]
    [InlineData("", true)]
    [InlineData("Bearer\r\n", false)]
    [InlineData("Be\tarer ", false)]
    [InlineData("Bärer ", false)]
    public void A_header_prefix_is_printable_ascii_only(string prefix, bool ok) => Assert.Equal(ok, HeaderRules.CheckPrefix(prefix) is null);

    [Theory]
    [InlineData("api_key", true)]
    [InlineData("filter[key]", true)]
    [InlineData("a b", false)]
    [InlineData("a&b=c", false)]
    [InlineData("", false)]
    public void Query_parameter_names_are_plain(string name, bool ok) => Assert.Equal(ok, HeaderRules.CheckQueryName(name) is null);

    [Theory]
    [InlineData("/health", true)]
    [InlineData("health", true)]
    [InlineData("/v1/ping?verbose=1", true)]
    [InlineData("", false)]
    [InlineData("https://other.example.test/x", false)]
    [InlineData("//other.example.test/x", false)]
    [InlineData("/a b", false)]
    [InlineData("/../admin", false)]
    [InlineData("/a/../b", false)]
    [InlineData("/x#frag", false)]
    [InlineData("/x\r\nHost: evil", false)]
    [InlineData("\\\\host\\share", false)]
    [InlineData("/x/{{password}}", false)]
    public void A_request_path_stays_on_the_connections_address(string path, bool ok) => Assert.Equal(ok, ConnectorUrls.CheckPath(path) is null);

    [Fact]
    public void Paths_are_added_to_the_base_address_keeping_its_own_path_and_the_target_has_no_query()
    {
        Assert.Equal("https://api.example.test/v2/ping", ConnectorUrls.Resolve("https://api.example.test/v2/", "/ping")!.ToString());
        Assert.Equal("https://api.example.test/v2/ping?x=1", ConnectorUrls.Resolve("https://api.example.test/v2", "ping?x=1")!.ToString());
        Assert.Null(ConnectorUrls.Resolve("https://api.example.test", "https://evil.example.test/x"));
        Assert.Null(ConnectorUrls.Resolve("not a url", "/x"));
        Assert.Equal("https://auth.example.test/login", ConnectorUrls.ResolveUrlOrPath("https://api.example.test", "https://auth.example.test/login")!.ToString());

        var withKey = ConnectorUrls.WithQuery(new Uri("https://api.example.test/v1/things?page=2"), [new("api_key", "a b&c")]);
        Assert.Equal("https://api.example.test/v1/things?page=2&api_key=a%20b%26c", withKey.AbsoluteUri);
        Assert.Equal("api.example.test/v1/things", ConnectorUrls.Target(withKey));
        Assert.Equal("127.0.0.1:8080/x", ConnectorUrls.Target(new Uri("http://user@127.0.0.1:8080/x?token=abc#f")));
    }

    // ---- what each strategy sends --------------------------------------------------------------------------------

    private static HttpAuthSettings Settings(string strategy, params (string Key, JsonElement Value)[] more)
    {
        var config = new Dictionary<string, JsonElement> { [FieldKeys.AuthStrategy] = S(strategy), [FieldKeys.BaseUrl] = S("https://api.example.test/v1") };
        foreach (var (k, v) in more) config[k] = v;
        return HttpAuthSettings.Read(config);
    }

    [Fact]
    public void An_api_key_goes_in_a_named_header_or_a_query_parameter()
    {
        var header = HttpAuth.ForStatic(Settings(AuthStrategies.ApiKey, (FieldKeys.ApiKeyName, S("X-Key"))), Values(("apiKey", "k-123")));
        Assert.Equal([new KeyValuePair<string, string>("X-Key", "k-123")], header.Addition!.Headers);
        Assert.Empty(header.Addition.Query);

        var query = HttpAuth.ForStatic(Settings(AuthStrategies.ApiKey, (FieldKeys.ApiKeyName, S("api_key")), (FieldKeys.ApiKeyIn, S("query"))), Values(("apiKey", "k-123")));
        Assert.Equal([new KeyValuePair<string, string>("api_key", "k-123")], query.Addition!.Query);
        Assert.Empty(query.Addition.Headers);
    }

    [Fact]
    public void Bearer_basic_and_none_build_the_expected_authorization()
    {
        Assert.Equal("Bearer tok-1", HttpAuth.ForStatic(Settings(AuthStrategies.Bearer), Values(("bearerToken", "tok-1"))).Addition!.Headers.Single().Value);
        var basic = HttpAuth.ForStatic(Settings(AuthStrategies.Basic, (FieldKeys.Username, S("ann"))), Values(("password", "p:w")));
        Assert.Equal("Basic " + Convert.ToBase64String("ann:p:w"u8.ToArray()), basic.Addition!.Headers.Single().Value);   // a colon in the password is fine
        Assert.Equal(AuthAddition.Empty, HttpAuth.ForStatic(Settings(AuthStrategies.None), _ => null).Addition);
    }

    [Fact]
    public void A_missing_credential_is_reported_by_name_without_building_a_request()
    {
        Assert.Contains("API key", HttpAuth.ForStatic(Settings(AuthStrategies.ApiKey, (FieldKeys.ApiKeyName, S("X-Key"))), _ => null).Error, StringComparison.Ordinal);
        Assert.Contains("bearer token", HttpAuth.ForStatic(Settings(AuthStrategies.Bearer), _ => null).Error, StringComparison.Ordinal);
        Assert.Contains("password", HttpAuth.ForStatic(Settings(AuthStrategies.Basic, (FieldKeys.Username, S("ann"))), _ => null).Error, StringComparison.Ordinal);
        Assert.NotNull(HttpAuth.ForStatic(Settings(AuthStrategies.LoginCall), _ => null).Error);
    }

    [Fact]
    public void A_fetched_token_is_sent_in_the_configured_header_with_the_configured_prefix()
    {
        Assert.Equal(new KeyValuePair<string, string>("Authorization", "Bearer t0k"), HttpAuth.ForToken(Settings(AuthStrategies.LoginCall), "t0k").Headers.Single());
        var custom = Settings(AuthStrategies.LoginCall, (FieldKeys.TokenHeader, S("X-Auth-Token")), (FieldKeys.TokenPrefix, S("")));
        Assert.Equal(new KeyValuePair<string, string>("X-Auth-Token", "t0k"), HttpAuth.ForToken(custom, "t0k").Headers.Single());
    }

    [Fact]
    public void Oauth_client_credentials_posts_a_form_with_the_client_and_optional_scope()
    {
        var s = Settings(AuthStrategies.OAuth2ClientCredentials, (FieldKeys.TokenUrl, S("https://auth.example.test/token")), (FieldKeys.ClientId, S("client 1")), (FieldKeys.Scope, S("read write")));
        var (request, error) = HttpAuth.BuildTokenRequest(s, Values(("clientSecret", "s&cret")));
        Assert.Null(error);
        Assert.Equal("POST", request!.Method);
        Assert.Equal("https://auth.example.test/token", request.Url.ToString());
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("grant_type=client_credentials&client_id=client%201&client_secret=s%26cret&scope=read%20write", request.Body);
        Assert.NotNull(HttpAuth.BuildTokenRequest(s, _ => null).Error);

        var token = HttpAuth.ReadTokenResponse(s, """{"access_token":"abc","expires_in":120}""");
        Assert.Equal(("abc", 120), (token.Token, token.LifetimeSeconds));
    }

    [Fact]
    public void A_login_call_builds_its_request_from_the_template_the_method_and_a_path_or_a_full_address()
    {
        var s = Settings(AuthStrategies.LoginCall, (FieldKeys.LoginUrl, S("/auth/login")), (FieldKeys.LoginBodyType, S("json")),
            (FieldKeys.LoginBody, S("""{"u":"{{username}}","p":"{{password}}"}""")), (FieldKeys.Username, S("ann")), (FieldKeys.TokenPointer, S("/data/jwt")));
        var (request, error) = HttpAuth.BuildTokenRequest(s, Values(("password", "pw\"1")));
        Assert.Null(error);
        Assert.Equal(("POST", "https://api.example.test/v1/auth/login", "application/json"), (request!.Method, request.Url.ToString(), request.ContentType));
        using (var sent = JsonDocument.Parse(request.Body!))
            Assert.Equal(("ann", "pw\"1"), (sent.RootElement.GetProperty("u").GetString(), sent.RootElement.GetProperty("p").GetString()));
        Assert.Equal("jwt-1", HttpAuth.ReadTokenResponse(s, """{"data":{"jwt":"jwt-1"}}""").Token);

        var elsewhere = Settings(AuthStrategies.LoginCall, (FieldKeys.LoginUrl, S("https://sso.example.test/login")), (FieldKeys.LoginBodyType, S("none")), (FieldKeys.LoginMethod, S("PUT")));
        var other = HttpAuth.BuildTokenRequest(elsewhere, _ => null).Request!;
        Assert.Equal(("PUT", "https://sso.example.test/login", null), (other.Method, other.Url.ToString(), other.Body));
    }

    [Fact]
    public void A_login_call_whose_body_needs_a_missing_value_builds_nothing()
    {
        var s = Settings(AuthStrategies.LoginCall, (FieldKeys.LoginUrl, S("/login")), (FieldKeys.LoginBody, S("""{"p":"{{password}}"}""")));
        var (request, error) = HttpAuth.BuildTokenRequest(s, _ => null);
        Assert.Null(request);
        Assert.Contains("{{password}}", error, StringComparison.Ordinal);
    }

    // ---- validation against descriptors --------------------------------------------------------------------------

    private static readonly ConnectorDescriptor Http = ShippedConnectors.Http;

    private static Dictionary<string, JsonElement> Cfg(params (string Key, JsonElement Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    [Fact]
    public void Unknown_settings_unknown_secrets_and_secrets_in_the_wrong_place_are_refused()
    {
        var check = ConnectionRules.CheckInput(Http, Cfg(("bogus", S("x")), ("apiKey", S("leak")), ("baseUrl", S("https://api.example.test"))),
            new Dictionary<string, string?> { ["nope"] = "x", ["baseUrl"] = "https://api.example.test" }, ["authStrategy", "unknown"]);
        Assert.Contains("config.bogus", check.Errors.Keys);
        Assert.Contains("config.apiKey", check.Errors.Keys);        // a secret sent as config
        Assert.Contains("secrets.nope", check.Errors.Keys);
        Assert.Contains("secrets.baseUrl", check.Errors.Keys);      // a non-secret sent as a secret
        Assert.Contains("clearSecrets", check.Errors.Keys);
        Assert.DoesNotContain("config.baseUrl", check.Errors.Keys);
        Assert.False(check.Config.ContainsKey("apiKey"));
    }

    [Fact]
    public void Values_are_checked_for_type_url_scheme_options_numbers_and_length()
    {
        var long501 = new string('a', ConnectionRules.MaxTextLength + 1);
        var check = ConnectionRules.CheckInput(Http, Cfg(
            ("baseUrl", S("ftp://api.example.test")), ("testPath", J("12")), ("authStrategy", S("magic")), ("apiKeyName", S(long501)),
            ("expirySeconds", J("12.5")), ("loginBody", J("true")), ("scope", S("a\nb"))), null, null);
        foreach (var key in new[] { "baseUrl", "testPath", "authStrategy", "apiKeyName", "expirySeconds", "loginBody", "scope" })
            Assert.Contains("config." + key, check.Errors.Keys);
        Assert.Contains("http", check.Errors["config.baseUrl"][0], StringComparison.Ordinal);

        Assert.Contains("config.expirySeconds", ConnectionRules.CheckInput(Http, Cfg(("expirySeconds", J("59"))), null, null).Errors.Keys);
        Assert.Contains("config.expirySeconds", ConnectionRules.CheckInput(Http, Cfg(("expirySeconds", J("86401"))), null, null).Errors.Keys);
        Assert.DoesNotContain("config.expirySeconds", ConnectionRules.CheckInput(Http, Cfg(("expirySeconds", J("3600"))), null, null).Errors.Keys);
        Assert.Contains("config.baseUrl", ConnectionRules.CheckInput(Http, Cfg(("baseUrl", S("https://user:pw@api.example.test"))), null, null).Errors.Keys);
        Assert.Contains("config.baseUrl", ConnectionRules.CheckInput(Http, Cfg(("baseUrl", S("https://api.example.test/" + new string('a', 2000)))), null, null).Errors.Keys);
    }

    [Fact]
    public void Text_is_trimmed_empty_values_mean_unset_and_defaults_fill_only_the_fields_that_apply()
    {
        var check = ConnectionRules.CheckInput(Http, Cfg(("baseUrl", S("  https://api.example.test  ")), ("testPath", S("   ")), ("authStrategy", S("bearer")), ("username", JsonDocument.Parse("null").RootElement)), null, null);
        Assert.Empty(check.Errors);
        Assert.Equal("https://api.example.test", check.Config["baseUrl"].GetString());
        Assert.False(check.Config.ContainsKey("testPath"));
        Assert.False(check.Config.ContainsKey("username"));
        // Bearer sends a stored token, so none of the fields of the other strategies get a default.
        Assert.False(check.Config.ContainsKey("tokenHeader"));
        Assert.False(check.Config.ContainsKey("loginBody"));
        Assert.False(check.Config.ContainsKey("apiKeyName"));

        var login = ConnectionRules.CheckInput(Http, Cfg(("authStrategy", S("login_call"))), null, null);
        Assert.Equal("POST", login.Config["loginMethod"].GetString());
        Assert.Equal("/access_token", login.Config["tokenPointer"].GetString());
        Assert.Equal("Bearer ", login.Config["tokenPrefix"].GetString());     // verbatim: the trailing space survives
        Assert.Contains("{{username}}", login.Config["loginBody"].GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_token_prefix_is_kept_exactly_as_typed()
    {
        var check = ConnectionRules.CheckInput(Http, Cfg(("authStrategy", S("login_call")), ("tokenPrefix", S("Token "))), null, null);
        Assert.Equal("Token ", check.Config["tokenPrefix"].GetString());
        Assert.Contains("config.tokenPrefix", ConnectionRules.CheckInput(Http, Cfg(("authStrategy", S("login_call")), ("tokenPrefix", S("Bearer\r\nX: 1"))), null, null).Errors.Keys);
    }

    [Fact]
    public void Secrets_are_trimmed_an_empty_one_is_left_alone_and_one_cannot_be_sent_and_cleared()
    {
        var check = ConnectionRules.CheckInput(Http, Cfg(("authStrategy", S("api_key"))),
            new Dictionary<string, string?> { ["apiKey"] = "  key-12345678  ", ["password"] = "", ["bearerToken"] = null }, ["clientSecret"]);
        Assert.Empty(check.Errors);
        Assert.Equal(new Dictionary<string, string> { ["apiKey"] = "key-12345678" }, check.Secrets);
        Assert.Equal(["clientSecret"], check.Clear);

        var both = ConnectionRules.CheckInput(Http, Cfg(("authStrategy", S("api_key"))), new Dictionary<string, string?> { ["apiKey"] = "new-key-value" }, ["apiKey"]);
        Assert.Contains("secrets.apiKey", both.Errors.Keys);
        Assert.Contains("secrets.apiKey", ConnectionRules.CheckInput(Http, Cfg(), new Dictionary<string, string?> { ["apiKey"] = "a\nb" }, null).Errors.Keys);
    }

    [Fact]
    public void An_absurd_number_of_keys_is_refused_with_one_error_not_one_per_key()
    {
        var many = Enumerable.Range(0, ConnectionRules.MaxKeys + 1).ToDictionary(i => $"k{i}", _ => S("x"));
        var check = ConnectionRules.CheckInput(Http, many, null, null);
        Assert.Equal(["config"], check.Errors.Keys.ToArray());
        Assert.Empty(check.Config);
    }

    private static Dictionary<string, string[]> Complete(string strategy, Dictionary<string, JsonElement> extra, params string[] secretsSet)
    {
        var config = Cfg(("baseUrl", S("https://api.example.test")), ("authStrategy", S(strategy)));
        foreach (var (k, v) in extra) config[k] = v;
        var check = ConnectionRules.CheckInput(Http, config, null, null);
        Assert.Empty(check.Errors);
        return ConnectionRules.CheckComplete(Http, check.Config, secretsSet.Contains);
    }

    [Fact]
    public void Each_strategy_needs_its_own_settings_and_secrets()
    {
        Assert.Empty(Complete("none", []));
        Assert.Empty(Complete("api_key", Cfg(("apiKeyName", S("X-Key"))), "apiKey"));
        Assert.Contains("secrets.apiKey", Complete("api_key", Cfg(("apiKeyName", S("X-Key")))).Keys);
        Assert.Contains("config.apiKeyName", Complete("api_key", Cfg(("apiKeyName", S("Host"))), "apiKey").Keys);       // an unsafe header name
        Assert.Empty(Complete("api_key", Cfg(("apiKeyIn", S("query")), ("apiKeyName", S("api_key"))), "apiKey"));
        Assert.Empty(Complete("bearer", [], "bearerToken"));
        Assert.Contains("secrets.bearerToken", Complete("bearer", []).Keys);
        Assert.Empty(Complete("basic", Cfg(("username", S("ann"))), "password"));
        Assert.Equal(["config.username", "secrets.password"], Complete("basic", []).Keys.Order().ToArray());
        Assert.Contains("config.username", Complete("basic", Cfg(("username", S("a:b"))), "password").Keys);
        Assert.Empty(Complete("oauth2_client_credentials", Cfg(("tokenUrl", S("https://auth.example.test/t")), ("clientId", S("c"))), "clientSecret"));
        Assert.Equal(["config.clientId", "config.tokenUrl", "secrets.clientSecret"], Complete("oauth2_client_credentials", []).Keys.Order().ToArray());
    }

    [Fact]
    public void A_login_call_is_checked_as_a_whole()
    {
        var ok = Cfg(("loginUrl", S("/auth/login")), ("username", S("ann")));
        Assert.Empty(Complete("login_call", ok, "password"));

        // The default body needs the user name and the password.
        Assert.Equal(["config.loginUrl", "config.username", "secrets.password"], Complete("login_call", []).Keys.Order().ToArray());
        Assert.Contains("secrets.password", Complete("login_call", ok).Keys);
        Assert.Contains("config.loginUrl", Complete("login_call", Cfg(("loginUrl", S("https://user:x@sso.example.test/login")), ("username", S("a"))), "password").Keys);
        Assert.Contains("config.loginUrl", Complete("login_call", Cfg(("loginUrl", S("login with spaces")), ("username", S("a"))), "password").Keys);
        Assert.Empty(Complete("login_call", Cfg(("loginUrl", S("https://sso.example.test/login")), ("username", S("a"))), "password"));

        var bad = Cfg(("loginUrl", S("/login")), ("username", S("a")), ("loginBody", S("""{"x":"{{nope}}"}""")), ("tokenPointer", S("access_token")), ("expiryPointer", S("expires")),
            ("tokenHeader", S("Content-Length")), ("tokenPrefix", S("aé")));
        var errors = Complete("login_call", bad, "password");
        foreach (var key in new[] { "config.loginBody", "config.tokenPointer", "config.expiryPointer", "config.tokenHeader", "config.tokenPrefix" }) Assert.Contains(key, errors.Keys);

        Assert.Empty(Complete("login_call", Cfg(("loginUrl", S("/login")), ("loginBodyType", S("none")), ("tokenPointer", S("/t"))), "password"));
        Assert.Empty(Complete("login_call", Cfg(("loginUrl", S("/login")), ("loginBodyType", S("form")), ("loginBody", S("k={{apiKey}}&c={{clientId}}")), ("clientId", S("c"))), "apiKey"));
        Assert.Equal(["secrets.apiKey"], Complete("login_call", Cfg(("loginUrl", S("/login")), ("loginBodyType", S("form")), ("loginBody", S("k={{apiKey}}")))).Keys.Order().ToArray());
    }

    [Fact]
    public void The_test_path_and_the_fixed_lifetime_are_checked_for_every_strategy()
    {
        Assert.Contains("config.testPath", Complete("none", Cfg(("testPath", S("https://other.example.test/x")))).Keys);
        Assert.Empty(Complete("none", Cfg(("testPath", S("/health")))));
        Assert.Empty(Complete("login_call", Cfg(("loginUrl", S("/l")), ("username", S("a")), ("expirySeconds", J("600"))), "password"));
    }

    [Fact]
    public void A_connection_needs_its_always_required_settings_and_a_known_strategy()
    {
        var errors = ConnectionRules.CheckComplete(Http, new Dictionary<string, JsonElement>(), _ => false);
        Assert.Contains("config.baseUrl", errors.Keys);
        Assert.Contains("config.authStrategy", errors.Keys);
        var wrong = ConnectionRules.CheckComplete(Http, Cfg(("baseUrl", S("https://x.example.test")), ("authStrategy", S("magic"))), _ => true);
        Assert.Contains("config.authStrategy", wrong.Keys);   // not one of the strategies the connector offers
    }

    [Fact]
    public void A_name_is_required_trimmed_and_limited()
    {
        Assert.Null(ConnectionRules.CheckName(" Acme PADS4 "));
        Assert.NotNull(ConnectionRules.CheckName("  "));
        Assert.NotNull(ConnectionRules.CheckName(null));
        Assert.NotNull(ConnectionRules.CheckName(new string('n', ConnectionRules.MaxNameLength + 1)));
        Assert.NotNull(ConnectionRules.CheckName("a\nb"));
    }

    [Fact]
    public void The_changed_settings_are_named()
    {
        var before = Cfg(("baseUrl", S("https://a.example.test")), ("testPath", S("/x")));
        var after = Cfg(("baseUrl", S("https://b.example.test")), ("scope", S("s")));
        Assert.Equal(["baseUrl", "scope", "testPath"], ConnectionRules.ChangedKeys(before, after));
        Assert.Empty(ConnectionRules.ChangedKeys(before, before));
    }

    // ---- descriptors and the registry ----------------------------------------------------------------------------

    public static IEnumerable<object[]> Descriptors() => [[ShippedConnectors.Http], [ShippedConnectors.Pads4], [ShippedConnectors.Canva]];

    [Theory]
    [MemberData(nameof(Descriptors))]
    public void Every_descriptor_is_consistent(ConnectorDescriptor d)
    {
        Assert.Equal(d.Fields.Count, d.Fields.Select(f => f.Key).Distinct().Count());
        foreach (var f in d.Fields)
        {
            Assert.Contains(f.Type, FieldTypes.All);
            Assert.Contains(f.Group, FieldGroups.All);
            Assert.False(string.IsNullOrWhiteSpace(f.Label));
            if (f.ShowWhen is { } w)
            {
                Assert.True(d.Field(w.Field) is { Type: FieldTypes.Select }, $"{f.Key} shows by a field that is not a select");
                Assert.All(w.Values, v => Assert.Contains(d.Field(w.Field)!.Options!, o => o.Value == v));
            }
            if (f.Type == FieldTypes.Select) Assert.NotEmpty(f.Options!);
            if (f.IsSecret) Assert.Null(f.Default);
            if (f.Default is { } def)
            {
                // A default must pass the field's own validation.
                var check = ConnectionRules.CheckInput(d, new Dictionary<string, JsonElement> { [f.Key] = def }, null, null);
                Assert.DoesNotContain(check.Errors.Keys, k => k == "config." + f.Key);
            }
        }
        Assert.Contains(d.Status, ConnectorStatuses.All);
        if (d.DefaultAuthStrategy is { } s) Assert.Contains(s, d.AuthStrategies);
        if (d.Fields.Count > 0 && d.AuthStrategies.Count > 0)
            Assert.Equal(d.AuthStrategies, d.Field(FieldKeys.AuthStrategy)!.Options!.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void The_shipped_connectors_are_http_available_pads4_preview_and_canva_planned()
    {
        Assert.Equal((ConnectorStatuses.Available, AuthStrategies.ApiKey), (ShippedConnectors.Http.Status, ShippedConnectors.Http.DefaultAuthStrategy));
        Assert.Equal((ConnectorStatuses.Preview, (string?)null), (ShippedConnectors.Pads4.Status, ShippedConnectors.Pads4.DefaultAuthStrategy));
        Assert.False(ShippedConnectors.Canva.CanBeCreated);
        Assert.Empty(ShippedConnectors.Canva.Fields);
        Assert.Equal(HttpAuthSettings.DefaultTokenPrefix, ShippedConnectors.Http.Field(FieldKeys.TokenPrefix)!.Default!.Value.GetString());
        Assert.All(ShippedConnectors.Http.Fields.Where(f => f.IsSecret), f => Assert.Equal(FieldGroups.Authentication, f.Group));
    }

    [Fact]
    public void The_registry_lists_available_then_preview_then_planned_and_finds_by_key()
    {
        var registry = new ConnectorRegistry([new PlannedConnector(ShippedConnectors.Canva), new PlannedConnector(ShippedConnectors.Pads4), new PlannedConnector(ShippedConnectors.Http)]);
        Assert.Equal(["http", "pads4", "canva"], registry.All.Select(c => c.Descriptor.Key).ToArray());
        Assert.Same(ShippedConnectors.Pads4, registry.Find("pads4")!.Descriptor);
        Assert.Null(registry.Find("nope"));
        Assert.Null(registry.Find(null));
    }

    // ---- the entity ----------------------------------------------------------------------------------------------

    private static IntegrationConnection NewConnection(Dictionary<string, StoredSecret>? secrets = null)
    {
        var id = Guid.NewGuid();
        return IntegrationConnection.Create(id, Guid.NewGuid(), "http", " Acme Orders ", true, Cfg(("baseUrl", S("https://api.example.test"))),
            secrets ?? new Dictionary<string, StoredSecret> { ["apiKey"] = new("v1.CIPHERTEXT", "wxyz") }, Guid.NewGuid(), Now);
    }

    [Fact]
    public void Events_name_the_connection_and_what_changed_but_never_carry_a_secret_or_a_cipher()
    {
        var c = NewConnection();
        c.Update("Acme Orders", true, Cfg(("baseUrl", S("https://other.example.test"))), new Dictionary<string, StoredSecret> { ["apiKey"] = new("v1.CIPHERTEXT-2", "abcd") }, ["apiKey"], Now.AddMinutes(1));
        c.RecordTest(false, "The server answered 500.", 120, Now.AddMinutes(2));
        c.MarkDeleted();
        var events = c.PullEvents();
        Assert.Equal(["integration.connection.created", "integration.connection.updated", "integration.connection.tested", "integration.connection.deleted"], events.Select(e => e.Name).ToArray());
        var updated = Assert.IsType<IntegrationConnectionUpdated>(events[1]);
        Assert.Equal(["config.baseUrl", "secrets.apiKey"], updated.Changed);
        var json = string.Join("\n", events.Select(e => JsonSerializer.Serialize(e, e.GetType())));
        Assert.DoesNotContain("CIPHERTEXT", json, StringComparison.Ordinal);
        Assert.DoesNotContain("wxyz", json, StringComparison.Ordinal);
        Assert.Contains("\"Acme Orders\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_settings_or_secrets_drops_the_saved_token_and_the_test_result_but_renaming_does_not()
    {
        var c = NewConnection();
        c.RecordTest(true, null, 50, Now);
        Assert.Equal(ConnectionStatuses.Ok, c.Status);
        Assert.Equal(Now, c.LastOkAt);

        c.Update("Renamed", false, c.Config, c.Secrets, [], Now.AddMinutes(1));
        Assert.Equal(ConnectionStatuses.Ok, c.Status);

        c.Update("Renamed", false, Cfg(("baseUrl", S("https://moved.example.test"))), c.Secrets, [], Now.AddMinutes(2));
        Assert.Equal((ConnectionStatuses.New, null, null), (c.Status, c.StatusDetail, c.TokenCipher));
        Assert.Equal(Now, c.LastOkAt);   // history of the last good test is kept
    }

    [Fact]
    public void A_failed_test_records_a_short_detail_and_a_good_one_clears_it()
    {
        var c = NewConnection();
        c.RecordTest(false, new string('x', 900), 10, Now);
        Assert.Equal((ConnectionStatuses.Error, 500), (c.Status, c.StatusDetail!.Length));
        Assert.Null(c.LastOkAt);
        c.RecordTest(true, "ignored", 10, Now.AddMinutes(1));
        Assert.Equal((ConnectionStatuses.Ok, null), (c.Status, c.StatusDetail));
    }

    [Fact]
    public void Ciphertexts_are_bound_to_their_connection_field_and_use()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.NotEqual(IntegrationSecretPurposes.Field(a, "apiKey"), IntegrationSecretPurposes.Field(b, "apiKey"));
        Assert.NotEqual(IntegrationSecretPurposes.Field(a, "apiKey"), IntegrationSecretPurposes.Field(a, "password"));
        Assert.NotEqual(IntegrationSecretPurposes.Field(a, "token"), IntegrationSecretPurposes.Token(a));
    }

    [Fact]
    public void The_new_permission_is_known_and_the_admin_alone_holds_it()
    {
        Assert.Contains(Dam.Domain.Authorization.Permissions.IntegrationsManage, Dam.Domain.Authorization.Permissions.All);
        Assert.Equal("integrations.manage", Dam.Domain.Authorization.Permissions.IntegrationsManage);
        var holders = Dam.Domain.Authorization.BuiltInRoles.All.Where(r => r.Permissions.Contains(Dam.Domain.Authorization.Permissions.IntegrationsManage)).Select(r => r.Name).ToArray();
        Assert.Equal([Dam.Domain.Authorization.BuiltInRoles.Admin], holders);
    }

    // ---- PADS4: a stored-credentials connector (nothing is called) -----------------------------------------------

    private static ConnectorDescriptor Pads4 => ShippedConnectors.Pads4;

    private sealed class ThrowingGuard : Dam.Application.Abstractions.IOutboundUrlGuard
    {
        public int Calls { get; private set; }
        public Task<string?> CheckAsync(string url, CancellationToken ct) { Calls++; return Task.FromResult<string?>("The host name could not be resolved."); }
    }

    [Fact]
    public void The_pads4_descriptor_has_exactly_four_required_connection_fields_and_no_strategy_or_capability()
    {
        Assert.Equal(("pads4", "PADS4", ConnectorStatuses.Preview), (Pads4.Key, Pads4.Name, Pads4.Status));
        Assert.Empty(Pads4.AuthStrategies);
        Assert.Null(Pads4.DefaultAuthStrategy);
        Assert.Empty(Pads4.Capabilities);
        Assert.False(Pads4.CanTest);
        Assert.False(Pads4.MakesCalls);
        Assert.Equal(
            [("baseUrl", "PADS4 server URL", FieldTypes.Url), ("domain", "Domain", FieldTypes.Text), ("username", "Username", FieldTypes.Text), ("password", "Password", FieldTypes.Secret)],
            Pads4.Fields.Select(f => (f.Key, f.Label, f.Type)).ToArray());
        Assert.All(Pads4.Fields, f =>
        {
            Assert.True(f.Required);
            Assert.Equal(FieldGroups.Connection, f.Group);
            Assert.Null(f.ShowWhen);
            Assert.Null(f.Default);
        });
        Assert.Contains("https://pads4.example.test", Pads4.Field("baseUrl")!.Help, StringComparison.Ordinal);
        Assert.Contains("sign-in domain", Pads4.Field("domain")!.Help, StringComparison.Ordinal);
        Assert.Equal("Saved. Orbit does not connect to PADS4 yet.", Pads4.SavedOnlyDetail);
        Assert.Null(Http.SavedOnlyDetail);
        Assert.True(Http.CanTest && Http.MakesCalls);
    }

    [Fact]
    public void A_pads4_connection_needs_all_four_values_and_names_them_as_config_or_secrets()
    {
        var empty = ConnectionRules.CheckComplete(Pads4, new Dictionary<string, JsonElement>(), _ => false);
        Assert.Equal(["config.baseUrl", "config.domain", "config.username", "secrets.password"], empty.Keys.Order(StringComparer.Ordinal).ToArray());
        var full = Cfg(("baseUrl", S("https://pads4.example.test")), ("domain", S("acme")), ("username", S("ann")));
        Assert.Empty(ConnectionRules.CheckComplete(Pads4, full, k => k == "password"));
        // A kept (already stored) secret counts; a missing one does not.
        Assert.Equal(["secrets.password"], ConnectionRules.CheckComplete(Pads4, full, _ => false).Keys.ToArray());
    }

    [Theory]
    [InlineData("http://192.168.1.20:8080")]
    [InlineData("http://pads4.internal.example.test")]
    [InlineData("https://10.0.0.5/pads4")]
    [InlineData("http://localhost:8080")]
    public void A_pads4_server_may_be_on_a_private_network_so_the_save_time_check_is_syntax_only(string url)
    {
        var check = ConnectionRules.CheckInput(Pads4, Cfg(("baseUrl", S(url))), null, null);
        Assert.Empty(check.Errors);
    }

    [Theory]
    [InlineData("ftp://pads4.example.test")]
    [InlineData("pads4.example.test")]
    [InlineData("https://ann:secret@pads4.example.test")]
    public void The_scheme_and_shape_rules_still_apply_to_the_pads4_address(string url)
    {
        Assert.Contains("config.baseUrl", ConnectionRules.CheckInput(Pads4, Cfg(("baseUrl", S(url))), null, null).Errors.Keys);
    }

    [Fact]
    public void The_pads4_length_rules_apply_and_strategy_settings_are_unknown_to_it()
    {
        var tooLong = ConnectionRules.CheckInput(Pads4, Cfg(("baseUrl", S("https://pads4.example.test/" + new string('a', ConnectionRules.MaxUrlLength))), ("domain", S(new string('d', ConnectionRules.MaxTextLength + 1)))), null, null);
        Assert.Contains("config.baseUrl", tooLong.Errors.Keys);
        Assert.Contains("config.domain", tooLong.Errors.Keys);
        var unknown = ConnectionRules.CheckInput(Pads4, Cfg(("authStrategy", S("login_call")), ("loginUrl", S("/login"))), new Dictionary<string, string?> { ["apiKey"] = "x-1234567", ["password"] = "pw-12345678" }, null);
        Assert.Contains("config.authStrategy", unknown.Errors.Keys);
        Assert.Contains("config.loginUrl", unknown.Errors.Keys);
        Assert.Contains("secrets.apiKey", unknown.Errors.Keys);
        Assert.DoesNotContain("secrets.password", unknown.Errors.Keys);
        // The password is a secret: it may not travel in config.
        Assert.Contains("config.password", ConnectionRules.CheckInput(Pads4, Cfg(("password", S("pw-12345678"))), null, null).Errors.Keys);
    }

    [Fact]
    public async Task The_outbound_guard_is_not_asked_about_a_pads4_address_but_is_for_a_connector_that_makes_calls()
    {
        var guard = new ThrowingGuard();
        var pads4 = await Dam.Application.Integrations.CreateConnectionHandler.UrlProblemsAsync(guard, Pads4, Cfg(("baseUrl", S("http://192.168.1.20:8080"))), null, default);
        Assert.Empty(pads4);
        Assert.Equal(0, guard.Calls);

        var http = await Dam.Application.Integrations.CreateConnectionHandler.UrlProblemsAsync(guard, Http, Cfg(("baseUrl", S("http://192.168.1.20:8080"))), null, default);
        Assert.Equal(["config.baseUrl"], http.Keys.ToArray());
        Assert.Equal(1, guard.Calls);
    }

    [Fact]
    public async Task The_stored_credentials_connector_makes_no_test()
    {
        var connector = new StoredCredentialsConnector(Pads4);
        Assert.Same(Pads4, connector.Descriptor);
        var result = await connector.TestAsync(NewConnection(), default);
        Assert.False(result.Ok);
        Assert.Equal("Testing is not available for this connector yet.", Assert.Single(result.Steps).Detail);
    }

    [Fact]
    public void A_saved_only_connection_keeps_its_saved_detail_through_updates_that_reset_the_status()
    {
        const string detail = "Saved. Orbit does not connect to PADS4 yet.";
        var c = IntegrationConnection.Create(Guid.NewGuid(), Guid.NewGuid(), "pads4", "PADS4", true, Cfg(("baseUrl", S("http://192.168.1.20:8080"))),
            new Dictionary<string, StoredSecret> { ["password"] = new("v1.CIPHER", "x9z1") }, null, Now, detail);
        Assert.Equal((ConnectionStatuses.New, detail), (c.Status, c.StatusDetail));
        c.Update("PADS4", true, Cfg(("baseUrl", S("http://192.168.1.21:8080"))), c.Secrets, [], Now.AddMinutes(1), detail);
        Assert.Equal((ConnectionStatuses.New, detail), (c.Status, c.StatusDetail));
        c.Update("Renamed", true, c.Config, c.Secrets, [], Now.AddMinutes(2), detail);
        Assert.Equal(detail, c.StatusDetail);
        var json = string.Join("\n", c.PullEvents().Select(e => JsonSerializer.Serialize(e, e.GetType())));
        Assert.DoesNotContain("CIPHER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("x9z1", json, StringComparison.Ordinal);
    }
}
