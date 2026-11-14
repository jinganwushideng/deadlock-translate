using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DeadlockTranslate;

// Launched on a private window station by tests/clipboard-native.test.py.
// Never reads or changes the interactive user's clipboard.
internal static class ClipboardNativeFixture {
    internal static async Task Run(string output,string expectedStation) {
        Directory.CreateDirectory(output);
        var station=new StringBuilder(256);
        if(!GetUserObjectInformation(GetProcessWindowStation(),2,station,512,out _)||station.ToString()!=expectedStation||station.ToString().Equals("WinSta0",StringComparison.OrdinalIgnoreCase))
            throw new Exception("Refusing to test the interactive clipboard");
        if(CountClipboardFormats()!=0)throw new Exception("Refusing a private station with existing clipboard data");
        var window=CreateWindowEx(0,"STATIC","",0,0,0,0,0,new IntPtr(-3),IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
        if(window==IntPtr.Zero)throw new Exception("Private clipboard owner window failed");
        var png=RegisterClipboardFormat("PNG");
        using var capture=new BuildSourceCapture(Path.Combine(output,"state"));
        try {
            // CMsgHeroBuild: id=101, hero=17, name, version=1.
            var name=Encoding.UTF8.GetBytes("Clipboard fixture");
            var raw=new byte[]{8,101,16,17,42,(byte)name.Length}.Concat(name).Concat(new byte[]{64,1}).ToArray();
            using var encoder=new ZstdSharp.Compressor();
            var export=Convert.ToBase64String(encoder.Wrap(raw).ToArray());
            for(var round=0;round<20;round++){
                var text="Original text "+round;
                var pixels=Enumerable.Repeat((byte)0x44,64).ToArray();
                var bitmap=CreateBitmap(4,4,1,32,pixels);
                if(bitmap==IntPtr.Zero)throw new Exception("Bitmap creation failed");
                Open(window);try{
                    Check(OwnClipboard(),"Another process owns the noninteractive clipboard");
                    Check(EmptyClipboard(),"Empty");Put(13,Encoding.Unicode.GetBytes(text+'\0'));
                    Check(SetClipboardData(2,bitmap)!=IntPtr.Zero,"Set bitmap");Put(png,[1,2,3,4]);
                }finally{CloseClipboard();}
                var prepared=JsonSerializer.SerializeToElement(await capture.Prepare("Clipboard fixture",101,1));
                Check(!prepared.GetProperty("manual").GetBoolean(),"Bitmap fixture unexpectedly required manual export");
                Open(window);try{Check(OwnClipboard(),"Clipboard changed externally");Check(EmptyClipboard(),"Empty export");Put(13,Encoding.Unicode.GetBytes(export+'\0'));}finally{CloseClipboard();}
                var result=JsonSerializer.SerializeToElement(await capture.Read(prepared.GetProperty("token").GetString()!));
                Check(result.TryGetProperty("source",out _),"Source not captured: "+result);
                Check(result.GetProperty("clipboard").GetString()=="restored","Clipboard was not restored");
                Open(window);try{
                    var h=GetClipboardData(13);var pointer=GlobalLock(h);
                    try{Check(Marshal.PtrToStringUni(pointer)==text,"Text changed");}finally{GlobalUnlock(h);}
                    Check(GetObject(GetClipboardData(2),Marshal.SizeOf<Bitmap>(),out var restored)!=0&&restored.Width==4&&restored.Height==4,"Bitmap changed");
                    Check(GlobalSize(GetClipboardData(png))==4,"Registered PNG blob changed");
                }finally{CloseClipboard();}
            }
            var unknown=RegisterClipboardFormat("DLT_TEST_OPAQUE_FORMAT");
            Open(window);try{Check(OwnClipboard(),"Clipboard changed externally");EmptyClipboard();Put(unknown,[1,2,3,4]);}finally{CloseClipboard();}
            var sequence=GetClipboardSequenceNumber();
            var manual=JsonSerializer.SerializeToElement(await capture.Prepare("Clipboard fixture",101,1));
            Check(manual.GetProperty("manual").GetBoolean()&&GetClipboardSequenceNumber()==sequence,"Unknown format was touched");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{nonInteractiveStation=true,rounds=20,textRestored=true,bitmapRestored=true,pngRestored=true,unknownFormatUntouched=true}));
        }finally{
            Open(window);try{if(OwnClipboard())EmptyClipboard();}finally{CloseClipboard();}
            DestroyWindow(window);
        }
    }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static bool OwnClipboard(){if(CountClipboardFormats()==0)return true;GetWindowThreadProcessId(GetClipboardOwner(),out var pid);return pid==Environment.ProcessId;}
    private static void Open(IntPtr window){for(var i=0;i<100;i++){if(OpenClipboard(window))return;Thread.Sleep(10);}throw new Exception("Clipboard busy");}
    private static void Put(uint format,byte[] data){
        var memory=GlobalAlloc(2,(nuint)data.Length);var pointer=GlobalLock(memory);
        try{Marshal.Copy(data,0,pointer,data.Length);}finally{GlobalUnlock(memory);}
        if(SetClipboardData(format,memory)==IntPtr.Zero){GlobalFree(memory);throw new Exception("Set clipboard memory failed");}
    }
    [StructLayout(LayoutKind.Sequential)] private struct Bitmap {internal int Type,Width,Height,WidthBytes;internal ushort Planes,BitsPixel;internal IntPtr Bits;}
    [DllImport("user32.dll")]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="GetUserObjectInformationW")]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,uint size,out uint needed);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr CreateWindowEx(uint extended,string name,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr param);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(IntPtr window);
    [DllImport("user32.dll")]private static extern bool OpenClipboard(IntPtr window);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll")]private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]private static extern int CountClipboardFormats();
    [DllImport("user32.dll")]private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll")]private static extern IntPtr SetClipboardData(uint format,IntPtr value);
    [DllImport("user32.dll")]private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalAlloc(uint flags,nuint size);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("kernel32.dll")]private static extern nuint GlobalSize(IntPtr handle);
    [DllImport("gdi32.dll")]private static extern IntPtr CreateBitmap(int width,int height,uint planes,uint bits,byte[] data);
    [DllImport("gdi32.dll",EntryPoint="GetObjectW")]private static extern int GetObject(IntPtr handle,int size,out Bitmap bitmap);
}
