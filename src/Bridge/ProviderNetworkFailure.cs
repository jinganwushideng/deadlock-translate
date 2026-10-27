using System.Net.Sockets;

namespace DeadlockTranslate;

public sealed class ProviderNetworkException(string provider,string stage,HttpRequestException inner)
    : HttpRequestException(inner.HttpRequestError,SafeMessage(inner),inner,inner.StatusCode) {
    public string Provider {get;}=provider;
    public string Stage {get;}=stage;
    private static string SafeMessage(HttpRequestException error) {
        var diagnosis=ApiConfigurationDiagnostics.NetworkFailure(error);
        return diagnosis.Title+"："+diagnosis.Detail+" "+string.Join(" ",diagnosis.Steps);
    }
}

public static class ProviderNetworkFailure {
    public static SocketError? SocketCode(Exception error) {
        for(Exception? item=error;item!=null;item=item.InnerException)if(item is SocketException socket)return socket.SocketErrorCode;
        return null;
    }
    // Retry only failures proving no connection was established. No replay after
    // response headers, a reset, a read timeout, TLS failure or uncertain delivery.
    public static bool CanRetryBeforeResponse(HttpRequestException error)=>error.StatusCode==null&&
        (error.HttpRequestError==HttpRequestError.NameResolutionError||error.HttpRequestError==HttpRequestError.ConnectionError&&
            SocketCode(error) is SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.NetworkDown or SocketError.HostUnreachable or SocketError.HostNotFound or SocketError.TryAgain);
    public static string Code(HttpRequestException error)=>error.HttpRequestError.ToString();
}
