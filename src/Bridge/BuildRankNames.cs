using System.Globalization;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;

// Valve's citadel_main English/Simplified Chinese localization, Citadel_ranks_rank11.
// Keep this scoped to guide text: ordinary words/handles in chat are unaffected.
internal static class BuildRankNames {
    private const string Official="不朽之星";
    private static readonly Regex Rank=new(
        @"(?<skip>(?:https?://|www\.)[^\s<>]+|\b[\w-]+(?:\.[\w-]+)*\.[a-z]{2,24}(?:/[^\s<>]*)?|@[\w-]+)|(?<rank>(?<![a-z0-9_.@])Eternus(?![a-z0-9_]|['’]s\b|\.[a-z0-9]))",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bilingual=new(@"不朽之星[ \t]*[（(](?:Eternus|不朽之星)[）)]",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));

    internal static bool Contains(string text)=>Rank.Matches(text).Any(m=>m.Groups["rank"].Success);
    internal static bool TryExact(string text,out string value){
        value=text.Equals("Eternus",StringComparison.OrdinalIgnoreCase)?Official:"";
        return value.Length!=0;
    }
    internal static PreparedTermText Protect(PreparedTermText prepared){
        if(!Contains(prepared.Text))return prepared;
        var map=new Dictionary<string,string>(prepared.Map);
        var suffix=0;
        var text=Rank.Replace(prepared.Text,m=>{
            if(!m.Groups["rank"].Success)return m.Value;
            string key;
            do{key="DLTERM"+(suffix++).ToString(CultureInfo.InvariantCulture)+"X";}
            while(map.ContainsKey(key)||prepared.Text.Contains(key,StringComparison.OrdinalIgnoreCase));
            map.Add(key,Official);
            return key;
        });
        return prepared with{Text=text,Map=map};
    }
    internal static bool TryNormalize(string source,string translation,out string value){
        value=translation;
        if(!Contains(source))return true;
        var candidate=translation.Length==0?source:translation;
        value=Bilingual.Replace(Rank.Replace(candidate,m=>m.Groups["rank"].Success?Official:m.Value),Official);
        return value.Contains(Official,StringComparison.Ordinal);
    }
}
