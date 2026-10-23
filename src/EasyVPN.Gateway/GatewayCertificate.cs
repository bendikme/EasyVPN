using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's TLS certificate, made on first start. The API and the relay both present it, and
/// clients pin it, so it needs no certificate authority - and must not change once clients use it.
/// </summary>
public sealed class GatewayCertificate
{
    private GatewayCertificate(X509Certificate2 certificate, string certFile, string keyFile)
    {
        Certificate = certificate;
        CertFile = certFile;
        KeyFile = keyFile;
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>The same certificate and its key as PEM files, the form gost reads.</summary>
    public string CertFile { get; }

    public string KeyFile { get; }

    public string Thumbprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static GatewayCertificate LoadOrCreate()
    {
        var folder = GatewaySettings.Folder;
        var pfx = Path.Combine(folder, "gateway.pfx");
        var certFile = Path.Combine(folder, "gateway.crt");
        var keyFile = Path.Combine(folder, "gateway.key");
        if (!File.Exists(pfx)) Create(pfx, certFile, keyFile);
        return new GatewayCertificate(X509CertificateLoader.LoadPkcs12FromFile(pfx, null), certFile, keyFile);
    }

    private static void Create(string pfx, string certFile, string keyFile)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Defaults.CertificateName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Defaults.CertificateName);
        request.CertificateExtensions.Add(names.Build());
        // Its own authority: gost on a client trusts this certificate, and nothing else, as its root.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        File.WriteAllText(certFile, certificate.ExportCertificatePem());
        File.WriteAllText(keyFile, rsa.ExportPkcs8PrivateKeyPem());
        // Last, so an interrupted first start makes all three again.
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx));
    }
}
