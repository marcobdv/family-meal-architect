using Api.Models;
using Api.Services;
using Microsoft.Extensions.Configuration;

namespace Tests;

public class AuthServiceTests
{
    private static AuthService Service() =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-that-is-long-enough-for-hmac-sha256-please",
                ["Jwt:Issuer"] = "TestIssuer",
                ["Jwt:Audience"] = "TestAudience"
            }).Build());

    [Fact]
    public void HashPassword_RoundTrips()
    {
        var auth = Service();
        var hash = auth.HashPassword("s3cret!");

        Assert.NotEqual("s3cret!", hash);
        Assert.True(auth.VerifyPassword(hash, "s3cret!"));
        Assert.False(auth.VerifyPassword(hash, "wrong"));
    }

    [Fact]
    public void HashPassword_ProducesDifferentHashesForSamePassword()
    {
        var auth = Service();
        Assert.NotEqual(auth.HashPassword("same"), auth.HashPassword("same")); // random salt
    }

    [Fact]
    public void CreateToken_ReturnsNonEmptyTokenWithFutureExpiry()
    {
        var auth = Service();
        var user = new User { Id = "u1", Name = "Test", Email = "t@e.com" };

        var (token, expiresAt) = auth.CreateToken(user);

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(3, token.Split('.').Length); // header.payload.signature
        Assert.True(expiresAt > DateTime.UtcNow);
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForMalformedHash()
    {
        Assert.False(Service().VerifyPassword("not-a-valid-hash", "whatever"));
    }

    [Fact]
    public void VerifyPassword_ReturnsFalseForCorruptBase64_DoesNotThrow()
    {
        // Well-structured (3 dot-separated parts, valid iteration count) but the
        // salt/hash segments are not valid base64 — must return false, not throw.
        Assert.False(Service().VerifyPassword("100000.@@@not-base64@@@.###also-bad###", "whatever"));
    }
}
