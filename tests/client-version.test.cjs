// Observe real game-script packets, including future versions, without a game.
const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
const world=require('./helpers/chat-world.cjs');
const root=path.join(__dirname,'..'),source=fs.readFileSync(process.env.DLT_CHAT_TEST_SOURCE||path.join(root,'mod/panorama/scripts/dlt_chat.js'),'utf8');
const current=JSON.parse(fs.readFileSync(path.join(root,'build-info.json'),'utf8'));
let passed=0;
for(const buildId of [current.buildId,'0.7.1-0123456789ab','1.0.0-abcdef012345','0.7.0.1-9876543210ab']){
 const version=buildId.slice(0,buildId.indexOf('-'));
 const candidate=source.replace(/var BUILD_ID = '[^']*';/,"var BUILD_ID = '"+buildId+"';");
 const w=world(candidate);w.row('version handshake fixture');w.step(12);
 assert(w.packets.length>0&&w.requests.length===1,'fixture must reach the real packet path');
 assert(w.packets.every(p=>p.clientVersion===version&&p.clientBuildId===buildId),'version and build identity diverged: '+buildId);
 passed++;console.log('PASS outgoing game packets identify '+buildId);
}
console.log(JSON.stringify({passed,realGame:false,cloudCalls:0}));
