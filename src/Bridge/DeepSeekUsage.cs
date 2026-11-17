using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DeadlockTranslate;

public sealed record DeepSeekTokens(int? Input,int? Output,int? Reasoning,int? CacheHit,int? CacheMiss);
public sealed record DeepSeekCost(decimal Minimum,decimal Maximum);
public sealed record DeepSeekCharge(string Request,string Task,string Category,DateTimeOffset At,string Model,DeepSeekTokens Tokens,DeepSeekCost? Cost,string RateVersion);
public sealed record DeepSeekBalance(string Currency,decimal Total,decimal Granted,decimal ToppedUp);
public sealed record DeepSeekNotice(long Id,string Category,int Requests,int MissingUsage,DeepSeekCost? Cost,DateTimeOffset At);

public sealed class DeepSeekUsage(ConfigStore store,HttpClient http) {
    private sealed class Account {
        public readonly Dictionary<string,DeepSeekCharge> Charges=new(StringComparer.Ordinal);
        public DeepSeekBalance[] Balance=[];
        public DateTimeOffset? CheckedAt;
        public DateTimeOffset LastAttempt;
        public bool Checking;
        public bool? Available;
        public string? Error;
        public bool StorageError;
    }
    private sealed class UsageTask(string category) {
        public readonly string Id=Guid.NewGuid().ToString("N");
        public readonly string Category=category;
        public readonly List<(string Account,DeepSeekCharge Charge)> Charges=[];
        public bool Ended;
    }
    public sealed class Scope(Func<bool,DeepSeekNotice?> close):IDisposable {
        private Func<bool,DeepSeekNotice?>? finish=close;
        private bool zeroIfNoRequests;
        public void CompletedWithoutCharge()=>zeroIfNoRequests=true;
        public DeepSeekNotice? Receipt {get;private set;}
        public void Dispose(){var end=Interlocked.Exchange(ref finish,null);if(end!=null)Receipt=end(zeroIfNoRequests);}
    }
    private readonly object gate=new();
    private readonly AsyncLocal<UsageTask?> task=new();
    private readonly DateTimeOffset started=DateTimeOffset.UtcNow;
    private readonly Dictionary<string,Account> accounts=new(StringComparer.Ordinal);
    private readonly Queue<(string Account,DeepSeekNotice Value)> notices=new();
    private long serial;
    public const string RateVersion="deepseek-2026-11-17-cny";

    public static bool IsOfficial(string? endpoint) =>
        Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.IsDefaultPort&&
        uri.Host.Equals("api.deepseek.com",StringComparison.OrdinalIgnoreCase)&&uri.UserInfo.Length==0&&
        uri.Query.Length==0&&uri.Fragment.Length==0&&uri.AbsolutePath.TrimEnd('/') is "" or "/v1" or "/chat/completions" or "/v1/chat/completions" or "/beta" or "/beta/chat/completions";

