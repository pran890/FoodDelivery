using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Identity.Features.Tokens;

/// <summary>The "Jwt" configuration section.</summary>
internal sealed class JwtOptions
{
    public const string Section = "Jwt";

    /// <summary>A fixed name for who issues the tokens. It looks like a URL by convention; nothing has to run there.</summary>
    [Required]
    public string Issuer { get; init; } = "https://identity.fooddelivery.local";

    [Required]
    public string Audience { get; init; } = "fooddelivery-api";

    /// <summary>Short, because a token can't be taken back.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>PEM file with the private key. Optional in Development only.</summary>
    public string? SigningKeyPath { get; init; }
}
