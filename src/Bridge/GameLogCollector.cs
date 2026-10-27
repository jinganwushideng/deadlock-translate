namespace DeadlockTranslate;

// Numeric geometry only; never panel IDs, usernames, text, screenshots or arbitrary data.
public sealed record HudLayoutEvidence(double? LabelWidth=null,double? LabelHeight=null,double? SourceWidth=null,double? SourceHeight=null,double? HostWidth=null,double? HostHeight=null,double? RowWidth=null,double? RowHeight=null,double? LabelX=null,double? LabelY=null,double? AnchorX=null,double? AnchorY=null) {
    public HudLayoutEvidence Clean() {
        static double? Value(double? value,bool coordinate=false)=>value is double n&&double.IsFinite(n)&&n>=(coordinate?-16384:0)&&n<=16384?Math.Round(n,1):null;
        return new(Value(LabelWidth),Value(LabelHeight),Value(SourceWidth),Value(SourceHeight),Value(HostWidth),Value(HostHeight),Value(RowWidth),Value(RowHeight),Value(LabelX,true),Value(LabelY,true),Value(AnchorX,true),Value(AnchorY,true));
    }
}
// Fixed numeric roles and at most eight ancestors. Unknown/computed style values
// stay null; local bounds are not claimed to be final screen clipping rectangles.
public sealed record HudAncestorEvidence(int Level,int Role,double? Width=null,double? Height=null,double? X=null,double? Y=null,double? Opacity=null,int Flags=0,int Overflow=0) {
    public static HudAncestorEvidence[]? Clean(HudAncestorEvidence[]? nodes) {
        if(nodes==null)return null;
        static double? Number(double? value,double min,double max)=>value is double n&&double.IsFinite(n)&&n>=min&&n<=max?Math.Round(n,2):null;
        return nodes.Take(8).Where(n=>n!=null&&n.Level is >=0 and <8&&n.Role is >=0 and <=9).Select(n=>n with{Width=Number(n.Width,0,16384),Height=Number(n.Height,0,16384),X=Number(n.X,-16384,16384),Y=Number(n.Y,-16384,16384),Opacity=Number(n.Opacity,0,1),Flags=n.Flags is >=0 and <=511?n.Flags:0,Overflow=n.Overflow is >=0 and <=3?n.Overflow:0}).ToArray();
    }
}
public sealed record FeedLayoutEvidence(double? RootWidth=null,double? RootHeight=null,double? RootX=null,double? RootY=null,double? CardWidth=null,double? CardHeight=null,double? TextWidth=null,double? TextHeight=null,int Flags=0,double RemainingMs=0) {
    public FeedLayoutEvidence Clean() {
        static double? Number(double? n,bool coordinate=false)=>n is double v&&double.IsFinite(v)&&v>=(coordinate?-16384:0)&&v<=16384?Math.Round(v,1):null;
        return new(Number(RootWidth),Number(RootHeight),Number(RootX,true),Number(RootY,true),Number(CardWidth),Number(CardHeight),Number(TextWidth),Number(TextHeight),Flags is >=0 and <=511?Flags:0,double.IsFinite(RemainingMs)?Math.Clamp(RemainingMs,0,15000):0);
    }
}
public sealed record GameLogEvent(long Seq,string Code,string? Job=null,string? Channel=null,int? Count=null,double? ElapsedMs=null,string? Reason=null,HudLayoutEvidence? Layout=null,HudAncestorEvidence[]? Ancestry=null,FeedLayoutEvidence? Feed=null);
public sealed class GameLogCollector(DiagnosticLog log){
    private readonly object gate=new();private string session="";private long last;
    private static readonly HashSet<string> Codes=new(["send-echo-observed","send-echo-unobserved","waiting_input","dispatched","echo_observed","echo_unobserved","send-close-request","send-close-confirmed","send-close-interrupted","send-close-timeout","send-close-retry","send-draft-open","send-draft-unavailable","send-draft-queued","send-unconfirmed","channel-cached","settings-background","settings-input","hud-nudge-start","hud-nudge-recovered","hud-nudge-failed","feed-layout","feed-restored","own-hidden","own-display","hud-root-reflow","hud-native-born","hud-local-receipt","hud-direct-applied","hud-native-write-failed","hud-context-applied","hud-context-unavailable","hud-context-rejected","hud-ancestry-sample","hud-template-paint","hud-template-paint-missing","hud-template-render-state","hud-template-api","hud-template-bound","hud-template-layout","hud-anchor-mounted","hud-anchor-layout","hud-anchor-unavailable","hud-anchor-hidden","hud-anchor-retired","hud-anchor-displaced","hud-layout-nudge","hud-layout-stalled","hud-layout-before","hud-layout-after","hud-refresh","hud-refresh-unavailable","hero-roster","hero-resolved","hero-unavailable","hud-wait","hud-retired","hud-restored","hud-retry-ended","feed-mounted","feed-unavailable","feed-written","feed-ended","feed-displaced","display-mode","bridge-bound","bridge-rebuilt","bridge-reconnect","adapter-loop-resumed","adapter-root-moved","hud-bound","hud-written","hud-expired","hud-host-repaired","hud-hidden-repaired","hud-rebound","adapter-start","session-reset","bridge-timeout","bridge-error","bridge-reset","transport-fallback","menu-mounted","menu-failed","settings-open","settings-close","hotkey-captured","hotkey-dropped","channel-wait","channel-fault","send-submitted","send-failed","chat-open","chat-closed"]);
    public long Accept(string current,GameLogEvent[] events){
        if(events.Length>16||events.Any(x=>x==null||x.Code==null||x.Seq is <1 or >1000000000||x.Code.Length>40||x.Job?.Length>100||x.Reason?.Length>40||x.Channel?.Length>16||x.Count is <0 or >1000000||x.ElapsedMs is double d&&(!double.IsFinite(d)||d<0||d>86400000)))throw new ArgumentException("游戏诊断批次无效");
        lock(gate){if(current!=session){session=current;last=0;}foreach(var item in events.OrderBy(x=>x.Seq)){
            if(item.Seq<=last)continue;last=item.Seq;
            if(!Codes.Contains(item.Code))continue;
            var reason=item.Reason switch{"template-missing"=>LogReason.DisplayTemplateMissing,"geometry"=>LogReason.DisplayGeometryUnavailable,"hidden"=>LogReason.DisplayHidden,"container-missing"=>LogReason.DisplayContainerMissing,"host-missing"=>LogReason.DisplayHostMissing,"detached"=>LogReason.DisplayDetached,"panel-gone"=>LogReason.DisplayPanelGone,"native-expired"=>LogReason.DisplayNativeExpired,"message-changed"=>LogReason.DisplayMessageChanged,"retry-expired"=>LogReason.DisplayRetryExpired,"input-truncated"=>LogReason.NativeInputTruncated,"empty"=>LogReason.EmptyInput,"unknown-channel"=>LogReason.UnknownChannel,"no-chat"=>LogReason.NoChat,"timeout"=>LogReason.Timeout,"disabled"=>LogReason.Disabled,"queue-full"=>LogReason.QueueFull,"native-close"=>LogReason.NativeCloseFailed,"old-client"=>LogReason.ClientOld,null or ""=>LogReason.None,_=>LogReason.Other};
            log.Record("game",LogKind.GameEvent,new(Reason:reason,Token:item.Job==null?null:DiagnosticLog.Token(current,item.Job),Stage:item.Code,Channel:item.Channel,Count:item.Count,ElapsedMs:item.ElapsedMs,Layout:item.Layout?.Clean(),Ancestry:HudAncestorEvidence.Clean(item.Ancestry),Feed:item.Feed?.Clean()));
        }return last;}
    }
}
