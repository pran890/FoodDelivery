using FoodDelivery.Identity.Features.Users;

namespace FoodDelivery.Identity.Tests;

public class EmailAddressTests
{
    [Theory]
    [InlineData("asha@example.com", "asha@example.com")]
    [InlineData("  Asha@Example.COM ", "asha@example.com")]
    [InlineData("a.b+tag@sub.example.com", "a.b+tag@sub.example.com")]
    public void Valid_emails_are_trimmed_and_lowercased(string input, string expected)
    {
        Assert.True(EmailAddress.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-at-sign")]
    [InlineData("a@")]
    [InlineData("@b.com")]
    [InlineData("two@at@signs.com")]
    [InlineData("Asha <asha@example.com>")]
    [InlineData("asİha@example.com")] // dotted capital I: .NET and Postgres lowercase it differently
    [InlineData("asKha@example.com")] // Kelvin sign: lowercases into a plain "k"
    public void Invalid_emails_are_rejected(string? input)
    {
        Assert.False(EmailAddress.TryNormalize(input, out _));
    }

    [Fact]
    public void Emails_longer_than_the_column_are_rejected()
    {
        var longest = new string('a', EmailAddress.MaxLength - "@b.com".Length) + "@b.com";

        Assert.True(EmailAddress.TryNormalize(longest, out _));
        Assert.False(EmailAddress.TryNormalize("a" + longest, out _));
    }
}
