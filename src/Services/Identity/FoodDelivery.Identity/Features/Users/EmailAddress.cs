using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;
using System.Text;

namespace FoodDelivery.Identity.Features.Users;

internal static class EmailAddress
{
    public const int MaxLength = 254;

    /// <summary>The single place that decides when two email addresses are the same.</summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        var email = input?.Trim();

        // ASCII only, and checked BEFORE lowercasing:
        // - .NET and Postgres disagree on lowercasing some letters (U+0130 stays as it is in .NET but
        //   becomes "i" in Postgres), which would violate the check constraint.
        // - Some letters lowercase INTO ASCII (the Kelvin sign U+212A becomes "k"), so a look-alike
        //   address could match somebody else's account. (The MailAddress check below happens to reject
        //   that one as well, but that is its internal detail.)
        if (string.IsNullOrEmpty(email) || email.Length > MaxLength || !Ascii.IsValid(email))
        {
            return false;
        }

        // MailAddress also accepts "Name <a@b.c>", so the parsed address must be the whole input.
        if (!MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
        {
            return false;
        }

        normalized = email.ToLowerInvariant();
        return true;
    }
}
