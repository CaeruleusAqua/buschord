using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Knx;
using Knx.Bus;

internal static class TelegramMetadataChecks
{
    internal static void Run(Action<string, bool> check)
    {
        var recorder = new TelegramRecorder();
        recorder.Start();
        foreach (var hop in new byte[] { 0, 4, 7 })
            recorder.Add(new BusTelegram
            {
                Source = "synthetic-tunnel", Timestamp = new DateTimeOffset(2026, 10, 28, 12, 0, hop, TimeSpan.Zero),
                Telegram = new Telegram
                {
                    Source = new IndividualAddress(1, 1, 1), Destination = new IndividualAddress(GroupAddress.ThreeLevel(1, 1, 1).Raw),
                    IsGroupAddress = true, Apci = Apci.GroupValueWrite, HopCount = hop, Data = [0x12, 0x34],
                },
            });
        recorder.Stop();
        var expected = new byte?[] { 0, 4, 7 };
        void Check(string name, bool value) => check("telegram metadata: " + name, value);
        Check("captured hop counts retain zero and nondefault values", recorder.Entries.Select(entry => entry.HopCount).SequenceEqual(expected));
        var rows = recorder.ToEtsCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(line => line.TrimEnd('\r').Split(';')).ToArray();
        Check("monitor export keeps thirteen columns", rows.All(row => row.Length == 13));
        Check("uncaptured flags priority and ack remain blank", rows.All(row => row[2] == "" && row[3] == "" && row[11] == ""));
        Check("monitor export uses each captured hop count", rows.Select(row => row[8]).SequenceEqual(new[] { "0", "4", "7" }));
        var parent = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(parent, "buschord-telegram-metadata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var json = Path.Combine(root, "capture.jsonl");
            var xml = Path.Combine(root, "capture.xml");
            var csv = Path.Combine(root, "capture.csv");
            recorder.SaveJsonLines(json);
            var jsonRows = TelegramRecorder.LoadJsonLines(json);
            Check("JSONL roundtrip retains captured hop count", jsonRows.Select(entry => entry.HopCount).SequenceEqual(expected));
            Check("JSONL roundtrip retains decoded payload without pretending it is cEMI", jsonRows.All(entry => entry.Data == "1234"));
            recorder.SaveXml(xml);
            var xmlRows = TelegramRecorder.LoadXml(xml);
            Check("XML roundtrip retains captured hop count", xmlRows.Select(entry => entry.HopCount).SequenceEqual(expected));
            Check("XML timestamps remain exact", xmlRows.Select(entry => entry.Timestamp).SequenceEqual(recorder.Entries.Select(entry => entry.Timestamp)));
            recorder.SaveCsv(csv);
            Check("legacy recording keeps its six-column layout", File.ReadAllLines(csv).All(line => line.Split(';').Length == 6));
            Check("legacy recording does not invent hop count", TelegramRecorder.LoadCsv(csv).All(entry => entry.HopCount is null));
            recorder.Entries[1].HopCount = null;
            var unknownRow = recorder.ToEtsCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries)[2].TrimEnd('\r').Split(';');
            Check("unknown hop count exports blank", unknownRow[8] == "");
            recorder.SaveJsonLines(json);
            Check("JSONL roundtrip preserves missing hop count", TelegramRecorder.LoadJsonLines(json)[1].HopCount is null);
            recorder.SaveXml(xml);
            Check("XML roundtrip preserves missing hop count", TelegramRecorder.LoadXml(xml)[1].HopCount is null
                && XDocument.Load(xml).Root!.Elements("Event").ElementAt(1).Attribute("HopCount") is null);
            File.WriteAllText(json, JsonSerializer.Serialize(new { Timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture), Data = "00" }));
            Check("old JSON without metadata loads as unknown", TelegramRecorder.LoadJsonLines(json).Single().HopCount is null);
            var invalid = XDocument.Load(xml);
            invalid.Root!.Elements("Event").First().SetAttributeValue("HopCount", "8");
            invalid.Save(xml);
            var rejected = false;
            try { TelegramRecorder.LoadXml(xml); } catch (InvalidDataException) { rejected = true; }
            Check("invalid captured hop count is rejected", rejected);
            rejected = false;
            try { recorder.Entries[0].HopCount = 255; } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check("public capture field cannot retain an invalid hop count", rejected && recorder.Entries[0].HopCount == 0);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(parent)
                && Path.GetFileName(resolved).StartsWith("buschord-telegram-metadata-", StringComparison.Ordinal)
                && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
}
