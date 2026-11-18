using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace Bifrost.Android;

/// <summary>
/// Keeps the device token encrypted with a key that lives in the Android Keystore and never leaves it (design §10).
/// The preferences file then holds only ciphertext: a copy of the phone's storage, or root reading the file, gets
/// nothing it can use. AES-256-GCM, a fresh random IV for each value.
/// </summary>
public static class TokenVault
{
    private const string Alias = "bifrost-device-token";
    private const string Provider = "AndroidKeyStore";
    private const int TagBits = 128;

    /// <returns>Base64 of the IV followed by the ciphertext.</returns>
    public static string Seal(string text)
    {
        var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var iv = cipher.GetIV()!;
        var sealedBytes = cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(text))!;
        return Convert.ToBase64String([.. iv, .. sealedBytes]);
    }

    /// <returns>The text, or null when it cannot be opened (the key was lost, for example after a lock-screen reset, or the value was altered).</returns>
    public static string? Open(string sealedText)
    {
        try
        {
            var all = Convert.FromBase64String(sealedText);
            if (all.Length < 13) return null;
            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(TagBits, all, 0, 12));
            return System.Text.Encoding.UTF8.GetString(cipher.DoFinal(all, 12, all.Length - 12)!);
        }
        catch (Exception e) when (e is FormatException or GeneralSecurityException or Java.Lang.IllegalArgumentException or KeyPermanentlyInvalidatedException)
        {
            return null; // unreadable means unpaired: the person pairs again, which is always safe
        }
    }

    private static IKey Key()
    {
        var store = KeyStore.GetInstance(Provider)!;
        store.Load(null);
        if (store.GetKey(Alias, null) is { } existing) return existing;
        var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
        generator.Init(new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build());
        return generator.GenerateKey()!;
    }
}
