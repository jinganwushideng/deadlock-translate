namespace DeadlockTranslate;

public sealed record DisplayPreviewOptions(string Mode="native",int Opacity=90,int Width=340,string Position="center-left",int OffsetX=28,int OffsetY=0,int FontSize=17,int Seconds=8,int Lines=3,bool ShowHeroName=true,bool ShowAccountName=true,string HeroNamePosition="before",string ChineseNameStyle="current") {
    public static DisplayPreviewOptions From(Settings s)=>new(s.IncomingDisplayMode,s.IncomingOpacity,s.IncomingFeedWidth,s.IncomingFeedPosition,s.IncomingFeedOffsetX,s.IncomingFeedOffsetY,s.FontSize,s.IncomingFeedSeconds,s.IncomingFeedLines,s.IncomingShowHeroName,s.IncomingShowAccountName,s.IncomingHeroNamePosition,s.ChineseNameStyle);
    public void Validate(){
        if(Mode is not ("native" or "independent" or "both") || Seconds is <3 or >15 || Lines is <1 or >10)throw new ArgumentException("显示模式或显示时长、条数无效");
        if(Opacity is <20 or >100 || Width is <240 or >600 || OffsetX is <0 or >120 || OffsetY is <0 or >120 || FontSize is <12 or >24)throw new ArgumentException("透明度、宽度、边距或字号超出允许范围");
        if(Position is not ("top-left" or "top-center" or "top-right" or "center-left" or "center" or "center-right" or "bottom-left" or "bottom-center" or "bottom-right"))throw new ArgumentException("无效的独立区位置");
        if(HeroNamePosition is not ("before" or "after") || ChineseNameStyle is not ("current" or "legacy"))throw new ArgumentException("角色名称显示格式无效");
    }
}

public sealed record DisplayPreviewCommand(string Epoch,long Revision,bool Active,int RemainingMs,DisplayPreviewOptions? Options);
// One bounded, synthetic preview lease. It never creates translation or outgoing tasks.
public sealed class DisplayPreviewService(Func<long>? clock=null) {
    readonly object gate=new();readonly string epoch=Guid.NewGuid().ToString("N");readonly Func<long> now=clock??(()=>Environment.TickCount64);
    long revision,until;DisplayPreviewOptions? options;
    public DisplayPreviewCommand Start(DisplayPreviewOptions value){value.Validate();lock(gate){options=value;until=now()+12000;revision++;return Snapshot();}}
    public DisplayPreviewCommand Stop(){lock(gate){options=null;until=0;revision++;return Snapshot();}}
    public DisplayPreviewCommand Snapshot(){lock(gate){var remaining=(int)Math.Clamp(until-now(),0,12000);return new(epoch,revision,remaining>0&&options!=null,remaining,remaining>0?options:null);}}
}
