using System.Net;

namespace DeadlockTranslate;

// Only fixed service/stage codes and status numbers travel into diagnostics.
// Redirect URLs, headers, cookies, provider response bodies and chat stay out.
public sealed class ProviderRequestException(string provider,string stage,string message,HttpStatusCode status,LogReason reason)
    : HttpRequestException(message,null,status)
{
    public string Provider {get;}=provider;
    public string Stage {get;}=stage;
    public LogReason Reason {get;}=reason;
}
public sealed record ProviderIssue(string Provider,string Stage,int HttpStatus,string Code,DateTime At);
