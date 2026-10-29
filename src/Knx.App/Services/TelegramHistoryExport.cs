using System.Globalization;
using System.Text;
using Knx.Bus.History;

namespace Knx.App.Services;

/// <summary>Exports explicitly loaded rows. Unknown metadata remains empty; payload and raw cEMI are distinct.</summary>
public static class TelegramHistoryExport
{
    public static string Csv(IEnumerable<TelegramHistoryRow> rows)
    {
        var output=new StringBuilder("Id,TimestampUtc,Source,Destination,IsGroup,Service,APDU,Origin,SourceId,ProjectId,HopCount,RawCemi,Flags,Priority,Acknowledged,IsContext\r\n");
        foreach(var row in rows)
        {
            var e=row.Entry;
            output.Append(Knx.Csv.Row(',',e.Id,e.TimestampUtc.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture),e.Source,e.Destination,
                e.IsGroup.ToString(),e.Service,e.DataHex,e.Origin,e.SourceId,e.ProjectId??"",e.HopCount?.ToString(CultureInfo.InvariantCulture)??"",
                e.RawCemiHex??"",e.Flags??"",e.Priority??"",e.Acknowledged?.ToString()??"",(!row.IsMatch).ToString())).Append("\r\n");
        }
        return output.ToString();
    }
}
