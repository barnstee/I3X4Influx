using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace I3X4Influx
{
    /// <summary>
    /// Validates OAuth2 / OpenID Connect bearer (JWT) access tokens for the i3X API. Signing keys are
    /// discovered from the configured authority's OIDC metadata (JWKS) and cached/refreshed automatically.
    ///
    /// Configured via environment variables:
    ///   - <c>I3X_OAUTH2_AUTHORITY</c> (required to enable OAuth2): the OIDC authority base URL, e.g.
    ///     "https://login.microsoftonline.com/{tenant}/v2.0".
    ///   - <c>I3X_OAUTH2_AUDIENCE</c> (optional): expected audience (aud) claim(s), comma-separated.
    ///   - <c>I3X_OAUTH2_ISSUER</c> (optional): expected issuer; defaults to the authority's metadata issuer.
    ///
    /// OAuth2 is a second, optional authentication method: it is only active when an authority is
    /// configured, and it coexists with HTTP Basic authentication.
    /// </summary>
    public sealed class OAuth2TokenValidator
    {
        private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;
        private readonly string[] _validAudiences;
        private readonly string _issuer;
        private readonly JsonWebTokenHandler _handler = new();

        public OAuth2TokenValidator()
        {
            string authority = Environment.GetEnvironmentVariable("I3X_OAUTH2_AUTHORITY");
            Configured = !string.IsNullOrEmpty(authority);
            if (!Configured)
            {
                return;
            }

            string metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever { RequireHttps = true });

            _validAudiences = (Environment.GetEnvironmentVariable("I3X_OAUTH2_AUDIENCE") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            _issuer = Environment.GetEnvironmentVariable("I3X_OAUTH2_ISSUER");
        }

        /// <summary>True when an OAuth2 authority is configured and bearer tokens can be validated.</summary>
        public bool Configured { get; }

        /// <summary>Validates a bearer JWT against the configured authority. Returns false when invalid.</summary>
        public async Task<bool> ValidateAsync(string token, CancellationToken cancellationToken)
        {
            if (!Configured || string.IsNullOrEmpty(token))
            {
                return false;
            }

            try
            {
                OpenIdConnectConfiguration config =
                    await _configManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

                var parameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = config.SigningKeys,
                    ValidateIssuer = true,
                    ValidIssuer = string.IsNullOrEmpty(_issuer) ? config.Issuer : _issuer,
                    ValidateAudience = _validAudiences.Length > 0,
                    ValidAudiences = _validAudiences,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };

                TokenValidationResult result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
                return result.IsValid;
            }
            catch
            {
                // Any failure (network, metadata, malformed/invalid token) is treated as unauthenticated.
                return false;
            }
        }
    }
}
