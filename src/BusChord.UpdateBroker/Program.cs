using System.Text.Json;
using Knx.Updates;
if (args is ["--recover", var recoveryPath])
{
    try { await ApplyBroker.RecoverAsync(recoveryPath); return 0; }
    catch (Exception error) { Console.Error.WriteLine("Code recovery failed: " + error.GetType().Name); return 1; }
}
if (args is not ["--apply", var path]) return 2;
try
{
    if (new FileInfo(path).Length > 65536) return 2;
    var ticket = JsonSerializer.Deserialize<ApplyTicket>(await File.ReadAllTextAsync(path));
    if (ticket is null || Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(ticket.StateDirectory)) return 2;
    try { return await ApplyBroker.ApplyAsync(ticket, ProductionUpdateTrust.Value); }
    finally { File.Delete(path); }
}
catch (Exception error) { Console.Error.WriteLine("Update apply failed: " + error.GetType().Name); return 1; }
