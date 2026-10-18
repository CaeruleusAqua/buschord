using System.Text.Json;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private object ProjectSearchTool(JsonElement args)
    {
        var project = Current;
        var query = SearchQueryFrom(args);
        var result = SearchRun(() => ProjectSearch.Search(project, query));
        return new
        {
            query = query.Text,
            scope = ProjectSearch.ScopeName(query.Scope),
            count = result.Hits.Count,
            total = result.Total,
            truncated = result.Truncated,
            results = result.Hits.Select(hit => new
            {
                kind = hit.Kind,
                id = hit.Id,
                path = hit.Path,
                field = hit.Field,
                value = hit.Value,
                snippet = hit.Snippet,
            }).ToList(),
        };
    }

    private object ProjectReplacePreviewTool(JsonElement args)
    {
        var project = Current;
        var query = ReplaceQueryFrom(args);
        return ReplacePreviewPayload(query, SearchRun(() => ProjectSearch.Preview(project, query)));
    }

    private object ProjectReplaceApplyTool(JsonElement args)
    {
        var project = Current;
        var query = ReplaceQueryFrom(args);
        var preview = SearchRun(() => ProjectSearch.Preview(project, query));
        var result = SearchRun(() => ProjectSearch.Apply(project, preview));
        var changed = result.FieldCount > 0;
        if (changed) _undo.Push(project);
        return new
        {
            changed,
            applied = changed,
            itemCount = result.ItemCount,
            fieldCount = result.FieldCount,
            changedIds = result.ChangedIds,
            changes = preview.Changes.Select(Change).ToList(),
            undo = History(),
        };
    }

    private static object ReplacePreviewPayload(ReplaceQuery query, ReplacePreview preview) => new
    {
        query = query.Text,
        replacement = query.Replacement,
        scope = ProjectSearch.ScopeName(query.Scope),
        itemCount = preview.ItemCount,
        fieldCount = preview.FieldCount,
        changedIds = preview.ItemIds,
        changes = preview.Changes.Select(Change).ToList(),
    };

    private static object Change(ReplaceChange change) => new
    {
        kind = change.Kind,
        id = change.Id,
        path = change.Path,
        field = change.Field,
        before = change.Before,
        after = change.After,
    };

    private static SearchQuery SearchQueryFrom(JsonElement args) => new()
    {
        Text = Required(args, "query"),
        Scope = ScopeArgument(args),
        Regex = Bool(args, "regex"),
        CaseSensitive = Bool(args, "case_sensitive"),
        Limit = LimitArgument(args),
    };

    private static ReplaceQuery ReplaceQueryFrom(JsonElement args) => new()
    {
        Text = Required(args, "query"),
        Replacement = ReplacementArgument(args),
        Scope = ScopeArgument(args),
        Regex = Bool(args, "regex"),
        CaseSensitive = Bool(args, "case_sensitive"),
    };

    /// <summary>Replacement may be empty: clearing a matched text is a valid rename.</summary>
    private static string ReplacementArgument(JsonElement args)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty("replacement", out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw new ToolException("missing required argument 'replacement'");

    private static SearchScope ScopeArgument(JsonElement args)
    {
        var value = Str(args, "scope");
        return ProjectSearch.TryParseScope(value, out var scope)
            ? scope
            : throw new ToolException(
                $"unknown scope '{value}' (expected all, devices, group_addresses, topology, " +
                "communication_objects or buildings)");
    }

    private static int LimitArgument(JsonElement args)
    {
        var limit = Int(args, "limit") ?? ProjectSearch.DefaultLimit;
        return limit is >= 1 and <= 1000
            ? limit
            : throw new ToolException("'limit' must be between 1 and 1000");
    }

    /// <summary>Turns core search/replace argument failures into deterministic tool errors.</summary>
    private static T SearchRun<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ArgumentException ex)
        {
            var message = ex.Message;
            var parameter = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
            throw new ToolException(parameter > 0 ? message[..parameter] : message);
        }
        catch (InvalidOperationException ex)
        {
            throw new ToolException(ex.Message);
        }
    }
}
