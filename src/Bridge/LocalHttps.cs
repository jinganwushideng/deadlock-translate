using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DeadlockTranslate;

// Schannel requires a user key container; no PersistKeySet, so disposal removes the imported container.
// Per-Windows-user key material. Trust is a separate, explicit installation step.
public static class LocalHttps {
    public static X509Certificate2 Load(string directory,bool renew=false) {
        Directory.CreateDirectory(directory);
        var name=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..24];
        using var mutex=new Mutex(false,"Local\\DeadlockTranslateTLS-"+name);
        bool held=false;
        try {
            try {held=mutex.WaitOne(TimeSpan.FromSeconds(10));} catch(AbandonedMutexException){held=true;}
            if(!held)throw new IOException("Local HTTPS certificate is being initialized.");
            var secret=Path.Combine(directory,"localhost-certificate.dpapi");
            if(renew&&File.Exists(secret)) {
                var backup=Path.Combine(directory,"certificate-backups",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);File.Copy(secret,Path.Combine(backup,"localhost-certificate.dpapi"));
                var publicFile=Path.Combine(directory,"localhost-certificate.cer");
                if(File.Exists(publicFile))File.Copy(publicFile,Path.Combine(backup,"localhost-certificate.cer"));
            }
            X509Certificate2 cert;
            if(File.Exists(secret)&&!renew) {
                var raw=ProtectedData.Unprotect(File.ReadAllBytes(secret),null,DataProtectionScope.CurrentUser);
                try {cert=X509CertificateLoader.LoadPkcs12(raw,null,X509KeyStorageFlags.UserKeySet);}
                finally {CryptographicOperations.ZeroMemory(raw);}
                if(!cert.HasPrivateKey) {
                    cert.Dispose(); throw new InvalidDataException("Local HTTPS certificate has no private key; renew explicitly.");
                }
            } else {
                using var rsa=RSA.Create(2048);
                var request=new CertificateRequest("CN=DeadlockTranslate localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false,false,0,true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature|X509KeyUsageFlags.KeyEncipherment,true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") },true));
                var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);san.AddIpAddress(IPAddress.IPv6Loopback);
                request.CertificateExtensions.Add(san.Build());
                using var created=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),DateTimeOffset.UtcNow.AddYears(2));
                var raw=created.Export(X509ContentType.Pfx);
                try {
                    ConfigStore.WriteAtomic(secret,ProtectedData.Protect(raw,null,DataProtectionScope.CurrentUser));
                    cert=X509CertificateLoader.LoadPkcs12(raw,null,X509KeyStorageFlags.UserKeySet);
                } finally {CryptographicOperations.ZeroMemory(raw);}
            }
            ConfigStore.WriteAtomic(Path.Combine(directory,"localhost-certificate.cer"),cert.Export(X509ContentType.Cert));
            return cert;
        } finally {if(held)mutex.ReleaseMutex();}
    }
}
