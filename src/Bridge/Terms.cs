using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;
public record Term(string Source, string Target, string English, string Kind = "name", bool Ambiguous = false, string LegacyTarget = "");
public record OutgoingTerm(string Chinese,string English,bool Ambiguous);
public record TermMatchPreview(string Source,string Target,string Display,string Action,int Start,int Length);
public record TermPreviewInput(string Text,string Direction="incoming",string Provider="quality",bool Bilingual=true,string NameStyle="current");
public record PreparedTermText(string Text,Dictionary<string,string> Map,Term[] Hints,int Revision=0,TermMatchPreview[]? Matches=null);
public sealed class Terms {
    private readonly string userPath;
    private sealed class Snapshot {
        public readonly Term[] Items;
        public readonly int Revision;
        public readonly Dictionary<string,Term> Exact;
        public readonly OutgoingTerm[] Outgoing;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string,Regex> patterns=new(StringComparer.OrdinalIgnoreCase);
        public Snapshot(Term[] items,int revision=0){
            Revision=revision;
            Items=items.ToArray();Exact=Items.ToDictionary(t=>t.Source,StringComparer.OrdinalIgnoreCase);
            Outgoing=Items.Where(t=>t.Kind=="name"&&t.English.Length>0)
                .SelectMany(t=>new[]{new OutgoingTerm(t.Target,t.English,t.Ambiguous),new OutgoingTerm(t.Source,t.English,t.Ambiguous)})
                .Where(t=>t.Chinese.Any(IsHan)).GroupBy(t=>t.Chinese,StringComparer.Ordinal)
                .Select(g=>new OutgoingTerm(g.Key,g.First().English,g.Any(t=>t.Ambiguous)))
                .OrderByDescending(t=>t.Chinese.Length).ToArray();
        }
        // Bounded by this snapshot's validated 5000 entries (and Chinese reverse aliases).
        // Avoid the process-wide 15-regex cache thrashing on messages with many names.
        public Regex Matcher(string source)=>patterns.GetOrAdd(source,s=>new Regex(Pattern(s),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)));
    }
    private Snapshot current;
    private readonly object gate = new();
    public int Revision => Volatile.Read(ref current).Revision;
    public object Preview(TermPreviewInput input,Settings settings) {
        if(input.Text==null||input.Text.Length is <1 or >1024||input.Direction is not ("incoming" or "outgoing")||input.Provider is not ("fast" or "quality")||input.NameStyle is not ("current" or "legacy"))throw new ArgumentException("请输入最多 1024 字的测试文本并选择有效方向、引擎与译名");
        var outgoing=input.Direction=="outgoing";
        var language=outgoing?TranslationLanguages.Outgoing(settings):TranslationLanguages.Incoming(settings);
        var chinese=!outgoing&&language.ChineseGlossary;
        var prepared=chinese?Prepare(input.Text,input.Bilingual,true,input.Provider=="quality",true,true):PrepareOutgoingNames(input.Text,true);
        var exact=outgoing?null:Exact(input.Text,input.Bilingual,chinese);
        string Render(string value)=>RenderChineseStyle(value,input.Text,chinese&&input.NameStyle=="legacy");
        return new {revision=prepared.Revision,localOnly=true,exact=exact==null?null:Render(exact),
            prepared=Render(Restore(prepared.Text,prepared.Map)),matches=prepared.Matches?.Select(m=>m with{Display=Render(m.Display)}),
            note="仅检查已保存词库，不调用云端，不发送聊天；歧义和短语提示的最终处理取决于上下文与模型。"};
    }
    public Terms(string dataDirectory, string defaultsFile) {
        userPath = Path.Combine(dataDirectory, "terms.json");
        var defaults = File.Exists(defaultsFile) ? JsonSerializer.Deserialize<Term[]>(File.ReadAllText(defaultsFile), ConfigStore.Json) ?? [] : [];
        var items = File.Exists(userPath) ? JsonSerializer.Deserialize<Term[]>(File.ReadAllText(userPath), ConfigStore.Json) ?? [] : defaults;
        Validate(items);current=new(items);
    }
    public Term[] All => Volatile.Read(ref current).Items.ToArray();
    public void Save(Term[] terms) { var copy=terms.ToArray();Validate(copy);lock(gate) { var next=new Snapshot(copy,current.Revision+1);ConfigStore.WriteAtomic(userPath, JsonSerializer.SerializeToUtf8Bytes(copy,ConfigStore.Json));Volatile.Write(ref current,next); } }
    public static void Validate(Term[] terms) {
        if (terms.Length > 5000 || terms.Any(t=>string.IsNullOrWhiteSpace(t.Source) || t.Source.Length > 100 || string.IsNullOrWhiteSpace(t.Target) || t.Target.Length > 150 || t.English.Length > 100 || t.LegacyTarget==null || t.LegacyTarget.Length>150 || (t.Kind != "name" && t.Kind != "phrase"))) throw new ArgumentException("词库最多 5000 条，词条不能为空或过长；旧译名最多 150 字");
        if (terms.Select(t=>t.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != terms.Length) throw new ArgumentException("词库中存在重复原词");
    }
    // Han sentences have no word spaces. Keep Latin/Cyrillic token boundaries, while
    // allowing both 中文外号没大招 and Bebop没大招. Never match QSR inside qsrhelper.
    private const string SpacedWordChar = @"[\p{L}\p{N}_-[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}]]";
    private static bool IsHan(char c) => c is >= '\u3400' and <= '\u9fff';
    public static string Pattern(string source) => (source.Length>0 && IsHan(source[0])?"":"(?<!"+SpacedWordChar+")")
        + Regex.Escape(source) + (source.Length>0 && IsHan(source[^1])?"":"(?!"+SpacedWordChar+")");
    // Cheap ordinal shortlist first: do not construct a regex for every word in the expanded lexicon.
    public Term[] Match(string text) => Match(text,Volatile.Read(ref current));
    private static Term[] Match(string text,Snapshot snapshot) => snapshot.Items.Where(t=>text.Contains(t.Source,StringComparison.OrdinalIgnoreCase) && snapshot.Matcher(t.Source).IsMatch(text)).OrderByDescending(t=>t.Source.Length).Take(40).ToArray();
    public OutgoingTerm[] MatchOutgoing(string text) => MatchOutgoing(text,Volatile.Read(ref current));
    private static OutgoingTerm[] MatchOutgoing(string text,Snapshot snapshot) {
        if(!text.Any(IsHan))return [];
        return snapshot.Outgoing.Where(t=>text.Contains(t.Chinese,StringComparison.Ordinal)).Take(30).ToArray();
    }
    public string PrepareOutgoing(string text,OutgoingTerm[] glossary){
        var replacements=glossary.DistinctBy(t=>t.Chinese).OrderByDescending(t=>t.Chinese.Length).ToArray();
        if(replacements.Length==0)return text;
        var map=replacements.ToDictionary(t=>t.Chinese,StringComparer.Ordinal);
        return Regex.Replace(text,string.Join("|",replacements.Select(t=>Regex.Escape(t.Chinese))),m=>{
            var term=map[m.Value];
            var context=!term.Ambiguous || HasGameContextAt(text,m.Index,m.Length);
            return context?term.English:m.Value;
        },RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
    }
    public string Display(Term t, bool bilingual,bool chinese=true) => chinese
        ? t.Target + (bilingual && t.Kind == "name" && t.English.Length>0 ? "（"+t.English+"）" : "")
        : t.English.Length>0?t.English:t.Source;
    public string RenderChineseStyle(string translated,string source,bool legacy) {
        if(!legacy)return translated;
        // Only names relevant to this message can change. A global text rewrite could
        // turn an unrelated sentence about a mech toy into a reference to the Walker.
        // Canonical names are still validated internally before choosing a display style.
        var matched=Match(source);
        var pairs=All.Where(t=>(t.Kind=="name"||t.LegacyTarget.Length>0)
                && matched.Any(m=>m.English.Length>0&&m.English==t.English || m.Target.Contains(t.Target,StringComparison.Ordinal)))
            .GroupBy(t=>t.Target,StringComparer.Ordinal).Select(g=>g.OrderByDescending(t=>matched.Any(m=>m.Source==t.Source)).ThenByDescending(t=>t.Source==t.English).First())
            .OrderByDescending(t=>t.Target.Length).ToArray();
        if(!pairs.Any(t=>t.LegacyTarget.Length>0&&t.LegacyTarget!=t.Target))return translated;
        // Include unchanged/blank names as longer identity matches, so a shorter
        // overlapping name cannot override an explicit "keep current" preference.
        var map=pairs.ToDictionary(t=>t.Target,t=>t.LegacyTarget.Length>0?t.LegacyTarget:t.Target,StringComparer.Ordinal);
        return Regex.Replace(translated,string.Join("|",pairs.Select(t=>Pattern(t.Target))),m=>map[m.Value],RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
    }
    public string? Exact(string text, bool bilingual,bool chinese=true) {
        var normalized = text.Trim().TrimEnd('!','?','.', '！','？','。').Trim();
        var term = Volatile.Read(ref current).Exact.GetValueOrDefault(normalized);
        if(term?.Ambiguous==true)return null;
        if(!chinese&&(term?.Kind!="name"||string.IsNullOrEmpty(term.English)))return null;
        return term == null ? null : Display(term,bilingual,chinese);
    }
    public static bool HasGameContext(string text, Term term) {
        if(!term.Ambiguous) return true;
        if(term.Kind!="name") return false;
        return Regex.Matches(text,Pattern(term.Source),RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50)).Any(m=>HasGameContextAt(text,m.Index,m.Length));
    }
    private static bool HasGameContextAt(string text,int index,int length){
        var after=text[(index+length)..];var before=text[..index];
        return Regex.IsMatch(after,@"^\s+(?:(?:has|have)\s+(?:no\s+)?(?:ult|ultimate|cd|cooldown|stamina|hp|ammo)|used\s+(?:ult|ultimate|hook|bomb)|hooked|ult|ultimate|hook|bomb|clone|кд|(?:нет|без)\s+(?:ульты|ульта)|на\s+кд|сзади|лоу|пропал|low|dead|missing|no\s+(?:ult|ultimate)|is\s+(?:missing|behind|low|dead|one|vulnerable))\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(before,@"\b(?:focus|kill|against|stun|silence|save|protect|фокус|бей|бейте|убей|убейте|спаси|прикрой)\s+$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(after,@"^\s*(?:没大招|有大招|大招|残血|丝血|消失|在后|换血|没技能|技能|开了|在(?:中路|左路|右路|蓝路|黄路|紫路|绿路))",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(before,@"(?:集火|攻击|小心|救(?:一下)?|抓|击杀|买|购买|买个|买一个|买一件|出个|出一个|出一件|出了|补个|补一个|装备|拆|守|推|护送|拿|运)\s*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
    }
    public (string Text, Dictionary<string,string> Map) Protect(string text, bool bilingual, bool resolveContext = false, bool namesOnly = false,bool chinese=true) {
        var p=Prepare(text,bilingual,resolveContext,namesOnly,chinese);return(p.Text,p.Map);
    }
    public PreparedTermText Prepare(string text,bool bilingual,bool resolveContext=true,bool namesOnly=true,bool chinese=true,bool preview=false) {
        var snapshot=Volatile.Read(ref current);return ProtectMatches(text,Match(text,snapshot),snapshot,bilingual,resolveContext,namesOnly,chinese,preview);
    }
    public (string Text, Dictionary<string,string> Map) ProtectOutgoing(string text) {
        var p=PrepareOutgoingNames(text);return(p.Text,p.Map);
    }
    public PreparedTermText PrepareOutgoingNames(string text,bool preview=false) {
        // Protect against the original text: converting 狼女 to ambiguous "Silver" first
        // would lose the certainty supplied by that nickname. Include Chinese canonical names.
        var snapshot=Volatile.Read(ref current);
        var matched=Match(text,snapshot)
            .Concat(MatchOutgoing(text,snapshot).Select(t=>new Term(t.Chinese,t.Chinese,t.English,"name",t.Ambiguous)))
            .DistinctBy(t=>t.Source,StringComparer.OrdinalIgnoreCase).OrderByDescending(t=>t.Source.Length).ToArray();
        return ProtectMatches(text,matched,snapshot,false,true,true,false,preview);
    }
    private PreparedTermText ProtectMatches(string text,Term[] matched,Snapshot snapshot,bool bilingual,bool resolveContext,bool namesOnly,bool chinese,bool collectPreview=false) {
        var map = new Dictionary<string,string>();
        var preview=new List<TermMatchPreview>();
        var spans=new List<(int Start,int Length,Term Term)>();
        foreach(var t in matched){
            foreach(Match match in snapshot.Matcher(t.Source).Matches(text)){
                var action=spans.Any(s=>match.Index<s.Start+s.Length&&match.Index+match.Length>s.Start)?"overlap":
                    namesOnly&&t.Kind!="name"?"hint":t.Ambiguous&&(!resolveContext||t.Kind!="name"||!HasGameContextAt(text,match.Index,match.Length))?"ambiguous":"protected";
                if(collectPreview&&preview.Count<120)preview.Add(new(t.Source,t.Target,Display(t,bilingual,chinese),action,match.Index,match.Length));
                if(action!="protected")continue;
                spans.Add((match.Index,match.Length,t));
            }
        }
        // Decide every occurrence against the original sentence, before replacing neighboring phrases.
        var output=new System.Text.StringBuilder();var end=0;
        var occupiedTokens=TokenMatcher.Matches(text).Select(m=>"DLTERM"+m.Groups[1].Value+"X").ToHashSet(StringComparer.Ordinal);
        var tokenIndex=0;
        foreach(var span in spans.OrderBy(s=>s.Start)){
            output.Append(text,end,span.Start-end);
            string token;do{token="DLTERM"+(tokenIndex++)+"X";}while(occupiedTokens.Contains(token));
            map[token]=Display(span.Term,bilingual,chinese);output.Append(token);
            end=span.Start+span.Length;
        }
        output.Append(text,end,text.Length-end);
        // Resolved names are already in current/requiredNames. Only uncovered phrases
        // and unresolved ambiguities need model hints; never hint a shorter overlapping name.
        var hints=matched.Where(t=>snapshot.Matcher(t.Source).Matches(text).Any(m=>!spans.Any(s=>m.Index>=s.Start&&m.Index+m.Length<=s.Start+s.Length))).ToArray();
        return new(output.ToString(),map,hints,snapshot.Revision,preview.ToArray());
    }
    private static readonly Regex TokenMatcher=new(@"DL\s*TERM\s*(\d+)\s*X",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
    public static string Restore(string text, Dictionary<string,string> map) {
        if(map.Count==0)return text;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var result=TokenMatcher.Replace(text,m=>{var key="DLTERM"+m.Groups[1].Value+"X";if(!map.TryGetValue(key,out var value))return m.Value;seen.Add(key);return value;});
        if(seen.Count!=map.Count)throw new InvalidDataException("翻译服务损坏了术语占位符");
        return result;
    }
    public static string NormalizeQualityNames(string text, Dictionary<string,string> map) {
        var index=0;
        foreach(var value in map.Values.Distinct()) {
            var split=value.IndexOf('（'); var chinese=split>=0?value[..split]:value;
            var english=split>=0?value[(split+1)..].TrimEnd('）'):"";
            var marker="DLTKEEPNAME"+(index++)+"X";
            if(split<0 && !value.Any(IsHan)) {
                // International names are whole tokens; normalize harmless casing without
                // accepting an unrelated longer word as proof that a required name survived.
                if(!Regex.IsMatch(text,Pattern(value),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)))throw new InvalidDataException("精翻未保留明确的游戏专名");
                text=Regex.Replace(text,Pattern(value),_=>marker,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
            }
            else if(text.Contains(value,StringComparison.Ordinal)) { text=text.Replace(value,marker,StringComparison.Ordinal); }
            else if(text.Contains(chinese,StringComparison.Ordinal)) { text=text.Replace(chinese,marker,StringComparison.Ordinal); }
            else if(english.Length>0 && Regex.IsMatch(text,@"(?<![（(])"+Pattern(english),RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50))) {
                text=Regex.Replace(text,@"(?<![（(])"+Pattern(english),_=>marker,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));
            } else throw new InvalidDataException("精翻未保留明确的游戏专名");
            text=text.Replace(marker,value,StringComparison.Ordinal);
        }
        return text;
    }
    public string NormalizeOutgoingQualityNames(string text,Dictionary<string,string> map) {
        // Resolve only required, known names. Formatting and unambiguous glossary
        // aliases can be repaired locally; absent names/pronouns are never invented.
        text=TokenMatcher.Replace(text,m=>map.GetValueOrDefault("DLTERM"+m.Groups[1].Value+"X",m.Value));
        var snapshot=Volatile.Read(ref current);
        foreach(var name in map.Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(n=>n.Length)) {
            var canonicalPattern=Pattern(name);
            if(Regex.IsMatch(text,canonicalPattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)))continue;
            var flexible=canonicalPattern.Replace(@"\ ",@"[\s\p{Pd}_]+",StringComparison.Ordinal);
            text=Regex.Replace(text,flexible,_=>name,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
            if(Regex.IsMatch(text,canonicalPattern,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)))continue;
            var aliases=snapshot.Items.Where(t=>t.Kind=="name"&&!t.Ambiguous&&t.English.Equals(name,StringComparison.OrdinalIgnoreCase))
                .SelectMany(t=>new[]{t.Source,t.Target,t.LegacyTarget}).Where(a=>a.Length>=2&&!a.Equals(name,StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(a=>a.Length);
            foreach(var alias in aliases)text=Regex.Replace(text,Pattern(alias),_=>name,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
        }
        return NormalizeQualityNames(text,map);
    }
}
