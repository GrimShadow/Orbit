using Dam.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Dam.IntegrationTests;

public sealed class OidcBackchannelTests
{
    private sealed class Recorder : HttpMessageHandler
    {
        public Uri? Seen { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }

    private static async Task<Uri?> Send(string url)
    {
        var recorder = new Recorder();
        using var client = new HttpClient(new OidcBackchannelHandler(new Uri("https://demo.example.net/realms/acme"), new Uri("http://localhost:18080/realms/acme/.well-known/openid-configuration"), recorder));
        await client.GetAsync(url);
        return recorder.Seen;
    }

    [Fact]
    public async Task Requests_to_the_public_authority_go_to_the_metadata_origin()
    {
        var seen = await Send("https://demo.example.net/realms/acme/protocol/openid-connect/certs");
        Assert.Equal("http://localhost:18080/realms/acme/protocol/openid-connect/certs", seen!.ToString());
    }

    [Fact]
    public async Task Other_hosts_are_left_alone()
    {
        var seen = await Send("https://elsewhere.example.org/x?y=1");
        Assert.Equal("https://elsewhere.example.org/x?y=1", seen!.ToString());
    }

    private static JwtBearerOptions Options(string authority, bool requireHttps) => new() { Authority = authority, RequireHttpsMetadata = requireHttps };

    [Fact]
    public void Unset_leaves_the_options_alone()
    {
        var o = Options("https://demo.example.net/realms/acme", true);
        OidcBackchannel.Apply(o, null);
        Assert.Null(o.MetadataAddress);
        Assert.Null(o.BackchannelHttpHandler);
        Assert.True(o.RequireHttpsMetadata);
    }

    [Fact]
    public void A_local_http_metadata_address_is_allowed_for_an_https_authority()
    {
        var o = Options("https://demo.example.net/realms/acme", true);
        OidcBackchannel.Apply(o, "http://localhost:18080/realms/acme/.well-known/openid-configuration");
        Assert.Equal("http://localhost:18080/realms/acme/.well-known/openid-configuration", o.MetadataAddress);
        Assert.NotNull(o.BackchannelHttpHandler);
    }

    [Fact]
    public void An_http_authority_or_a_remote_http_metadata_address_is_refused_when_https_is_required()
    {
        Assert.Throws<InvalidOperationException>(() => OidcBackchannel.Apply(Options("http://demo.example.net/realms/acme", true), "http://localhost:18080/x"));
        Assert.Throws<InvalidOperationException>(() => OidcBackchannel.Apply(Options("https://demo.example.net/realms/acme", true), "http://idp.example.org/x"));
        OidcBackchannel.Apply(Options("http://demo.localhost:8088/realms/acme", false), "http://idp.example.org/x");   // dev: allowed
    }
}
