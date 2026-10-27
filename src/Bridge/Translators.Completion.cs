using System.Diagnostics;
using System.Text.Json;

namespace DeadlockTranslate;
public sealed partial class Translators {
    private async Task<string> ReadCompletion(HttpRequestMessage request,long epoch,LlmTelemetry.Trace trace,CancellationToken ct,int promptCharacters){
        trace.Stage("waiting");var started=Stopwatch.GetTimestamp();
        try{
            var bytes=request.Content==null?null:await request.Content.ReadAsByteArrayAsync(ct);
            for(var attempt=0;attempt<2;attempt++){
                ct.ThrowIfCancellationRequested();
                using var send=new HttpRequestMessage(request.Method,request.RequestUri);
                foreach(var header in request.Headers)send.Headers.TryAddWithoutValidation(header.Key,header.Value);
                if(bytes!=null){send.Content=new ByteArrayContent(bytes);foreach(var header in request.Content!.Headers)send.Content.Headers.TryAddWithoutValidation(header.Key,header.Value);}
                HttpResponseMessage response;
                try{response=await http.SendAsync(send,HttpCompletionOption.ResponseHeadersRead,ct);}
                catch(HttpRequestException ex) when(attempt==0&&!ct.IsCancellationRequested&&ProviderNetworkFailure.CanRetryBeforeResponse(ex)){
                    log?.Record("translation",LogKind.TranslationTiming,new(Token:trace.Id,Stage:"provider-retry",Provider:"quality",Count:1,NetworkError:ProviderNetworkFailure.Code(ex),SocketError:(int?)ProviderNetworkFailure.SocketCode(ex)));
                    await Task.Delay(150,ct);continue;
                }
                catch(HttpRequestException ex) when(ex.StatusCode==null){throw new ProviderNetworkException("llm","llm-connect",ex);}
                using(response){
                    trace.Response(response.Content.Headers.ContentType?.MediaType=="text/event-stream"?"stream":"json");await CheckApiResponse(response,"llm","llm-translate",epoch,ct);
                    try{return await ChatCompletionReader.Read(response,trace,ct);}
                    catch(HttpRequestException ex) when(ex.StatusCode==null){throw new ProviderNetworkException("llm","llm-response",ex);}
                    catch(IOException ex){throw new ProviderNetworkException("llm","llm-response",new HttpRequestException(HttpRequestError.ResponseEnded,"Incomplete API response",ex));}
                    catch(Exception ex) when(ex is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException){throw Failure(response.StatusCode,"llm","llm-translate",epoch,"API 响应没有完整可用的译文；请检查协议或关闭流式反馈后主动重试。",LogReason.InvalidData);}
                }
            }
            throw new InvalidOperationException("网络重试未完成");
        }finally{Timing("provider-response",started,promptCharacters,trace.Snapshot().Direction=="outgoing",trace.Id);}
    }
}
