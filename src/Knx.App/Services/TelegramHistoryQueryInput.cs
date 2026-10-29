using System.Globalization;
using System.Text.RegularExpressions;
using Knx.Bus.History;

namespace Knx.App.Services;

public sealed record TelegramHistoryQueryInput(string FromUtc,string UntilUtc,string Sources="",string Destinations="",
    string Services="",string SourceIds="",string ProjectIds="",string? Origin=null,int Context=2)
{
    public TelegramQuery Parse(long generation)
    {
        if(!TryUtc(FromUtc,out var from)||!TryUtc(UntilUtc,out var until)||until<=from)throw new FormatException("HistoryInvalidTimeRange");
        if(Context is <0 or >20)throw new FormatException("HistoryInvalidContext");
        return new TelegramQuery { FromUtc=from,UntilUtc=until,Sources=Values(Sources),Destinations=Values(Destinations),
            Services=Values(Services),SourceIds=Values(SourceIds),ProjectIds=Values(ProjectIds),Origins=Origin is null?[]:[Origin],
            PageSize=Math.Min(100,4096/(1+2*Context)),ContextBefore=Context,ContextAfter=Context,NewestFirst=true,RequestGeneration=generation };
    }
    public static bool TryUtc(string text,out DateTimeOffset value)
    {
        text=text.Trim();
        var fullTimestamp=text.Length<=40&&Regex.IsMatch(text,@"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d{1,7})?([Zz]|[+-]\d{2}:\d{2})$",RegexOptions.CultureInvariant);
        if(fullTimestamp&&DateTimeOffset.TryParse(text,CultureInfo.InvariantCulture,DateTimeStyles.None,out value))
        {value=value.ToUniversalTime();return true;}
        value=default;return false;
    }
    private static IReadOnlyList<string> Values(string text)
    {
        if(text.Length>16384)throw new FormatException("HistoryTooManyFilters");
        var values=text.Split(';',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if(values.Length>64||values.Any(v=>v.Length>256))throw new FormatException("HistoryTooManyFilters");
        return values;
    }
}
