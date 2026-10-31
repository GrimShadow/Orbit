using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Dam.Api.Auth;

public static class OidcBackchannel
{
    /// <summary>
    /// DAM_OIDC_METADATA_ADDRESS (optional, default unset = unchanged): read the discovery document and signing keys from this address instead of
    /// from the authority, e.g. a Keycloak on this machine behind a tunnel. Tokens are still validated against the authority's issuer. HTTPS is
    /// still required for the authority when DAM_OIDC_REQUIRE_HTTPS is not "false"; the metadata address may be plain HTTP only on loopback.
    /// </summary>
    public static void Apply(JwtBearerOptions o, string? metadataAddress)
    {
        if (string.IsNullOrWhiteSpace(metadataAddress)) return;
        var authority = new Uri(o.Authority ?? throw new InvalidOperationException("DAM_OIDC_AUTHORITY is not set."));
        var metadata = new Uri(metadataAddress);
        if (o.RequireHttpsMetadata)
        {
            if (authority.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("DAM_OIDC_AUTHORITY must be https unless DAM_OIDC_REQUIRE_HTTPS=false.");
            if (metadata.Scheme != Uri.UriSchemeHttps && !metadata.IsLoopback)
                throw new InvalidOperationException("DAM_OIDC_METADATA_ADDRESS must be https unless it points at this machine (localhost).");
            o.RequireHttpsMetadata = false;   // the checks above replace the built-in one, which would reject a plain-HTTP loopback address
        }
        o.MetadataAddress = metadataAddress;
        o.BackchannelHttpHandler = new OidcBackchannelHandler(authority, metadata, new HttpClientHandler());
    }
}

/// <summary>
/// For setups where the identity provider is reached at one address by browsers (and named as the token issuer) and at another by this
/// server, e.g. a quick tunnel or an ingress in front of Keycloak. When DAM_OIDC_METADATA_ADDRESS is set, every back-channel request the
/// JWT handler makes to the authority's public origin (the discovery document and the signing keys it points to) is sent to the origin of
/// that metadata address instead. Tokens are still validated against the public issuer. Unset = no change in behaviour.
/// </summary>
public sealed class OidcBackchannelHandler(Uri publicAuthority, Uri metadataAddress, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && SameOrigin(uri, publicAuthority))
        {
            request.RequestUri = new UriBuilder(uri) { Scheme = metadataAddress.Scheme, Host = metadataAddress.Host, Port = metadataAddress.Port }.Uri;
            request.Headers.Host = null;
        }
        return base.SendAsync(request, cancellationToken);
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
}
