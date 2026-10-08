using System.Security.Cryptography.X509Certificates;

namespace DeadlockTranslate;

public record CertificateStatus(string State,string Message,DateTime? ExpiresUtc,int? DaysRemaining,string? Thumbprint,bool HttpsReady);
public sealed class CertificateDiagnostics(X509Certificate2? certificate,string? loadError) {
    private readonly object gate=new();private long checkedAt;private bool trusted;
    public bool CanServe=>certificate!=null&&certificate.HasPrivateKey&&certificate.NotBefore.ToUniversalTime()<=DateTime.UtcNow&&certificate.NotAfter.ToUniversalTime()>DateTime.UtcNow;
    public CertificateStatus Inspect(bool force=false){
        if(certificate==null)return new("unavailable",loadError??"本机证书不可用，请运行 HTTPS 配置脚本。",null,null,null,false);
        var expires=certificate.NotAfter.ToUniversalTime();var days=(int)Math.Floor((expires-DateTime.UtcNow).TotalDays);
        if(days<0)return new("expired","本机证书已过期，请退出后台后运行 Renew-Local-Https.cmd，再重启 Steam。",expires,days,certificate.Thumbprint,false);
        if(certificate.NotBefore.ToUniversalTime()>DateTime.UtcNow)return new("not-yet-valid","本机证书尚未生效，请检查 Windows 时间。",expires,days,certificate.Thumbprint,false);
        lock(gate){
            if(force||checkedAt==0||Environment.TickCount64-checkedAt>30000){
                try{using var store=new X509Store(StoreName.Root,StoreLocation.CurrentUser);store.Open(OpenFlags.ReadOnly);var matches=store.Certificates.Find(X509FindType.FindByThumbprint,certificate.Thumbprint,false);trusted=matches.Count>0;foreach(var c in matches)c.Dispose();}
                catch{return new("trust-check-failed","无法读取当前用户证书信任状态，请手动检查证书。",expires,days,certificate.Thumbprint,CanServe);}
                checkedAt=Environment.TickCount64;
            }
            if(!trusted)return new("untrusted","本机证书尚未受信任：运行 Trust-Local-Https.cmd，核对指纹并确认，再完整重启 Steam。",expires,days,certificate.Thumbprint,CanServe);
            return days<30?new("expiring","本机证书将在 30 天内到期，请安排退出后台后续期。",expires,days,certificate.Thumbprint,CanServe)
                :new("ready","本机 HTTPS 证书正常。若刚安装证书，需完整重启 Steam。",expires,days,certificate.Thumbprint,CanServe);
        }
    }
}
