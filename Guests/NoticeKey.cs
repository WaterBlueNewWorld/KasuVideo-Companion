// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) 2026 WaterBlueNewWorld

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.KasuVideoCompanion.Guests;

/// <summary>
/// The key the companion signs its notices to the rooms server with (<see cref="EndNotices"/>):
/// an ECDSA P-256 pair made on first use and kept in the companion's data folder
/// (<see cref="FileName"/>, beside the pass store), never in the plugin configuration, which
/// Jellyfin hands to administrators. The rooms Worker reads the public half from
/// <c>GET /KasuVideo/Companion/NoticeKey</c> on the one server it trusts, so nothing secret is
/// copied between them. An unreadable key file is replaced by a new key: the Worker fetches the
/// key again when a notice doesn't verify.
/// </summary>
public sealed class NoticeKey : IDisposable
{
    /// <summary>The key file's name in the companion's data folder.</summary>
    public const string FileName = "notice-key.pem";

    private readonly ECDsa _key;

    /// <summary>Loads the key from <paramref name="folder"/>, or makes and keeps a new one.</summary>
    /// <param name="folder">The companion's data folder.</param>
    /// <param name="logger">Where a replaced key is noted (never the key).</param>
    public NoticeKey(string folder, ILogger? logger = null)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, FileName);
        _key = Load(path, logger) ?? Make(path);
        PublicKey = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Gets the public half, as base64 DER (SubjectPublicKeyInfo), which WebCrypto imports as <c>spki</c>.</summary>
    public string PublicKey { get; }

    /// <summary>
    /// Signs <paramref name="data"/> (SHA-256): 64 bytes, r then s (IEEE P1363), the form WebCrypto
    /// verifies, as base64.
    /// </summary>
    /// <param name="data">The exact bytes sent.</param>
    /// <returns>The signature.</returns>
    public string Sign(byte[] data) =>
        Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <inheritdoc />
    public void Dispose() => _key.Dispose();

    private static ECDsa? Load(string path, ILogger? logger)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(path));
            if (key.KeySize == 256)
            {
                return key;
            }
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException or IOException)
        {
            // Replaced below.
        }

        key.Dispose();
        logger?.LogWarning("The companion's notice key couldn't be read; a new one replaces it");
        return null;
    }

    private static ECDsa Make(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();
        var temporary = path + ".new";
        File.WriteAllText(temporary, pem);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
        return key;
    }
}
