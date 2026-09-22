using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;
public record Term(string Source, string Target, string English, string Kind = "name", bool Ambiguous = false);
public record OutgoingTerm(string Chinese,string English,bool Ambiguous);
public sealed class Terms {
    private readonly string userPath;
    private Term[] defaults;
    private Term[] current = [];
    private readonly object gate = new();
    public int Revision { get; private set; }
    public Terms(string dataDirectory, string defaultsFile) {
        userPath = Path.Combine(dataDirectory, "terms.json");
        defaults = File.Exists(defaultsFile) ? JsonSerializer.Deserialize<Term[]>(File.ReadAllText(defaultsFile), ConfigStore.Json) ?? [] : [];
        current = File.Exists(userPath) ? JsonSerializer.Deserialize<Term[]>(File.ReadAllText(userPath), ConfigStore.Json) ?? [] : defaults;
        Validate(current);
    }
    public Term[] All { get { lock(gate) return current.ToArray(); } }
    public void Save(Term[] terms) { Validate(terms); lock(gate) { ConfigStore.WriteAtomic(userPath, JsonSerializer.SerializeToUtf8Bytes(terms,ConfigStore.Json)); current = terms; Revision++; } }
    public static void Validate(Term[] terms) {
        if (terms.Length > 5000 || terms.Any(t=>string.IsNullOrWhiteSpace(t.Source) || t.Source.Length > 100 || string.IsNullOrWhiteSpace(t.Target) || t.Target.Length > 150 || t.English.Length > 100 || (t.Kind != "name" && t.Kind != "phrase"))) throw new ArgumentException("词库最多 5000 条，词条不能为空或过长");
        if (terms.Select(t=>t.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count() != terms.Length) throw new ArgumentException("词库中存在重复原词");
    }
    public static string Pattern(string source) => @"(?<![\p{L}\p{N}_])" + Regex.Escape(source) + @"(?![\p{L}\p{N}_])";
    // Cheap ordinal shortlist first: do not construct a regex for every word in the expanded lexicon.
    public Term[] Match(string text) => All.Where(t=>text.Contains(t.Source,StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(text, Pattern(t.Source), RegexOptions.IgnoreCase|RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))).OrderByDescending(t=>t.Source.Length).Take(40).ToArray();
    public OutgoingTerm[] MatchOutgoing(string text) => All.Where(t=>t.Kind=="name" && t.English.Length>0)
        .SelectMany(t=>new[]{new OutgoingTerm(t.Target,t.English,t.Ambiguous),new OutgoingTerm(t.Source,t.English,t.Ambiguous)})
        .Where(t=>t.Chinese.Any(c=>c is >= '\u3400' and <= '\u9fff') && text.Contains(t.Chinese,StringComparison.Ordinal))
        // Canonical names retain their own ambiguity; aliases never weaken that decision.
        .GroupBy(t=>t.Chinese,StringComparer.Ordinal).Select(g=>new OutgoingTerm(g.Key,g.First().English,g.Any(t=>t.Ambiguous)))
        .OrderByDescending(t=>t.Chinese.Length).Take(30).ToArray();
    public string PrepareOutgoing(string text,OutgoingTerm[] glossary){
        var replacements=glossary.DistinctBy(t=>t.Chinese).OrderByDescending(t=>t.Chinese.Length).ToArray();
        if(replacements.Length==0)return text;
        var map=replacements.ToDictionary(t=>t.Chinese,StringComparer.Ordinal);
        return Regex.Replace(text,string.Join("|",replacements.Select(t=>Regex.Escape(t.Chinese))),m=>{
            var term=map[m.Value];
            var context=!term.Ambiguous || Regex.IsMatch(text[(m.Index+m.Length)..],@"^(?:没大招|有大招|大招|残血|消失|在后|换血|没技能|技能|开了)",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))
                || Regex.IsMatch(text[..m.Index],@"(?:集火|攻击|小心|救|抓|击杀|买|购买|出了|装备)\s*$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
            return context?term.English:m.Value;
        },RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50));
    }
    public string Display(Term t, bool bilingual) => t.Target + (bilingual && t.Kind == "name" && t.English.Length>0 ? "（"+t.English+"）" : "");
    public string? Exact(string text, bool bilingual) {
        var normalized = text.Trim().TrimEnd('!','?','.', '！','？','。').Trim();
        var term = All.FirstOrDefault(t=>!t.Ambiguous && string.Equals(t.Source, normalized, StringComparison.OrdinalIgnoreCase));
        return term == null ? null : Display(term,bilingual);
    }
    public static bool HasGameContext(string text, Term term) {
        if(!term.Ambiguous) return true;
        if(term.Kind!="name") return false;
        return Regex.Matches(text,Pattern(term.Source),RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50)).Any(m=>HasGameContextAt(text,m.Index,m.Length));
    }
    private static bool HasGameContextAt(string text,int index,int length){
        return Regex.IsMatch(text[(index+length)..],@"^\s+(?:(?:has|have)\s+(?:no\s+)?(?:ult|ultimate|cd|cooldown|stamina|hp|ammo)|used\s+(?:ult|ultimate|hook|bomb)|hooked|ult|ultimate|hook|bomb|clone|кд|нет\s+ульты|сзади|low|dead|missing|no\s+ult|is\s+(?:missing|behind|low|dead|one))\b",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50))
            || Regex.IsMatch(text[..index],@"\b(?:focus|kill|against|stun|silence|save|protect)\s+$",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));
    }
    public (string Text, Dictionary<string,string> Map) Protect(string text, bool bilingual, bool resolveContext = false, bool namesOnly = false) {
        var map = new Dictionary<string,string>();
        var spans=new List<(int Start,int Length,Term Term)>();
        foreach(var t in Match(text).Where(t=>!namesOnly || t.Kind=="name")){
            foreach(Match match in Regex.Matches(text,Pattern(t.Source),RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50))){
                if(t.Ambiguous && (!resolveContext || t.Kind!="name" || !HasGameContextAt(text,match.Index,match.Length)))continue;
                if(spans.Any(s=>match.Index<s.Start+s.Length && match.Index+match.Length>s.Start))continue;
                spans.Add((match.Index,match.Length,t));
            }
        }
        // Decide every occurrence against the original sentence, before replacing neighboring phrases.
        var output=new System.Text.StringBuilder();var end=0;
        foreach(var span in spans.OrderBy(s=>s.Start)){
            output.Append(text,end,span.Start-end);
            var token="DLTERM"+map.Count+"X";map[token]=Display(span.Term,bilingual);output.Append(token);
            end=span.Start+span.Length;
        }
        output.Append(text,end,text.Length-end);
        return (output.ToString(),map);
    }
    public static string Restore(string text, Dictionary<string,string> map) {
        foreach(var (key,value) in map) {
            var number = key[6..^1]; var regex = new Regex(@"DL\s*TERM\s*"+number+@"\s*X", RegexOptions.IgnoreCase);
            if (!regex.IsMatch(text)) throw new InvalidDataException("翻译服务损坏了术语占位符");
            text = regex.Replace(text, _=>value);
        }
        return text;
    }
    public static string NormalizeQualityNames(string text, Dictionary<string,string> map) {
        var index=0;
        foreach(var value in map.Values.Distinct()) {
            var split=value.IndexOf('（'); var chinese=split>=0?value[..split]:value;
            var english=split>=0?value[(split+1)..].TrimEnd('）'):"";
            var marker="DLTKEEPNAME"+(index++)+"X";
            if(text.Contains(value,StringComparison.Ordinal)) { text=text.Replace(value,marker,StringComparison.Ordinal); }
            else if(text.Contains(chinese,StringComparison.Ordinal)) { text=text.Replace(chinese,marker,StringComparison.Ordinal); }
            else if(english.Length>0 && Regex.IsMatch(text,@"(?<![（(])"+Pattern(english),RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50))) {
                text=Regex.Replace(text,@"(?<![（(])"+Pattern(english),_=>marker,RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(50));
            } else throw new InvalidDataException("精翻未保留明确的游戏专名");
            text=text.Replace(marker,value,StringComparison.Ordinal);
        }
        return text;
    }
}
