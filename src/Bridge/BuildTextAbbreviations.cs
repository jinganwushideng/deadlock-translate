using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeadlockTranslate;

// Candidates curated from the user-supplied glossary. They never become hard
// substitutions or alter the player's dictionary; only matched entries are sent.
internal static class BuildTextAbbreviations {
    private sealed record Entry(string Source,string[] Names,string[] OtherMeanings){
        internal Regex Pattern {get;}=new(@"(?<![\p{L}\p{N}_])"+Regex.Escape(Source)+@"(?![\p{L}\p{N}_])",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    }
    private sealed record FileData(Entry[] Entries);
    internal sealed record Hint(string Source,string[] Candidates,string[] OtherMeanings,bool Ambiguous=true);
    private static readonly Entry[] entries=Load();
    private static Entry[] Load(){
        try{
            var file=new FileInfo(Path.Combine(AppContext.BaseDirectory,"data","build-abbreviations.json"));
            if(!file.Exists||file.Length>256000)return [];
            return (JsonSerializer.Deserialize<FileData>(File.ReadAllBytes(file.FullName),ConfigStore.Json)?.Entries??[])
                .Where(e=>e.Source is {Length:>=2 and <=60}&&e.Names is {Length:>0 and <=8}&&e.OtherMeanings is {Length:<=8})
                .Take(200).OrderByDescending(e=>e.Source.Length).ToArray();
        }catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){return [];}
    }
    internal static Hint[] Match(string text,Terms terms,bool bilingual,bool chinese){
        text=WithoutLinks(text);
        return entries.Where(e=>e.Pattern.IsMatch(text))
            .Select(e=>new Hint(e.Source,e.Names.Select(n=>terms.Exact(n,bilingual,chinese)).Where(n=>!string.IsNullOrWhiteSpace(n)).Cast<string>().Distinct().ToArray(),e.OtherMeanings))
            .Where(h=>h.Candidates.Length>0).Take(8).ToArray();
    }
    internal static bool Contains(string text){text=WithoutLinks(text);return entries.Any(e=>e.Pattern.IsMatch(text));}
    private static string WithoutLinks(string text)=>Regex.Replace(text,
        @"(?:https?://|www\.)\S+|\b[\w-]+(?:\.[\w-]+)*\.[a-z]{2,24}(?:/\S*)?"," ",
        RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
}
