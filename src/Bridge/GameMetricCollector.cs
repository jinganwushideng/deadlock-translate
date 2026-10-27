namespace DeadlockTranslate;

// Fixed schema only: no panel names, raw session IDs, URLs or chat text.
public sealed record GameMetricSummary(int Scans,int Rows,int Submitted,int Rendered,int Bridges,int Errors,double MaxScanMs,int RegularRows,int HudRows,int LobbyRows,int HudContainers,int HudRebinds,int HudRendered,int HudRecovered,int HudHostRepairs,int HudHiddenRepairs,int HudBindings,int HudExpiredResults,int HudDeferredScans,int GameLogDrops,int HudWaits,int HudRetryRecovered,int HudNativeExpired,int HudPanelGone,int HudRetryDropped,int FeedRendered,int FeedExpired,int FeedDropped,PerformanceSample? Performance=null) {
    public static GameMetricSummary From(Metrics m) => new(m.Scans,m.Rows,m.Submitted,m.Rendered,m.Bridges,m.Errors,m.MaxScanMs,m.RegularRows,m.HudRows,m.LobbyRows,m.HudContainers,m.HudRebinds,m.HudRendered,m.HudRecovered,m.HudHostRepairs,m.HudHiddenRepairs,m.HudBindings,m.HudExpiredResults,m.HudDeferredScans,m.GameLogDrops,m.HudWaits,m.HudRetryRecovered,m.HudNativeExpired,m.HudPanelGone,m.HudRetryDropped,m.FeedRendered,m.FeedExpired,m.FeedDropped,m.Performance?.Clean());
    public GameMetricSummary Clean() {
        static int Count(int value) => Math.Clamp(value,0,2_000_000_000);
        return this with {Performance=Performance?.Clean(),Scans=Count(Scans),Rows=Count(Rows),Submitted=Count(Submitted),Rendered=Count(Rendered),Bridges=Count(Bridges),Errors=Count(Errors),MaxScanMs=double.IsFinite(MaxScanMs)?Math.Clamp(MaxScanMs,0,60000):0,RegularRows=Count(RegularRows),HudRows=Count(HudRows),LobbyRows=Count(LobbyRows),HudContainers=Math.Clamp(HudContainers,0,8),HudRebinds=Count(HudRebinds),HudRendered=Count(HudRendered),HudRecovered=Count(HudRecovered),HudHostRepairs=Count(HudHostRepairs),HudHiddenRepairs=Count(HudHiddenRepairs),HudBindings=Count(HudBindings),HudExpiredResults=Count(HudExpiredResults),HudDeferredScans=Count(HudDeferredScans),GameLogDrops=Count(GameLogDrops),HudWaits=Count(HudWaits),HudRetryRecovered=Count(HudRetryRecovered),HudNativeExpired=Count(HudNativeExpired),HudPanelGone=Count(HudPanelGone),HudRetryDropped=Count(HudRetryDropped),FeedRendered=Count(FeedRendered),FeedExpired=Count(FeedExpired),FeedDropped=Count(FeedDropped)};
    }
}
public sealed class GameMetricCollector(DiagnosticLog log) {
    readonly object gate=new();string session="";GameMetricSummary? latest;long lastSample;bool stopped;
    void Emit(string stage) { if(latest!=null)log.Record("game",LogKind.GameMetrics,new(Token:DiagnosticLog.Token(session,"metrics"),Stage:stage,Counters:latest)); }
    public void Accept(string current,Metrics metrics,long now) {
        lock(gate) {
            if(stopped)return;
            if(current!=session) { Emit("metrics-session-end");session=current;latest=null;lastSample=now-30000; }
            var next=GameMetricSummary.From(metrics).Clean();
            // Native counter reset without an observable match ID is still a new segment.
            if(latest!=null && next.Scans<latest.Scans) {Emit("metrics-reset");lastSample=now-30000;}
            latest=next;
            if(now-lastSample>=30000) {Emit("metrics-sample");lastSample=now;}
        }
    }
    public void Stop() { lock(gate) {if(stopped)return;Emit("metrics-stop");stopped=true;} }
}
