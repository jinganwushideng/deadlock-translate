using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeadlockTranslate;
public sealed record HotkeyStatus(bool Available,string Binding,string Label,bool ChatOpen,bool BridgeFresh,bool GameForeground,bool? BackendElevated,bool? GameElevated,int RegistrationError,long Triggered,long Acknowledged,long AckTimeouts,long Rejected,long Reinitializations,long TestDetected,bool TestActive,DateTime? LastTestUtc,string? LastRejection,bool? GameSameUser=null,bool ConfirmationTimedOut=false);

// The callback never reads configuration locks, inspects processes, serializes, or writes files.
public sealed class NativeHotkey : IDisposable {
    private readonly ConfigStore config;private readonly DiagnosticLog? log;
    private readonly Thread thread;private readonly HookProc callback;private readonly Timer foregroundTimer;
    private readonly HotkeyState keys=new();
    private readonly QuickMessageHotkeys quick=new();
    private readonly HotkeyNoiseSummary noise=new();
    private readonly HotkeyRecorder recorder;
    private volatile bool settingsOpen;private long lastSettingsSeen;
    private HotkeyChord binding;private volatile bool outgoingEnabled,chatOpen,stopping;
    private IntPtr hook;private uint threadId;private long lastSeen,sequence,triggerAt,acknowledged,ackTimeouts,rejected,reinitialized,testDetected,testUntil,lastRejectedAt,foregroundAt;
    private long lastTimedOut;private int installError,foregroundBusy,reinitializePending;
    private volatile uint foregroundPid;private volatile bool foregroundGame,foregroundBrowser;private bool? gameElevated,gameSameUser;private readonly bool? backendElevated;
    private long lastTestUtcTicks;private string? lastRejection;
    public bool Available=>Volatile.Read(ref hook)!=IntPtr.Zero&&thread.IsAlive;
    public NativeHotkey(ConfigStore config,DiagnosticLog? log=null){
        this.config=config;this.log=log;binding=HotkeyChord.Parse(config.Current.OutgoingHotkey);outgoingEnabled=config.Current.OutgoingEnabled;backendElevated=Elevated((uint)Environment.ProcessId);
        recorder=new(view=>log?.Record("hotkey",LogKind.HotkeyRecordingFinished,new(Stage:"recording",Reason:view.State=="expired"?LogReason.Timeout:view.State=="failed"?LogReason.Other:LogReason.None,Binding:view.State=="completed"?view.Binding:null,Flag:view.State=="completed")));
        quick.Configure(config.Current);registeredSettings=config.Current;callback=Handle;config.Changed+=Configure;thread=new Thread(Run){IsBackground=true,Name="DLT hotkey",Priority=ThreadPriority.AboveNormal};
        foregroundTimer=new(_=>SampleForeground(),null,0,500);thread.Start();
    }
    private Settings? registeredSettings;
    private readonly object configurationGate=new();
    private void Configure(){lock(configurationGate){var settings=config.Current;var old=registeredSettings;
        if(old!=null&&old.OutgoingHotkey==settings.OutgoingHotkey&&old.OutgoingEnabled==settings.OutgoingEnabled&&old.QuickMessages.SequenceEqual(settings.QuickMessages))return;
        registeredSettings=settings;Volatile.Write(ref binding,HotkeyChord.Parse(settings.OutgoingHotkey));outgoingEnabled=settings.OutgoingEnabled;quick.Configure(settings);Reinitialize();}}
    public void UpdateQuickSession(string session,bool compatible,string channel)=>quick.Session(session,compatible,channel);
    public QuickMessage[] TakeQuickMessages(string session)=>quick.Take(session,Environment.TickCount64);
    public void UpdateFocus(bool open){var previous=chatOpen;chatOpen=open;Interlocked.Exchange(ref lastSeen,Environment.TickCount64);if(open&&!previous)Reinitialize();}
    public static bool AllowsCapture(bool outgoingEnabled,bool chatOpen,long now,long lastSeen)=>outgoingEnabled&&chatOpen&&lastSeen>0&&now>=lastSeen&&now-lastSeen<5000;
    public void Reinitialize(){Interlocked.Exchange(ref reinitializePending,1);var id=Volatile.Read(ref threadId);if(id!=0)PostThreadMessage(id,0x8001,UIntPtr.Zero,IntPtr.Zero);}
    public void BeginTest(){recorder.Cancel();Interlocked.Exchange(ref testUntil,Environment.TickCount64+15000);Reinitialize();}
    public void UpdateSettings(bool open){settingsOpen=open;Interlocked.Exchange(ref lastSettingsSeen,Environment.TickCount64);if(!open)recorder.Cancel(gameOnly:true);}
    private bool SettingsFresh(long now)=>settingsOpen&&now-Interlocked.Read(ref lastSettingsSeen)<1500;
    public HotkeyRecordingView BeginRecording(bool inGame){
        if(!Available)throw new InvalidOperationException("快捷键监听未启用，请重建监听，或使用下方按键选项。");
        if(ReadModifiers()!=0||recorder.HasSuppressedKeys||quick.HasSuppressedKey)throw new InvalidOperationException("请先松开所有按键，再点击录制。");
        var window=GetForegroundWindow();GetWindowThreadProcessId(window,out var pid);
        if(window==IntPtr.Zero||pid==0)throw new InvalidOperationException("无法确认前台设置窗口，请使用下方按键选项。");
        using var process=Process.GetProcessById((int)pid);var name=process.ProcessName;
        var game=name.Equals("deadlock",StringComparison.OrdinalIgnoreCase)||name.Equals("citadel",StringComparison.OrdinalIgnoreCase);
        var browser=name is "msedge" or "chrome" or "firefox" or "brave" or "opera" or "vivaldi";
        if(StartupCompatibility.SameUser((int)pid)!=true)throw new InvalidOperationException("请使用原 Windows 用户打开设置窗口。");
        if(inGame?(!game||!SettingsFresh(Environment.TickCount64)):!browser)throw new InvalidOperationException(inGame?"未确认游戏内设置窗口已打开，请稍后重试；后台与 VPK 需成套更新。":"请在前台浏览器设置窗口录制，或直接使用按键选项。");
        if(Elevated(pid)==true&&backendElevated!=true)throw new InvalidOperationException("前台窗口是管理员权限，后台是普通权限。请使用相同权限；游戏必须管理员运行时可主动选择管理员兼容重启。也可使用下方按键选项。");
        Interlocked.Exchange(ref testUntil,0);var id=recorder.Begin(Environment.TickCount64,window,inGame);PostThreadMessage(Volatile.Read(ref threadId),0x8002,UIntPtr.Zero,IntPtr.Zero);
        log?.Record("hotkey",LogKind.HotkeyRecordingStarted,new(Stage:"recording"));
        return recorder.Status(id,Environment.TickCount64)!;
    }
    public HotkeyRecordingView? Recording(string id)=>recorder.Status(id,Environment.TickCount64);
    public void CancelRecording(string? id=null)=>recorder.Cancel(id);
    public long PendingSequence(long seen){
        var current=Interlocked.Read(ref sequence);var now=Environment.TickCount64;
        if(seen>=current&&current>Interlocked.Read(ref acknowledged)){Interlocked.Exchange(ref acknowledged,current);log?.Record("hotkey",LogKind.HotkeyAcknowledged,new(Count:(int)Math.Min(current,int.MaxValue)));}
        if(current>seen&&now-Interlocked.Read(ref triggerAt)<5000)return current;
        if(current>seen&&current>Interlocked.Read(ref lastTimedOut)){Interlocked.Exchange(ref lastTimedOut,current);Interlocked.Increment(ref ackTimeouts);log?.Record("hotkey",LogKind.HotkeyAckTimeout,new(Reason:LogReason.Timeout));}
        return 0;
    }
    public HotkeyStatus Status{
        get{var b=Volatile.Read(ref binding);var now=Environment.TickCount64;var current=Interlocked.Read(ref sequence);var ack=Interlocked.Read(ref acknowledged);return new(Available,b.Canonical,b.Label,chatOpen,AllowsCapture(true,true,now,Interlocked.Read(ref lastSeen)),foregroundGame,backendElevated,gameElevated,Volatile.Read(ref installError),current,ack,Interlocked.Read(ref ackTimeouts),Interlocked.Read(ref rejected),Interlocked.Read(ref reinitialized),Interlocked.Read(ref testDetected),now<Interlocked.Read(ref testUntil),Interlocked.Read(ref lastTestUtcTicks)>0?new DateTime(Interlocked.Read(ref lastTestUtcTicks),DateTimeKind.Utc):null,Volatile.Read(ref lastRejection),gameSameUser,current>ack&&now-Interlocked.Read(ref triggerAt)>=5000);}
    }
    private void SampleForeground(){
        if(stopping||Interlocked.Exchange(ref foregroundBusy,1)!=0)return;
        try{
            GetWindowThreadProcessId(GetForegroundWindow(),out var pid);var now=Environment.TickCount64;
            if(pid==foregroundPid&&now-Interlocked.Read(ref foregroundAt)<3000)return;
            var game=false;var browser=false;bool? elevated=null;bool? sameUser=null;
            if(pid!=0){try{using var p=Process.GetProcessById((int)pid);var name=p.ProcessName;game=name.Equals("deadlock",StringComparison.OrdinalIgnoreCase)||name.Equals("citadel",StringComparison.OrdinalIgnoreCase);browser=name is "msedge" or "chrome" or "firefox";if(game){elevated=Elevated(pid);sameUser=StartupCompatibility.SameUser((int)pid);}}catch{}}
            var entered=game&&(!foregroundGame||pid!=foregroundPid);
            if(game&&elevated==true&&backendElevated==false&&(entered||gameElevated!=true))log?.Record("hotkey",LogKind.PrivilegeMismatch,new(Reason:LogReason.ElevatedGame));
            if(game&&sameUser==false&&(entered||gameSameUser!=false))log?.Record("hotkey",LogKind.PrivilegeMismatch,new(Reason:LogReason.DifferentUser));
            foregroundGame=game;foregroundBrowser=browser;gameElevated=elevated;gameSameUser=sameUser;foregroundPid=pid;Interlocked.Exchange(ref foregroundAt,now);
            if(entered)Reinitialize();
        }finally{Interlocked.Exchange(ref foregroundBusy,0);}
    }
    private IntPtr Handle(int code,IntPtr w,IntPtr l){
        if(code<0||l==IntPtr.Zero)return CallNextHookEx(hook,code,w,l);
        try{
            var key=Marshal.ReadInt32(l);var scan=Marshal.ReadInt32(l,4);var flags=Marshal.ReadInt32(l,8);var message=w.ToInt32();var down=message is 0x100 or 0x104;var up=message is 0x101 or 0x105;
            var now=Environment.TickCount64;var b=Volatile.Read(ref binding);
            var foregroundWindow=GetForegroundWindow();GetWindowThreadProcessId(foregroundWindow,out var currentPid);
            var record=recorder.Process(key,scan,flags,down,up,now,foregroundWindow,SettingsFresh(now));
            if(record.BlockSending||record.Suppress){keys.Process(b,key,scan,flags,down,up,new(false,false,false,false,false,false));quick.Process(key,scan,flags,down,up,now,false);return record.Suppress?(IntPtr)1:CallNextHookEx(hook,code,w,l);}
            var validForeground=currentPid!=0&&currentPid==foregroundPid&&now-Interlocked.Read(ref foregroundAt)<5000;
            var game=validForeground&&foregroundGame&&gameSameUser!=false;var test=now<Interlocked.Read(ref testUntil);var settings=SettingsFresh(now);var testAllowed=validForeground&&(foregroundBrowser||(game&&(!chatOpen||settings)));
            if(quick.Process(key,scan,flags,down,up,now,outgoingEnabled&&game&&!chatOpen&&!settings&&!test&&AllowsCapture(true,true,now,Interlocked.Read(ref lastSeen))))return (IntPtr)1;
            var decision=keys.Process(b,key,scan,flags,down,up,new(outgoingEnabled,chatOpen,AllowsCapture(true,true,now,Interlocked.Read(ref lastSeen)),game&&!settings,testAllowed,test));
            if(decision.Action==HotkeyAction.Trigger){chatOpen=false;Interlocked.Exchange(ref triggerAt,now);Interlocked.Increment(ref sequence);log?.Record("hotkey",LogKind.HotkeyTriggered,new(Binding:b.Canonical));return (IntPtr)1;}
            if(decision.Action==HotkeyAction.Test){Interlocked.Increment(ref testDetected);Interlocked.Exchange(ref lastTestUtcTicks,DateTime.UtcNow.Ticks);log?.Record("hotkey",LogKind.HotkeyTest,new(Binding:b.Canonical));return (IntPtr)1;}
            if(decision.Action==HotkeyAction.Suppress)return (IntPtr)1;
            if(game&&down&&key==b.VirtualKey&&HotkeyNoiseSummary.IsNoise(decision.Reason,keys.Held,chatOpen))noise.Observe(decision.Reason,now);
            else if(game&&down&&key==b.VirtualKey&&decision.Reason!=LogReason.None&&now-Interlocked.Read(ref lastRejectedAt)>1000){Interlocked.Exchange(ref lastRejectedAt,now);Interlocked.Increment(ref rejected);Volatile.Write(ref lastRejection,decision.Reason.ToString());log?.Record("hotkey",LogKind.HotkeyRejected,new(Reason:decision.Reason,Binding:b.Canonical));}
        }catch{}
        return CallNextHookEx(hook,code,w,l);
    }
    private KeyModifiers ReadModifiers(){KeyModifiers held=0;foreach(var k in new[]{0xA2,0xA3,0xA4,0xA5,0xA0,0xA1,0x5B,0x5C})if((GetAsyncKeyState(k)&0x8000)!=0)held|=HotkeyChord.Modifier(k,0,0);return held;}
    private void Install(){
        var old=hook;if(old!=IntPtr.Zero)UnhookWindowsHookEx(old);
        hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);installError=hook==IntPtr.Zero?Marshal.GetLastWin32Error():0;var held=ReadModifiers();keys.Seed(held);quick.Seed(held);
        log?.Record("hotkey",hook==IntPtr.Zero?LogKind.HotkeyInstallFailed:LogKind.HotkeyInstalled,new(Win32Error:installError,Binding:Volatile.Read(ref binding).Canonical));
    }
    private void Run(){
        PeekMessage(out _,IntPtr.Zero,0,0,0);threadId=GetCurrentThreadId();Install();SetTimer(IntPtr.Zero,(UIntPtr)1,1000,IntPtr.Zero);
        try{while(!stopping&&GetMessage(out var msg,IntPtr.Zero,0,0)>0){
            if(msg.Id is 0x8001 or 0x8002 or 0x113){
                FlushNoise(Environment.TickCount64);
                quick.RecoverRelease(key=>(GetAsyncKeyState(key)&0x8000)!=0);
                if(!recorder.Active(Environment.TickCount64))recorder.RecoverReleases(key=>(GetAsyncKeyState(key)&0x8000)!=0);
                if(recorder.IsArming(Environment.TickCount64)&&!recorder.HasSuppressedKeys&&keys.SwallowedKey==0&&!quick.HasSuppressedKey&&ReadModifiers()==0){Install();if(hook==IntPtr.Zero)recorder.Fail("录制监听无法启动，可使用按键选项，或重建监听后重试。");else recorder.Activate(Environment.TickCount64,GetForegroundWindow(),SettingsFresh(Environment.TickCount64));}
                if(Volatile.Read(ref reinitializePending)!=0&&!recorder.Active(Environment.TickCount64)&&!recorder.HasSuppressedKeys){if(keys.SwallowedKey!=0)keys.RecoverRelease((GetAsyncKeyState(keys.SwallowedKey)&0x8000)!=0);var actual=ReadModifiers();if(keys.SwallowedKey==0&&!quick.HasSuppressedKey&&actual==0){Interlocked.Exchange(ref reinitializePending,0);Install();Interlocked.Increment(ref reinitialized);log?.Record("hotkey",LogKind.HotkeyReinitialized);}}
            }
            else{TranslateMessage(ref msg);DispatchMessage(ref msg);}
        }}finally{FlushNoise(Environment.TickCount64,true);KillTimer(IntPtr.Zero,(UIntPtr)1);if(hook!=IntPtr.Zero)UnhookWindowsHookEx(hook);hook=IntPtr.Zero;threadId=0;}
    }
    private void FlushNoise(long now,bool force=false){var count=noise.Drain(now,force);if(count>0)log?.Record("hotkey",LogKind.HotkeySummary,new(Reason:LogReason.ModifiersMismatch,Stage:"hotkey-summary",Count:count));}
    public void Dispose(){recorder.Cancel();stopping=true;config.Changed-=Configure;foregroundTimer.Dispose();var id=threadId;if(id!=0)PostThreadMessage(id,0x12,UIntPtr.Zero,IntPtr.Zero);thread.Join(2000);}
    private static bool? Elevated(uint pid){
        var process=OpenProcess(0x1000,false,pid);if(process==IntPtr.Zero)return null;
        try{if(!OpenProcessToken(process,8,out var token))return null;try{return GetTokenInformation(token,20,out var value,4,out _)?value!=0:null;}finally{CloseHandle(token);}}finally{CloseHandle(process);}
    }
    private delegate IntPtr HookProc(int n,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern int GetMessage(out Message message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool PeekMessage(out Message message,IntPtr window,uint min,uint max,uint remove);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint id,uint message,UIntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern UIntPtr SetTimer(IntPtr window,UIntPtr id,uint milliseconds,IntPtr callback);
    [DllImport("user32.dll")]private static extern bool KillTimer(IntPtr window,UIntPtr id);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(IntPtr token,int information,out int elevation,int bytes,out int required);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
