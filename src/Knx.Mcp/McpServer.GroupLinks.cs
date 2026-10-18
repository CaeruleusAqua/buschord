using System.Text.Json;
using System.Text.Json.Nodes;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private static JsonObject GroupLinkSchema() => Schema(
        StrProp("device_address", "Individual address, e.g. 1.1.5.", true),
        StrProp("object_id", "Communication object reference id from communication_objects_list (not ComObjectId).", true),
        StrProp("group_address", "Existing group address, e.g. 1/1/1.", true));

    private object CommunicationObjectsList(JsonElement args)
    {
        var project = Current;
        var device = LinkDevice(project, LinkArgument(args, "device_address"));
        var objects = device.ComObjectRefs.Select(obj => new
        {
            id = obj.Id,
            comObjectId = obj.ComObjectId,
            number = obj.Number,
            text = obj.Text,
            functionText = obj.FunctionText,
            links = obj.Links.Select(id => new
            {
                id,
                address = ProjectQuery.FindGroupAddressById(project, id)?.Address.ToString(),
                name = ProjectQuery.FindGroupAddressById(project, id)?.Name,
            }).ToList(),
        }).ToList();
        return new { deviceAddress = device.Address.ToString(), count = objects.Count, communicationObjects = objects };
    }

    private object GroupLinkChange(JsonElement args, bool add)
    {
        var project = Current;
        var device = LinkDevice(project, LinkArgument(args, "device_address"));
        var objectId = LinkArgument(args, "object_id");
        var address = ParseAddress(LinkArgument(args, "group_address"));
        var entries = ProjectQuery.GroupAddresses(project).Where(a => a.Address == address).Take(2).ToList();
        if (entries.Count != 1) throw new ToolException($"group address {address} not found or ambiguous");
        bool changed;
        try
        {
            changed = add
                ? GroupLinks.Link(project, device, objectId, entries[0].Id)
                : GroupLinks.Unlink(project, device, objectId, entries[0].Id);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException(ex.Message);
        }
        if (changed) _undo.Push(project);
        return new { changed, linked = add, deviceAddress = device.Address.ToString(), objectId,
            groupAddress = Address(entries[0]), undo = History() };
    }

    private static DeviceInstance LinkDevice(Project project, string value)
    {
        var address = ParseIndividualAddress(value);
        var devices = ProjectQuery.DeviceList(project).Where(d => d.Address == address).Take(2).ToList();
        return devices.Count == 1 ? devices[0] : throw new ToolException($"device {address} not found or ambiguous");
    }

    private static string LinkArgument(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new ToolException($"'{name}' must be a non-empty string");
}
