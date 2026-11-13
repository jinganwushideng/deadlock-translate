namespace DeadlockTranslate;

internal static class BuildTextBridge {
    // Keep native HTML titles small. Each part is acknowledged before the next
    // is exposed; reading a response part never repeats the provider request.
    internal const string Page="""
<!doctype html><meta charset="utf-8"><style>html,body{background:transparent}</style><script>
let pending,incoming,outgoing;
const size=1200,maximum=1024;
function title(value){document.title='DLTBUILD:'+JSON.stringify(value);}
function part(index){
    if(outgoing&&index>=0&&index<outgoing.parts.length)
        title({id:outgoing.id,wire:1,part:index,count:outgoing.parts.length,data:outgoing.parts[index]});
}
function publish(result){
    const text=JSON.stringify(result);
    if(text.length<=1400){outgoing=null;title(result);return;}
    const encoded=encodeURIComponent(text),parts=[];
    for(let i=0;i<encoded.length;i+=size)parts.push(encoded.slice(i,i+size));
    if(parts.length>maximum){title({id:result.id,error:'unavailable'});return;}
    outgoing={id:result.id,parts};part(0);
}
async function run(input){
    pending?.abort();
    const request=new AbortController();pending=request;outgoing=null;
    try{
        const response=await fetch(location.pathname,{
            method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(input),signal:request.signal
        });
        const result=response.ok?await response.json():{id:input.id,error:'unavailable'};
        if(pending===request&&!request.signal.aborted)publish(result);
    }catch{
        if(pending===request&&!request.signal.aborted)publish({id:input.id,error:'unavailable'});
    }
}
function translate(){
    let input;
    try{
        input=JSON.parse(decodeURIComponent(location.hash.slice(1)));
        if(input.wire!==1){incoming=null;run(input);return;}
        if(!Number.isSafeInteger(input.id)||input.id<0)return;
        if(Number.isInteger(input.read)){
            if(outgoing&&outgoing.id===input.id)part(input.read);
            return;
        }
        if(!Number.isInteger(input.part)||!Number.isInteger(input.count)||input.count<1||input.count>maximum||
            input.part<0||input.part>=input.count||typeof input.data!=='string'||input.data.length>size)return;
        if(!incoming||incoming.id!==input.id){
            if(input.part!==0)return;
            pending?.abort();outgoing=null;
            incoming={id:input.id,count:input.count,parts:[],sent:false};
        }
        if(incoming.count!==input.count)return;
        if(incoming.sent){title({id:input.id,ack:input.part});return;}
        if(input.part===incoming.parts.length)incoming.parts.push(input.data);
        else if(incoming.parts[input.part]!==input.data)return;
        title({id:input.id,ack:input.part});
        if(incoming.parts.length===incoming.count){
            incoming.sent=true;
            const assembled=JSON.parse(decodeURIComponent(incoming.parts.join('')));
            incoming.parts=[];
            if(assembled.id!==input.id)throw Error('id mismatch');
            run(assembled);
        }
    }catch{if(input&&Number.isSafeInteger(input.id))title({id:input.id,error:'unavailable'});}
}
addEventListener('hashchange',translate);
addEventListener('pagehide',()=>pending?.abort());
if(location.hash)translate();
</script>
""";
}
