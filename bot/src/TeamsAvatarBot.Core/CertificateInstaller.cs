using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace TeamsAvatarBot.Core;

/// <summary>
/// Loads our Let's Encrypt cert from <c>certs/iris.pfx</c> and ensures it is
/// installed in the LocalMachine\My store, because
/// <see cref="Microsoft.Skype.Bots.Media.MediaPlatformInstanceSettings"/>
/// looks up the cert by thumbprint from the Windows cert store rather than
/// accepting a file path.
///
/// Requires elevated process to write to LocalMachine\My. We're already
/// running elevated to bind port 443, so this is fine.
/// </summary>
public static class CertificateInstaller
{
    /// <summary>
    /// Load the PFX, install in LocalMachine\My if not already present,
    /// return the thumbprint.
    /// </summary>
    /// <param name="pfxPath">Absolute path to the PFX file.</param>
    /// <param name="password">PFX password (defaults to "changeit" upstream).</param>
    /// <param name="logger">For status messages.</param>
    /// <returns>The thumbprint string suitable for MediaPlatformInstanceSettings.</returns>
    public static string EnsureInstalled(string pfxPath, string password, ILogger logger)
    {
        if (!File.Exists(pfxPath))
            throw new FileNotFoundException("cert PFX not found: " + pfxPath);

        // X509KeyStorageFlags: PersistKeySet so the private key is kept after
        // the X509Certificate2 is disposed; MachineKeySet so the key is
        // accessible by the machine account (matches LocalMachine store).
        var cert = new X509Certificate2(
            pfxPath,
            password,
            X509KeyStorageFlags.PersistKeySet
                | X509KeyStorageFlags.MachineKeySet
                | X509KeyStorageFlags.Exportable);

        var thumbprint = cert.Thumbprint
            ?? throw new InvalidOperationException("cert has no thumbprint (corrupt PFX?)");

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (existing.Count > 0)
        {
            logger.LogInformation("cert {Thumbprint} already in LocalMachine\\My", thumbprint);
        }
        else
        {
            store.Add(cert);
            logger.LogInformation("installed cert {Thumbprint} into LocalMachine\\My", thumbprint);
        }

        store.Close();
        return thumbprint;
    }
}
