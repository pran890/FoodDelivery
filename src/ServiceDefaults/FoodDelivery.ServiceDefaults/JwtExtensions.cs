using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Microsoft.Extensions.Hosting;

public static class JwtExtensions
{
    /// <summary>The HttpClient that fetches Identity's public keys.</summary>
    public const string KeysHttpClient = "identity-keys";

    /// <summary>
    /// Step 12: accept Identity's access tokens. The only thing a service knows about Identity is its name;
    /// the keys come from its public /.well-known addresses and are cached, so requests don't call Identity.
    /// </summary>
    public static TBuilder AddFoodDeliveryJwt<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        var settings = builder.Configuration.GetSection("Jwt");
        var issuer = settings["Issuer"] ?? "https://identity.fooddelivery.local";
        var audience = settings["Audience"] ?? "fooddelivery-api";
        var authority = settings["Authority"] ?? "https+http://identity";

        // From the factory, so it gets service discovery ("identity" → an address) and the retries of Step 05.
        builder.Services.AddHttpClient(KeysHttpClient);

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory>((options, clients) =>
            {
                options.MetadataAddress = $"{authority}/.well-known/openid-configuration";
                options.Backchannel = clients.CreateClient(KeysHttpClient);
                // "https+http" belongs to service discovery. In production Jwt:Authority must be set, and be https.
                options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();

                options.Events = new JwtBearerEvents { OnChallenge = SayUnavailableWithoutKeys };

                // Keep our claim names. The default renames "sub" and "role" to long schema URLs.
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    // Only access tokens, and only the algorithm we use (see Step 11's attacks).
                    ValidTypes = ["at+jwt"],
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    // The default of 5 minutes would make a 15-minute token last 20.
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
            })
            // A wrong authority should stop the start, not answer every request with 500.
            .ValidateOnStart();

        // Every endpoint needs a token unless it says AllowAnonymous: forgetting fails safely.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return builder;
    }

    /// <summary>
    /// Identity was down before we ever got its keys, so we can't judge the token. The default answer is 401,
    /// which tells a client that its perfectly good token is bad, and signs the user out. 503 says "try again".
    /// </summary>
    private static Task SayUnavailableWithoutKeys(JwtBearerChallengeContext context)
    {
        // The library has no own exception type for "no keys at all" (IDX10500), as opposed to "keys, but not
        // this one" (IDX10503). A test fails if a new version changes that.
        if (context.AuthenticateFailure is SecurityTokenSignatureKeyNotFoundException failure
            && failure.Message.StartsWith("IDX10500", StringComparison.Ordinal))
        {
            context.HandleResponse();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = "5";
        }

        return Task.CompletedTask;
    }
}
