using System.Net;
using System.Text;
using System.Text.Json;
using DeadlockTranslate;

internal static class BuildParallelFixture {
    internal static async Task Run(string folder,Action<bool,string> check){
        var gate=new PriorityConcurrency(4,true);
        using(var first=gate.TryEnterBackground())using(var second=gate.TryEnterBackground()){
            check(first!=null&&second!=null&&gate.TryEnterBackground()==null,"two build requests enter concurrently while a third waits");
            using var receive=await gate.Enter(default).WaitAsync(TimeSpan.FromSeconds(2));
            using var send=await gate.Enter(default,true).WaitAsync(TimeSpan.FromSeconds(2));
            check(JsonSerializer.SerializeToElement(gate.Stats).GetProperty("active").GetInt32()==4,"parallel builds leave capacity for both receive and reserved send work");
        }
        check(JsonSerializer.SerializeToElement(gate.Stats).GetProperty("background").GetInt32()==0,"all background leases return their capacity");
        var single=new PriorityConcurrency(1,true);var lease=single.TryEnterBackground()!;
        var waiting=single.Enter(default);
        check(single.TryEnterBackground()==null&&!waiting.IsCompleted,"one-slot configuration remains serial and live work queues first");
        lease.Dispose();using(var foreground=await waiting.WaitAsync(TimeSpan.FromSeconds(2)))
            check(single.TryEnterBackground()==null,"queued foreground work takes the released build slot");

        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished=new System.Collections.Concurrent.ConcurrentBag<string>();var active=0;var peak=0;var started=0;
        async Task Translate(string[] batch){
            var current=Interlocked.Increment(ref active);InterlockedExtensions.Max(ref peak,current);
            if(Interlocked.Increment(ref started)==2)ready.TrySetResult();
            try{await release.Task;finished.Add(batch[0]);}finally{Interlocked.Decrement(ref active);}
        }
        var batches=Enumerable.Range(0,7).Select(i=>new[]{"field-"+i}).ToArray();
        var run=BuildTextJobs.RunBatches(batches,Translate,()=>false,default);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        check(started==2&&!run.IsCompleted,"batch runner starts two workers without prematurely reporting completion");
        release.SetResult();await run;
        check(peak==2&&finished.Distinct().Count()==7&&started==7,"bounded workers process every unique batch exactly once");
        using var cancel=new CancellationTokenSource();var cancellationReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var admitted=0;
        async Task Cancellable(string[] batch){if(Interlocked.Increment(ref admitted)==2)cancellationReady.TrySetResult();await Task.Delay(Timeout.Infinite,cancel.Token);}
        var cancelled=BuildTextJobs.RunBatches(batches,Cancellable,()=>false,cancel.Token);
        await cancellationReady.Task.WaitAsync(TimeSpan.FromSeconds(2));cancel.Cancel();
        try{await cancelled;throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
        check(admitted==2,"cancellation stops active workers and prevents remaining batches from being dispatched");

        var job=new BuildTextJobs.Job("fixture",default,2);job.SetPlan(new("fixture",["first","second"],null,new string('A',64),false));
        job.Apply(["second"],JsonSerializer.SerializeToElement(new{translations=new[]{"后返回顺序中的第二段"},cacheHits=0,translated=1}),false);
        var early=JsonSerializer.SerializeToElement(job.Snapshot(0,1),ConfigStore.Json);
        check(!early.GetProperty("ended").GetBoolean()&&early.GetProperty("done").GetInt32()==1,"out-of-order results remain in-progress until all batches finish");
        job.Apply(["first"],JsonSerializer.SerializeToElement(new{translations=new[]{"第一段"},cacheHits=0,translated=1}),false);job.Finish();
        var done=JsonSerializer.SerializeToElement(job.Snapshot(0,2),ConfigStore.Json);
        check(done.GetProperty("ended").GetBoolean()&&done.GetProperty("done").GetInt32()==2&&done.GetProperty("fields")[0].GetProperty("source").GetString()=="second","out-of-order responses retain source association and correct final totals");

        var dir=Path.Combine(folder,"parallel-provider");Directory.CreateDirectory(dir);
        var config=new ConfigStore(dir);config.Save(new Settings{LlmBaseUrl="http://127.0.0.1:9/v1",LlmKey="local-fixture",LlmStreaming=false,LlmConcurrency=4,OutgoingEnabled=true});
        using var handler=new ConcurrentApi();using var client=new HttpClient(handler);var terms=new Terms(dir,Path.Combine(dir,"none.json"));
        using var translators=new Translators(config,terms,client);var profile=BuildTextProfile.Resolve(config.Current);
        var one=translators.TranslateBuildQuality(["First annotation"],"",profile,default);
        var two=translators.TranslateBuildQuality(["Second annotation"],"",profile,default);
        await handler.Ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try{await translators.TranslateBuildQuality(["Third annotation"],"",profile,default);throw new Exception("Expected busy");}
        catch(FastProviderBusyException){check(handler.Count==2,"real build translator admits two overlapping HTTP requests and holds back a third");}
        handler.Release.SetResult();await Task.WhenAll(one,two);
        check(one.Result[0]=="译文"&&two.Result[0]=="译文","both concurrent provider replies pass parsing and complete independently");
    }
    private sealed class ConcurrentApi:HttpMessageHandler {
        internal readonly TaskCompletionSource Ready=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){
            if(Interlocked.Increment(ref Count)==2)Ready.TrySetResult();await Release.Task.WaitAsync(token);
            var translated=JsonSerializer.Serialize(new[]{new{id=0,translation="译文"}});
            return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content=translated},finish_reason="stop"}}}),Encoding.UTF8,"application/json")};
        }
    }
    private static class InterlockedExtensions {
        internal static void Max(ref int field,int value){int old;do{old=Volatile.Read(ref field);if(old>=value)return;}while(Interlocked.CompareExchange(ref field,value,old)!=old);}
    }
}
