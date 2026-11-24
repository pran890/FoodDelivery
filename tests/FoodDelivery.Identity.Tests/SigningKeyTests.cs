using System.Security.Cryptography;
using FoodDelivery.Identity.Features.Tokens;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FoodDelivery.Identity.Tests;

// Unit tests: no containers, only a temporary file.
public sealed class SigningKeyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"signing-key-test-{Guid.NewGuid():N}.pem");

    private static SigningKey Load(string environment, string? path) =>
        new(Options.Create(new JwtOptions { SigningKeyPath = path }), new FakeEnvironment(environment));

    [Fact]
    public void Development_creates_a_key_once_and_keeps_using_it()
    {
        using var first = Load("Development", _path);
        using var second = Load("Development", _path); // like a restart

        Assert.Equal(first.Credentials.Key.KeyId, second.Credentials.Key.KeyId);
    }

    [Fact]
    public async Task Instances_starting_together_agree_on_one_key()
    {
        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Load("Development", _path))));

        Assert.Single(keys.Select(k => k.Credentials.Key.KeyId).Distinct());
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), Path.GetFileName(_path) + ".*.tmp"));
        Array.ForEach(keys, k => k.Dispose());
    }

    // The JWT library caches signing objects per key for the whole process. With that shared cache, an
    // instance that shuts down takes the same key away from another one (found by CI in Step 11).
    [Fact]
    public void Disposing_one_instance_does_not_break_another_with_the_same_key()
    {
        static string Issue(SigningKey key) =>
            new TokenIssuer(key, Options.Create(new JwtOptions()), TimeProvider.System).Issue(Guid.NewGuid(), "a@b.com", ["Customer"]);

        var first = Load("Development", _path);
        using var second = Load("Development", _path);
        Issue(first);

        first.Dispose();

        Assert.NotEmpty(Issue(second));
    }
    [Fact]
    public void Production_never_creates_a_key()
    {
        Assert.Throws<InvalidOperationException>(() => Load("Production", _path));
        Assert.Throws<InvalidOperationException>(() => Load("Production", null));
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void A_short_key_is_refused()
    {
        using var rsa = RSA.Create(1024);
        File.WriteAllText(_path, rsa.ExportPkcs8PrivateKeyPem());

        Assert.Throws<InvalidOperationException>(() => Load("Production", _path));
    }

    [Fact]
    public void A_public_key_is_refused_at_startup()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_path, rsa.ExportSubjectPublicKeyInfoPem());

        Assert.ThrowsAny<CryptographicException>(() => Load("Production", _path));
    }

    public void Dispose() => File.Delete(_path);

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
