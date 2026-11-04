using System.Runtime.InteropServices;

namespace DeadlockTranslate;

public sealed class SecretClipboardException(string code,string message) : Exception(message) {
    public string Code { get; }=code;
}
public sealed record SecretPasteRequest(string Field);

// Only called by the explicit authenticated settings-page paste action. Nothing
// here reads stored credentials, alters the clipboard, or records its contents.
public static class SecretClipboard {
    private const uint UnicodeText=13;
    private static readonly object gate=new();
    public static bool ValidField(string? field)=>field is "llmKey" or "azureKey";
    public static string Validate(string? value){
        if(value==null||value.Length>8192)throw new SecretClipboardException("clipboard-size","剪贴板内容过长；请只复制 API 密钥本身。");
        var key=value.Trim();
        if(key.Length==0)throw new SecretClipboardException("clipboard-empty","剪贴板没有文字；请先复制 API 密钥，再点击粘贴。");
        if(key.Length>4096||key.Any(c=>c<'!'||c>'~'))throw new SecretClipboardException("clipboard-format","剪贴板不是单行密钥；请只复制密钥，不要包含说明文字、空格或换行。");
        return key;
    }
    public static string ReadKey(){
        lock(gate){
            var opened=false;
            for(var i=0;i<3;i++){if(OpenClipboard(IntPtr.Zero)){opened=true;break;}Thread.Sleep(40);}
            if(!opened)throw new SecretClipboardException("clipboard-busy","剪贴板暂被其他程序占用，请稍后重新点击粘贴。");
            try{
                if(!IsClipboardFormatAvailable(UnicodeText))throw new SecretClipboardException("clipboard-empty","剪贴板没有文字；请先复制 API 密钥，再点击粘贴。");
                var handle=GetClipboardData(UnicodeText);
                if(handle==IntPtr.Zero)throw new SecretClipboardException("clipboard-unavailable","无法读取剪贴板文字，请重新复制密钥后再试，或用 Settings.cmd 打开浏览器设置。");
                var bytes=GlobalSize(handle).ToUInt64();
                if(bytes<2||bytes>64*1024)throw new SecretClipboardException("clipboard-size","剪贴板内容过长或无效；请只复制 API 密钥本身。");
                var buffer=GlobalLock(handle);
                if(buffer==IntPtr.Zero)throw new SecretClipboardException("clipboard-unavailable","无法读取剪贴板文字，请重新复制后再试。");
                try{
                    // Search only inside the bounded HGLOBAL. Never trust a missing
                    // terminator to keep Marshal.PtrToStringUni inside that buffer.
                    var length=0;var capacity=(int)Math.Min(bytes/2,8193);
                    while(length<capacity&&Marshal.ReadInt16(buffer,length*2)!=0)length++;
                    if(length==capacity)throw new SecretClipboardException("clipboard-size","剪贴板内容过长或无效；请只复制 API 密钥本身。");
                    return Validate(Marshal.PtrToStringUni(buffer,length));
                }finally{GlobalUnlock(handle);}
            }finally{CloseClipboard();}
        }
    }
    [DllImport("user32.dll",SetLastError=true)]private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")]private static extern bool CloseClipboard();
    [DllImport("user32.dll")]private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")]private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern UIntPtr GlobalSize(IntPtr memory);
}
