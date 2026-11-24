using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FoodDelivery.Identity.Features.Tokens;

/// <summary>
/// The one RSA key that signs all tokens. Step 16 replaces this with several encrypted keys in the database.
/// </summary>
internal sealed class SigningKey : IDisposable
{
    private const int KeySizeInBits = 2048;

    private readonly RSA _rsa;

    public SigningKey(IOptions<JwtOptions> options, IHostEnvironment environment)
    {
        _rsa = Load(options.Value.SigningKeyPath, environment.IsDevelopment());

        if (_rsa.KeySize < KeySizeInBits)
        {
            throw new InvalidOperationException($"The signing key has {_rsa.KeySize} bits; at least {KeySizeInBits} are needed.");
        }

        // Sign something now: a file with only the public key would otherwise fail at the first sign-in.
        _rsa.SignData("test"u8, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var key = new RsaSecurityKey(_rsa)
        {
            // Its own cache of signing objects. The default one is shared by the whole process and finds
            // entries by key, so it could hand out an object whose RSA was disposed by someone else.
            CryptoProviderFactory = new CryptoProviderFactory(),
        };
        // The key id is a fingerprint of the PUBLIC key (RFC 7638): the same key always gets the same id.
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        Credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var publicPart = _rsa.ExportParameters(includePrivateParameters: false);
        PublicKey = new WellKnown.PublicKey(
            KeyType: "RSA",
            Use: "sig",
            Algorithm: SecurityAlgorithms.RsaSha256,
            KeyId: key.KeyId,
            Modulus: Base64UrlEncoder.Encode(publicPart.Modulus),
            Exponent: Base64UrlEncoder.Encode(publicPart.Exponent));
    }

    public SigningCredentials Credentials { get; }

    /// <summary>What other services may see.</summary>
    public WellKnown.PublicKey PublicKey { get; }

    public void Dispose() => _rsa.Dispose();

    private static RSA Load(string? path, bool isDevelopment)
    {
        if (isDevelopment)
        {
            // Outside the repository, which is public.
            path ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FoodDelivery", "identity-signing-key.pem");
            CreateIfMissing(path);
        }

        // In production a missing key must stop the app. Quietly making a new one would break every
        // token already issued, and each instance would have a different key.
        if (!File.Exists(path))
        {
            throw new InvalidOperationException("Jwt:SigningKeyPath must point to an existing private key (PEM).");
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(path));
        return rsa;
    }

    private static void CreateIfMissing(string path)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var rsa = RSA.Create(KeySizeInBits);

        // Write a temporary file, then move it: another instance never sees a half-written key.
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            // Readable by this user only. On Windows the folder's permissions already do that.
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temporary, fileOptions))
        {
            writer.Write(rsa.ExportPkcs8PrivateKeyPem());
        }

        try
        {
            File.Move(temporary, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another instance was first: use its key.
            File.Delete(temporary);
        }
    }
}
