using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;

namespace FoodDelivery.Identity.Tests;

// The one test that sends a real email: the real EmailOtpSender over SMTP to a real Mailpit.
public sealed class EmailTests(IdentityFixture fixture) : IdentityTest(fixture)
{
    private const long Megabyte = 1024 * 1024;

    [Fact]
    public async Task The_code_arrives_by_email_and_signs_in()
    {
        await using var mailpit = new ContainerBuilder("axllent/mailpit:v1.31.2")
            .WithPortBinding(1025, assignRandomHostPort: true)
            .WithPortBinding(8025, assignRandomHostPort: true)
            .WithCreateParameterModifier(p => p.HostConfig!.Memory = p.HostConfig.MemorySwap = 64 * Megabyte)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/readyz")))
            .Build();
        await mailpit.StartAsync(Ct);
        await using var factory = Fixture.CreateFactory(
            fakeSender: false, ("ConnectionStrings:mailpit", $"Endpoint=smtp://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(1025)}"));
        using var client = factory.CreateClient();

        using var request = await RequestCodeAsync("asha@example.com", client);
        Assert.Equal(HttpStatusCode.Accepted, request.StatusCode);

        using var inbox = new HttpClient { BaseAddress = new Uri($"http://{mailpit.Hostname}:{mailpit.GetMappedPublicPort(8025)}") };
        using var message = JsonDocument.Parse(await inbox.GetStringAsync("/api/v1/message/latest", Ct));
        Assert.Equal("asha@example.com", message.RootElement.GetProperty("To")[0].GetProperty("Address").GetString());
        var code = Regex.Match(message.RootElement.GetProperty("Text").GetString()!, "[0-9]{6}").Value;

        using var verify = await VerifyAsync("asha@example.com", code, client);
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }
}
