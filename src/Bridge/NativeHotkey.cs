using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeadlockTranslate;

// Observes only the requested chord. It never types, pastes, changes focus, or reads game memory.
public sealed class NativeHotkey : IDisposable {
    private readonly ConfigStore config;
    private readonly Thread thread;
    private readonly HookProc callback;
    private IntPtr hook;
    private uint threadId;
    private long lastSeen,sequence,triggerAt;
    private volatile bool chatOpen,swallowedSpace;
    private uint cachedPid;
    private bool cachedIsGame;
    public bool Available => hook!=IntPtr.Zero;
    public NativeHotkey(ConfigStore config) {this.config=config;callback=Handle;thread=new Thread(Run){IsBackground=true,Name="DLT hotkey"};thread.Start();}
    public void UpdateFocus(bool open) {chatOpen=open;Interlocked.Exchange(ref lastSeen,Environment.TickCount64);}
    public long PendingSequence(long seen) {var current=Interlocked.Read(ref sequence);return current>seen && Environment.TickCount64-Interlocked.Read(ref triggerAt)<2500?current:0;}
    // The chat input must have been reported open and the game must still be talking to us. A short
    // freshness window on every report made the chord depend on the exact poll timing, which made
    // it fail intermittently right after the player opened the chat box.
    public static bool AllowsCapture(bool outgoingEnabled,bool chatOpen,long now,long lastSeen)=>outgoingEnabled && chatOpen && now-lastSeen<5000;
    private IntPtr Handle(int code,IntPtr w,IntPtr l) {
        if(code<0)return CallNextHookEx(hook,code,w,l);
        var key=Marshal.ReadInt32(l);
        if(key!=0x20)return CallNextHookEx(hook,code,w,l);
        var message=w.ToInt32();var down=message==0x100 || message==0x104;var up=message==0x101 || message==0x105;
        if(up && swallowedSpace){swallowedSpace=false;return (IntPtr)1;}
        if(down && swallowedSpace)return (IntPtr)1;
        if(down && AllowsCapture(config.Current.OutgoingEnabled,chatOpen,Environment.TickCount64,Interlocked.Read(ref lastSeen)) && (GetAsyncKeyState(0xA4)&0x8000)!=0) {
            GetWindowThreadProcessId(GetForegroundWindow(),out var pid);
            if(pid!=cachedPid) {cachedPid=pid;try{cachedIsGame=Process.GetProcessById((int)pid).ProcessName.Equals("deadlock",StringComparison.OrdinalIgnoreCase);}catch{cachedIsGame=false;}}
            if(cachedIsGame) {swallowedSpace=true;chatOpen=false;Interlocked.Exchange(ref triggerAt,Environment.TickCount64);Interlocked.Increment(ref sequence);return (IntPtr)1;}
        }
        return CallNextHookEx(hook,code,w,l);
    }
    private void Run() {threadId=GetCurrentThreadId();hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);if(hook==IntPtr.Zero)return;while(GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}UnhookWindowsHookEx(hook);hook=IntPtr.Zero;}
    public void Dispose(){if(threadId!=0)PostThreadMessage(threadId,0x12,IntPtr.Zero,IntPtr.Zero);thread.Join(1000);}
    private delegate IntPtr HookProc(int n,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)]private struct Message{public IntPtr Window;public uint Id;public UIntPtr W;public IntPtr L;public uint Time;public int X,Y;public uint Private;}
    [DllImport("user32.dll")]private static extern IntPtr SetWindowsHookEx(int id,HookProc callback,IntPtr module,uint thread);
    [DllImport("user32.dll")]private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]private static extern int GetMessage(out Message message,IntPtr window,uint min,uint max);
    [DllImport("user32.dll")]private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")]private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll")]private static extern bool PostThreadMessage(uint id,uint message,IntPtr w,IntPtr l);
}
