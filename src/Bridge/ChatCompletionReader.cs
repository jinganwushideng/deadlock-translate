using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;
public static class ChatCompletionReader {
    private sealed class Answer {
        public readonly StringBuilder Text=new();public string? Finish;public bool Reasoning,Done,Finished;
        public void Add(string? content,LlmTelemetry.Trace trace){if(string.IsNullOrEmpty(content))return;if(Finished||Text.Length+content.Length>2500)throw new InvalidDataException("API text exceeds the bounded response");Text.Append(content);trace.Content();}
    }
    public static async Task<string> Read(HttpResponseMessage response,LlmTelemetry.Trace trace,CancellationToken ct){
        var stream=response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream",StringComparison.OrdinalIgnoreCase)==true;
        trace.Response(stream?"stream":"json");var answer=new Answer();
        await using var raw=await response.Content.ReadAsStreamAsync(ct);await using var limited=new LimitedReadStream(raw,2*1024*1024);
        if(stream){
            using var reader=new StreamReader(limited,Encoding.UTF8,true,4096,leaveOpen:true);var data=new StringBuilder();
            while(true){var line=await reader.ReadLineAsync(ct);if(line==null){if(data.Length>0)Consume(data.ToString(),answer,trace);break;}if(line.Length>65536)throw new InvalidDataException("SSE line too long");
                if(line.Length==0){if(data.Length>0){Consume(data.ToString(),answer,trace);data.Clear();if(answer.Done)break;}continue;}
                if(line.StartsWith("data:",StringComparison.Ordinal)){if(data.Length>0)data.Append('\n');data.Append(line.AsSpan(5).TrimStart());if(data.Length>65536)throw new InvalidDataException("SSE event too long");}
            }
            if(!answer.Done||!answer.Finished)throw new IOException("Incomplete streaming response");
        }else{
            using var json=await JsonDocument.ParseAsync(limited,cancellationToken:ct);var root=json.RootElement;
            Usage(root,trace,answer);var choice=root.GetProperty("choices")[0];var message=choice.GetProperty("message");
            answer.Reasoning|=HasText(message,"reasoning_content");answer.Add(message.GetProperty("content").GetString(),trace);
            if(choice.TryGetProperty("finish_reason",out var reason)&&reason.ValueKind==JsonValueKind.String){answer.Finish=SafeFinish(reason.GetString());trace.Usage(null,null,null,answer.Finish);}
        }
        if(answer.Finish=="length")throw new TranslationOutputException(answer.Reasoning&&answer.Text.Length==0?TranslationOutputFailure.ReasoningBudget:TranslationOutputFailure.Truncated);
        if(answer.Finish is "content_filter" or "tool_calls" or "function_call")throw new InvalidDataException("No complete translation");
        if(answer.Text.Length==0){if(answer.Reasoning)throw new TranslationOutputException(TranslationOutputFailure.ReasoningOnly);throw new JsonException("No usable text");}
        trace.Stage("validating");return answer.Text.ToString().Trim();
    }
    private static void Consume(string data,Answer answer,LlmTelemetry.Trace trace){
        if(data=="[DONE]"){answer.Done=true;return;}
        using var json=JsonDocument.Parse(data);var root=json.RootElement;Usage(root,trace,answer);
        if(root.TryGetProperty("error",out _))throw new InvalidDataException("Streaming API error");
        if(!root.TryGetProperty("choices",out var choices)||choices.ValueKind!=JsonValueKind.Array)throw new JsonException("Invalid stream choices");
        foreach(var choice in choices.EnumerateArray()){
            if(choice.TryGetProperty("index",out var index)&&index.ValueKind==JsonValueKind.Number&&index.GetInt32()!=0)continue;
            if(choice.TryGetProperty("delta",out var delta)&&delta.ValueKind==JsonValueKind.Object){
                if(HasText(delta,"reasoning_content")){if(answer.Finished)throw new InvalidDataException("Delta after completion");answer.Reasoning=true;if(answer.Text.Length==0)trace.Stage("thinking");}
                if(delta.TryGetProperty("content",out var content)&&content.ValueKind==JsonValueKind.String)answer.Add(content.GetString(),trace);
            }
            if(choice.TryGetProperty("finish_reason",out var finish)&&finish.ValueKind==JsonValueKind.String){answer.Finish=SafeFinish(finish.GetString());answer.Finished=true;trace.Usage(null,null,null,answer.Finish);}
        }
    }
    public static string SafeFinish(string? reason)=>reason is "stop" or "length" or "content_filter" or "tool_calls" or "function_call"?reason:"other";
    private static bool HasText(JsonElement element,string key)=>element.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString()?.Length>0;
    private static int? Number(JsonElement element,string key)=>element.ValueKind==JsonValueKind.Object&&element.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out var n)&&n is >=0 and <=2000000?n:null;
    private static void Usage(JsonElement root,LlmTelemetry.Trace trace,Answer answer){
        if(!root.TryGetProperty("usage",out var usage)||usage.ValueKind!=JsonValueKind.Object)return;
        var reasoning=usage.TryGetProperty("completion_tokens_details",out var detail)?Number(detail,"reasoning_tokens"):null;
        answer.Reasoning|=reasoning>0;trace.Usage(Number(usage,"prompt_tokens"),Number(usage,"completion_tokens"),reasoning);
    }
    private sealed class LimitedReadStream(Stream source,long maximum):Stream {
        private long count;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>count;set=>throw new NotSupportedException();}
        private int Check(int n){count+=n;if(count>maximum)throw new InvalidDataException("Response too large");return n;}
        public override int Read(byte[] buffer,int offset,int length)=>Check(source.Read(buffer,offset,length));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default)=>Check(await source.ReadAsync(buffer,cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer,int offset,int length,CancellationToken cancellationToken)=>ReadAsync(buffer.AsMemory(offset,length),cancellationToken).AsTask();
        public override void Flush(){}public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int length)=>throw new NotSupportedException();
    }
}
