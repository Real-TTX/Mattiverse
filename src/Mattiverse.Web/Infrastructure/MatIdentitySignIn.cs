using Mattiverse.Core.Configuration;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Mattiverse.Web.Infrastructure;

/// <summary>
/// Mattiverse's own sign-in through MatIdentity (OpenID Connect, code flow with PKCE). The scheme always exists; its options are read from the settings
/// (<see cref="SystemConfig.Identity"/>) whenever they are built, so installing or removing MatIdentity needs no restart: the login page drops the cached
/// options before it sends someone to MatIdentity (<see cref="Refresh"/>). The browser goes to MatIdentity's public address; Mattiverse's own calls (discovery,
/// keys, token) go to its address inside Docker (<see cref="InternalUrlRewriter"/>).
/// </summary>
public sealed class MatIdentitySignIn : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public const string Scheme = "MatIdentity";

    /// <summary>The short cookie between the answer of MatIdentity and the local session (<c>/Account/External</c>).</summary>
    public const string ExternalScheme = "MatIdentity.External";

    private readonly JsonConfigStore _store;
    private readonly SystemConfigService _config;

    public MatIdentitySignIn(JsonConfigStore store, SystemConfigService config)
    {
        _store = store;
        _config = config;
    }

    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != Scheme)
        {
            return;
        }

        // Options are built synchronously; the settings are a small cached JSON file.
        var config = _store.GetAsync<SystemConfig>(SystemConfig.FileName).GetAwaiter().GetResult();
        var identity = config.Identity;
        options.SignInScheme = ExternalScheme;
        options.CallbackPath = "/signin-oidc";
        options.SignedOutCallbackPath = "/signout-callback-oidc";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SaveTokens = true;
        options.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email", "roles" })
        {
            options.Scope.Add(scope);
        }

        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "role";

        // Sign-out at MatIdentity names the client, so MatIdentity accepts the address it sends the person back to.
        options.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            context.ProtocolMessage.ClientId = context.Options.ClientId;
            return Task.CompletedTask;
        };

        if (identity is null)
        {
            // Not connected: values that pass the checks of the handler; the login page never sends anyone here then.
            options.Authority = "http://matidentity.invalid";
            options.ClientId = "mattiverse";
            options.RequireHttpsMetadata = false;
            return;
        }

        options.Authority = identity.Authority;
        options.ClientId = identity.ClientId;
        options.ClientSecret = _config.GetIdentitySecretAsync().GetAwaiter().GetResult();
        options.RequireHttpsMetadata = identity.Authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(identity.InternalUrl) && !string.Equals(identity.InternalUrl.TrimEnd('/'), identity.Authority.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
        {
            options.BackchannelHttpHandler = new InternalUrlRewriter(identity.Authority, identity.InternalUrl) { InnerHandler = new HttpClientHandler() };
        }
    }

    /// <summary>Drops the cached options, so the next use reads the settings again.</summary>
    public static void Refresh(IServiceProvider services)
        => services.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>().TryRemove(Scheme);
}

/// <summary>
/// Sends Mattiverse's server-to-server calls to MatIdentity to its address inside Docker: a request to the public address (from the discovery document) goes
/// to the internal one with the same path. The browser keeps using the public address, and the issuer of the tokens stays the public one.
/// </summary>
public sealed class InternalUrlRewriter : DelegatingHandler
{
    private readonly Uri _public;
    private readonly Uri _internal;

    public InternalUrlRewriter(string publicUrl, string internalUrl)
    {
        _public = new Uri(publicUrl.TrimEnd('/') + "/");
        _internal = new Uri(internalUrl.TrimEnd('/') + "/");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri = Rewrite(request.RequestUri);
        return base.SendAsync(request, cancellationToken);
    }

    public Uri? Rewrite(Uri? uri)
    {
        if (uri is null || !_public.IsBaseOf(uri))
        {
            return uri;
        }

        return new Uri(_internal, _public.MakeRelativeUri(uri));
    }
}