    private static string Identify(string key)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("DLT DeepSeek usage\n"+key)));
    private string Ledger(string account)=>Path.Combine(store.DirectoryPath,"deepseek-usage-"+account+".jsonl");
    private Account Open(string id){
        if(accounts.TryGetValue(id,out var account))return account;
        account=new Account();
        try{
            if(File.Exists(Ledger(id)))foreach(var line in File.ReadLines(Ledger(id))){
                if(string.IsNullOrWhiteSpace(line))continue;
                try{var charge=JsonSerializer.Deserialize<DeepSeekCharge>(line);if(charge!=null)account.Charges.TryAdd(charge.Request,charge);}
                catch(JsonException){account.StorageError=true;}
            }
        }catch(IOException){account.StorageError=true;}
        catch(UnauthorizedAccessException){account.StorageError=true;}
        accounts[id]=account;return account;
    }

    public Scope Begin(string category){
        var previous=task.Value;
        if(previous!=null)return new Scope(_=>null);
        var operation=new UsageTask(category);
        task.Value=operation;
        var credentials=store.DeepSeekCredentials();
        return new Scope(zero=>{
            task.Value=previous;
            lock(gate){
                if(operation.Ended)return null;
                operation.Ended=true;
                DeepSeekNotice? receipt=null;
                foreach(var group in operation.Charges.GroupBy(x=>x.Account))
                    receipt=Publish(group.Key,operation.Category,group.Select(x=>x.Charge).ToArray());
                if(zero&&receipt==null&&credentials.Official&&credentials.Key.Length>0)
                    receipt=EnqueueNotice(Identify(credentials.Key),operation.Category,0,0,new(0,0));
                return receipt;
            }
        });
    }

    public static DeepSeekCost? Estimate(string model,DeepSeekTokens usage,DateTimeOffset at){
        if(usage.Input is not int input||usage.Output is not int output||
           usage.CacheHit is not int hit||usage.CacheMiss is not int miss||hit+miss!=input)return null;
        var full=model switch {
            "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp"=>(.04m,2m,8m),
            "deepseek-v4-pro"=>(.30m,9m,27m),
            _=>(0m,0m,0m)
        };
        if(full.Item3==0)return null;
        decimal high=(hit*full.Item1+miss*full.Item2+output*full.Item3)/1_000_000m;
        var local=at.ToOffset(TimeSpan.FromHours(8));
        bool peak=local.DayOfWeek is >=DayOfWeek.Monday and <=DayOfWeek.Friday && local.Hour is >=9 and <12 or >=14 and <18;
        return new(high/2,peak?high:high/2);
    }

    public void Record(string id,string category,string model,Uri? endpoint,string? key,DeepSeekTokens tokens){
        if(string.IsNullOrEmpty(key)||!IsOfficial(endpoint?.AbsoluteUri))return;
        var accountId=Identify(key);
        var operation=task.Value;
        var now=DateTimeOffset.UtcNow;
        var row=new DeepSeekCharge(id,operation?.Id??id,operation?.Category??category,now,model,tokens,Estimate(model,tokens,now),RateVersion);
        lock(gate){
            var account=Open(accountId);
            if(!account.Charges.TryAdd(id,row))return;
            try{
                File.AppendAllText(Ledger(accountId),Environment.NewLine+JsonSerializer.Serialize(row)+Environment.NewLine);
            }catch(IOException){account.StorageError=true;}
            catch(UnauthorizedAccessException){account.StorageError=true;}
            if(operation is {Ended:false})operation.Charges.Add((accountId,row));
            else if(operation==null)Publish(accountId,row.Category,[row]);
        }
        _=RefreshAsync(false);
    }

    private DeepSeekNotice? Publish(string account,string category,DeepSeekCharge[] charges){
        if(category=="keepalive"||charges.Length==0)return null;
        return EnqueueNotice(account,category,charges.Length,charges.Count(x=>x.Cost==null),Total(charges));
    }
    private DeepSeekNotice EnqueueNotice(string account,string category,int requests,int missing,DeepSeekCost? cost){
        var notice=new DeepSeekNotice(++serial,category,requests,missing,cost,DateTimeOffset.UtcNow);
        notices.Enqueue((account,notice));
        while(notices.Count>64)notices.Dequeue();
        return notice;
    }
    private static DeepSeekCost? Total(IEnumerable<DeepSeekCharge> charges){
        var priced=charges.Where(x=>x.Cost!=null).Select(x=>x.Cost!).ToArray();
        return priced.Length==0?null:new(priced.Sum(x=>x.Minimum),priced.Sum(x=>x.Maximum));
    }
    private static object Sum(IEnumerable<DeepSeekCharge> selection){
        var rows=selection.ToArray();
        return new{requests=rows.Length,input=rows.Sum(x=>(long)(x.Tokens.Input??0)),output=rows.Sum(x=>(long)(x.Tokens.Output??0)),
            reasoning=rows.Sum(x=>(long)(x.Tokens.Reasoning??0)),cacheHit=rows.Sum(x=>(long)(x.Tokens.CacheHit??0)),unreported=rows.Count(x=>x.Tokens.Input==null||x.Tokens.Output==null),
            unpriced=rows.Count(x=>x.Cost==null),cost=Total(rows)};
    }
    private static object Period(IEnumerable<DeepSeekCharge> rows){
        var list=rows.ToArray();
        return new{total=Sum(list),categories=list.GroupBy(x=>x.Category).ToDictionary(g=>g.Key,g=>Sum(g))};
    }
    private static DateTime BillingDate(DateTimeOffset at)=>at.ToOffset(TimeSpan.FromHours(8)).Date;
    public object Status(){
        var credentials=store.DeepSeekCredentials();
        if(!credentials.Official||credentials.Key.Length==0)return new{eligible=credentials.Official,configured=false};
        lock(gate){
            var account=Open(Identify(credentials.Key));
            var rows=account.Charges.Values.ToArray();
            var today=BillingDate(DateTimeOffset.UtcNow);
            return new{eligible=true,configured=true,account.Balance,account.Available,account.CheckedAt,account.Checking,account.Error,account.StorageError,rateVersion=RateVersion,
                run=Period(rows.Where(x=>x.At>=started)),today=Period(rows.Where(x=>BillingDate(x.At)==today)),lifetime=Period(rows)};
        }
    }

    public object? GameNotice(long seen){
        var credentials=store.DeepSeekCredentials();
        if(!credentials.Official||credentials.Key.Length==0||!store.Current.DeepSeekCostPopup)return null;
        var id=Identify(credentials.Key);
        if(!Monitor.TryEnter(gate))return null;
        try{
            var now=DateTimeOffset.UtcNow;
            var values=notices.Where(x=>x.Account==id&&x.Value.Id>seen&&now-x.Value.At<TimeSpan.FromSeconds(15)&&x.Value.Category!="probe").Select(x=>x.Value).Take(8).ToArray();
            if(values.Length==0)return null;
            return accounts.TryGetValue(id,out var account)
                ? new{items=values,account.Balance,account.CheckedAt}
                : new{items=values,Balance=Array.Empty<DeepSeekBalance>(),CheckedAt=(DateTimeOffset?)null};
        }finally{Monitor.Exit(gate);}
    }

    public async Task RefreshAsync(bool manual){
        var credentials=store.DeepSeekCredentials();
        if(!credentials.Official||credentials.Key.Length==0)return;
        var id=Identify(credentials.Key);
        Account account;
        lock(gate){
            account=Open(id);
            var now=DateTimeOffset.UtcNow;
            if(account.Checking||now-account.LastAttempt<TimeSpan.FromSeconds(manual?5:60))return;
            account.Checking=true;account.LastAttempt=now;
        }
        try{
            using var limit=new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var request=new HttpRequestMessage(HttpMethod.Get,"https://api.deepseek.com/user/balance");
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",credentials.Key);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,limit.Token).ConfigureAwait(false);
            if(!response.IsSuccessStatusCode){lock(gate)account.Error="余额接口返回 HTTP "+(int)response.StatusCode;return;}
            await response.Content.LoadIntoBufferAsync(16384,limit.Token).ConfigureAwait(false);
            using var document=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false));
            var balance=document.RootElement.GetProperty("balance_infos").EnumerateArray().Select(item=>
                new DeepSeekBalance(item.GetProperty("currency").GetString()??"",Money(item,"total_balance"),Money(item,"granted_balance"),Money(item,"topped_up_balance"))).ToArray();
            if(balance.Length==0||balance.Any(x=>x.Currency is not ("CNY" or "USD")))throw new JsonException("Invalid balance currency");
            var available=document.RootElement.GetProperty("is_available").GetBoolean();
            lock(gate){account.Available=available;account.Balance=balance;account.CheckedAt=DateTimeOffset.UtcNow;account.Error=null;}
        }catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){
            lock(gate)account.Error="暂时无法查询 DeepSeek 余额";
        }finally{lock(gate)account.Checking=false;}
    }
    private static decimal Money(JsonElement item,string key)=>decimal.Parse(item.GetProperty(key).GetString()??"",NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture);
}
