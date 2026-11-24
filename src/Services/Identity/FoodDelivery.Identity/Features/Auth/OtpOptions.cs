using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Identity.Features.Auth;

/// <summary>The "Otp" configuration section, checked when the app starts.</summary>
internal sealed class OtpOptions
{
    public const string Section = "Otp";

    /// <summary>The secret that makes a stored code useless to someone who can only read Redis.</summary>
    [Required, MinLength(32)]
    public string HmacKey { get; init; } = "";

    [Required]
    public string From { get; init; } = "";

    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(5);

    [Range(1, 10)]
    public int MaxAttempts { get; init; } = 5;
}
