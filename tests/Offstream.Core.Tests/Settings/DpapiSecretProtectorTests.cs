using Offstream.Core.Settings;
using Xunit;

namespace Offstream.Core.Tests.Settings;

/// <summary>
/// The real DPAPI implementation, against the real Windows keystore.
/// </summary>
/// <remarks>
/// Deliberately not faked: the property worth proving is that Windows' own key derivation
/// round-trips, and a fake would prove only that the fake round-trips.
/// <see cref="SettingsStoreTests"/> uses a reversible stand-in to test the store's behaviour
/// around this, which is a different question.
/// </remarks>
public sealed class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void ProtectThenUnprotect_ReturnsTheOriginal()
    {
        const string secret = "AQB1c2VyLXJlZnJlc2gtdG9rZW4";

        Assert.Equal(secret, _protector.Unprotect(_protector.Protect(secret)));
    }

    [Fact]
    public void Protect_DoesNotLeaveThePlaintextVisible()
    {
        const string secret = "the-refresh-token";

        Assert.DoesNotContain(secret, _protector.Protect(secret), StringComparison.Ordinal);
    }

    /// <summary>DPAPI output is base64 here, so it can sit in a JSON string unescaped.</summary>
    [Fact]
    public void Protect_ProducesBase64()
    {
        var protectedValue = _protector.Protect("token");

        Assert.True(Convert.TryFromBase64String(protectedValue, new byte[protectedValue.Length], out _));
    }

    [Fact]
    public void Protect_IsNotDeterministic()
    {
        // DPAPI salts each call, so the same input twice must not produce the same ciphertext —
        // otherwise a settings file would leak whether two tokens are identical.
        Assert.NotEqual(_protector.Protect("token"), _protector.Protect("token"));
    }

    [Theory]
    [InlineData("not base64 at all!")]
    [InlineData("bm90LXByb3RlY3RlZC1hdC1hbGw=")]
    public void Unprotect_WithSomethingItDidNotProtect_ReturnsNull(string value) =>
        Assert.Null(_protector.Unprotect(value));

    [Fact]
    public void Unprotect_WithTamperedCiphertext_ReturnsNullRatherThanThrowing()
    {
        var protectedValue = _protector.Protect("token");
        var bytes = Convert.FromBase64String(protectedValue);

        bytes[^1] ^= 0xFF;

        Assert.Null(_protector.Unprotect(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void ProtectThenUnprotect_HandlesUnicodeAndEmptyValues()
    {
        Assert.Equal("", _protector.Unprotect(_protector.Protect("")));
        Assert.Equal("ü — 日本語", _protector.Unprotect(_protector.Protect("ü — 日本語")));
    }
}
