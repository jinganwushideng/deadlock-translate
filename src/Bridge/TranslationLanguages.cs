using System.Text.RegularExpressions;

namespace DeadlockTranslate;

public record TranslationLanguage(string Id,string Name,string? ServiceCode,bool ChineseGlossary=false);
public static class TranslationLanguages {
    public static readonly TranslationLanguage[] Presets=[
        new("zh-Hans","简体中文","zh-Hans",true),new("zh-Hant","繁体中文","zh-Hant"),
        new("en","英语","en"),new("ru","俄语","ru"),new("ja","日语","ja"),new("ko","韩语","ko"),
        new("de","德语","de"),new("fr","法语","fr"),new("es","西班牙语","es"),new("pt","葡萄牙语（巴西）","pt"),
        new("it","意大利语","it"),new("tr","土耳其语","tr"),new("pl","波兰语","pl"),new("uk","乌克兰语","uk"),
        new("vi","越南语","vi"),new("th","泰语","th"),new("id","印度尼西亚语","id"),new("ar","阿拉伯语","ar")
    ];
    public static TranslationLanguage Incoming(Settings settings)=>Resolve(settings.IncomingTargetLanguage,settings.IncomingCustomLanguage,settings.IncomingCustomLanguageCode);
    public static TranslationLanguage Outgoing(Settings settings)=>Resolve(settings.OutgoingTargetLanguage,settings.OutgoingCustomLanguage,settings.OutgoingCustomLanguageCode);
    public static TranslationLanguage Resolve(string id,string customName="",string customCode="") {
        if(id==null||customName==null||customCode==null)throw new ArgumentException("目标语言字段不能为空值");
        var preset=Presets.FirstOrDefault(p=>p.Id==id);
        if(preset!=null)return preset;
        if(id!="custom")throw new ArgumentException("无效的目标语言");
        if(string.IsNullOrWhiteSpace(customName)||customName.Length>80||customName.Any(char.IsControl))throw new ArgumentException("自定义语言名称不能为空、含控制字符或超过80字符");
        if(customCode.Length>32||customCode.Any(char.IsControl)||(customCode.Length>0&&!Regex.IsMatch(customCode,@"\A[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*\z",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))))
            throw new ArgumentException("自定义快译语言代码格式无效，例如 nl、pt-pt、zh-Hant；仅使用大模型时可以留空");
        return new("custom",customName.Trim(),customCode.Length==0?null:customCode);
    }
    public static string RequireServiceCode(TranslationLanguage language)=>language.ServiceCode??throw new InvalidOperationException("自定义语言尚未填写快译语言代码，请填写服务支持的代码或选择仅高质量精翻");
}
