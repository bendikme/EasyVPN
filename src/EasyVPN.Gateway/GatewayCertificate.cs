using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EasyVPN.Gateway;

/// <summary>
/// The gateway's TLS certificate, made on first start. The API, and the tunnel on it, present it,
/// and clients pin it, so it needs no certificate authority - and must not change once clients use it.
/// </summary>
public sealed class GatewayCertificate
{
    private GatewayCertificate(X509Certificate2 certificate) => Certificate = certificate;

    public X509Certificate2 Certificate { get; }

    public string Thumbprint => Certificate.GetCertHashString(HashAlgorithmName.SHA256);

    public static GatewayCertificate LoadOrCreate()
    {
        var folder = GatewaySettings.Folder;
        var pfx = Path.Combine(folder, "gateway.pfx");
        // The PEM copies gost read, up to 0.4: the key among them, which nothing needs any more.
        foreach (var pem in (string[])["gateway.crt", "gateway.key"])
            File.Delete(Path.Combine(folder, pem));
        if (!File.Exists(pfx)) Create(pfx);
        return new GatewayCertificate(X509CertificateLoader.LoadPkcs12FromFile(pfx, null));
    }

    private static void Create(string pfx)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest($"CN={Defaults.CertificateName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Defaults.CertificateName);
        request.CertificateExtensions.Add(names.Build());
        // Its own authority, as clients made up to 0.4 trusted it as their only root.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        File.WriteAllBytes(pfx, certificate.Export(X509ContentType.Pfx));
    }
}
