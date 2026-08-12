using System.Security.Cryptography;

namespace Offstream.Core.Settings;

/// <summary>Encrypts a string so it can sit on disk without being readable.</summary>
/// <remarks>
/// An interface rather than a direct DPAPI call so the settings round-trip tests can run
/// without touching the real user keystore, and so a future non-Windows or non-DPAPI scheme
/// slots in without changing <see cref="SettingsStore"/>.
/// </remarks>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/>, returning text safe to write to JSON.</summary>
    string Protect(string plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>.
    /// </summary>
    /// <returns>
    /// The plaintext, or <see langword="null"/> when the value cannot be decrypted — which is a
    /// normal, expected condition rather than an error. See the remarks on
    /// <see cref="DpapiSecretProtector"/>.
    /// </returns>
    string? Unprotect(string protectedValue);
}

/// <summary>
/// Windows DPAPI (<see cref="ProtectedData"/>) scoped to the current user.
/// </summary>
/// <remarks>
/// <para>
/// The key is derived from the user's Windows credentials and never leaves the machine, so a
/// copied <c>settings.json</c> is useless to anyone else — which is the property that matters
/// for a token granting access to somebody's Spotify account.
/// </para>
/// <para>
/// <b>Failing to decrypt is expected, not exceptional.</b> The same file opened under a
/// different Windows user, restored to a different machine, or read after a credential reset
/// will not decrypt. <see cref="Unprotect"/> returns null in all those cases so the caller can
/// treat it as "no saved token, sign in again" instead of a corrupt settings file — losing a
/// refresh token costs one browser round trip, while refusing to load settings at all would
/// cost the user every other preference they have.
/// </para>
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // System.Text.Encoding is written out in full below: this assembly has its own
    // Offstream.Core.Encoding namespace (the ffmpeg boundary), which shadows it from in here.

    /// <summary>
    /// Additional entropy mixed into the key, so a protected value from another application
    /// running as the same user cannot be swapped in.
    /// </summary>
    private static readonly byte[] Entropy = "Offstream.Settings.v1"u8.ToArray();

    /// <inheritdoc />
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var encrypted = ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);

        return Convert.ToBase64String(encrypted);
    }

    /// <inheritdoc />
    public string? Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);

            return System.Text.Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Wrong user, wrong machine, or not a protected value at all. All mean the same
            // thing to the caller: there is no usable token here.
            return null;
        }
    }
}
