using System.Security.Cryptography;
using System.Text;

namespace Revelei.Inference;

/// <summary>Where the catalog the app uses came from.</summary>
public enum CatalogSource
{
    /// <summary>The copy this build embeds, because nothing newer was fetched or trusted.</summary>
    Embedded,

    /// <summary>A newer catalog fetched from the network, its signature verified.</summary>
    Fetched,
}

/// <summary>What a refresh left the app with.</summary>
/// <param name="Manifest">The catalog to use.</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Kept">Why the embedded copy was kept, or null when the fetched one was taken.</param>
public sealed record CatalogRefresh(ModelManifest Manifest, CatalogSource Source, string? Kept = null);

/// <summary>
/// PB70: a catalog the app can refresh without trusting the network more than the copy it was
/// built with. The catalog is fetched with a detached signature (catalog.json.sig, the base64 of
/// an ECDSA P-256 SHA-256 signature in IEEE P1363 form) and taken only when that signature
/// verifies against the public key the app was built with and its sequence is not lower than the
/// embedded copy's, so a stale or replayed file can never roll a model back. Every file it lists
/// is still a resolve/&lt;commit&gt;/ URL with size and SHA-256, so trust per file is unchanged.
/// Anything else keeps the embedded copy and says why. Only the catalog is fetched: no photo,
/// prompt or identifier leaves the PC, and nothing here runs unless a person asks for it.
/// </summary>
public static class ModelCatalog
{
    /// <summary>The signature's address: the catalog's own with .sig appended.</summary>
    /// <param name="catalog">The catalog's address.</param>
    /// <returns>The signature's address.</returns>
    public static Uri SignatureUrl(Uri catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new Uri(catalog.AbsoluteUri + ".sig");
    }

    /// <summary>Checks a fetched catalog against the key and the floor, and reads it.</summary>
    /// <param name="catalog">catalog.json's bytes, exactly as served: the signature covers them.</param>
    /// <param name="signature">catalog.json.sig's text.</param>
    /// <param name="publicKey">The signing key's SubjectPublicKeyInfo, base64, as the app was built with.</param>
    /// <param name="floor">The embedded catalog, below which nothing is taken.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="InvalidDataException">The signature does not verify, the catalog is older than the floor, or it does not read.</exception>
    public static ModelManifest Accept(byte[] catalog, string signature, string publicKey, ModelManifest floor)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(floor);
        if (!Verifies(catalog, signature, publicKey))
        {
            throw new InvalidDataException("The catalog's signature does not verify against the key this build trusts.");
        }

        ModelManifest fetched = ModelManifest.Parse(Encoding.UTF8.GetString(catalog));
        if (fetched.Sequence < floor.Sequence)
        {
            throw new InvalidDataException($"The catalog is sequence {fetched.Sequence}, older than the {floor.Sequence} this build carries.");
        }

        return fetched;
    }

    /// <summary>Fetches the catalog and its signature, and keeps the floor unless both are trusted.</summary>
    /// <param name="http">The client.</param>
    /// <param name="catalog">The catalog's stable address.</param>
    /// <param name="publicKey">The signing key's SubjectPublicKeyInfo, base64.</param>
    /// <param name="floor">The embedded catalog.</param>
    /// <param name="cancellation">Cancels the fetch.</param>
    /// <returns>The catalog to use, and why the floor was kept when it was.</returns>
    public static async Task<CatalogRefresh> RefreshAsync(HttpClient http, Uri catalog, string publicKey, ModelManifest floor, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(floor);
        try
        {
            byte[] body = await http.GetByteArrayAsync(catalog, cancellation).ConfigureAwait(false);
            string signature = await http.GetStringAsync(SignatureUrl(catalog), cancellation).ConfigureAwait(false);
            return new CatalogRefresh(Accept(body, signature, publicKey, floor), CatalogSource.Fetched);
        }
        catch (HttpRequestException failure)
        {
            return new CatalogRefresh(floor, CatalogSource.Embedded, $"The catalog could not be fetched: {failure.Message}");
        }
        catch (InvalidDataException refused)
        {
            return new CatalogRefresh(floor, CatalogSource.Embedded, refused.Message);
        }
    }

    private static bool Verifies(byte[] catalog, string signature, string publicKey)
    {
        byte[] signed;
        try
        {
            signed = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        return key.VerifyData(catalog, signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
