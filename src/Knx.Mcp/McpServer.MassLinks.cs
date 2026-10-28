using System.Text.Json;
using System.Text.Json.Nodes;
using Knx.Workbench;

namespace Knx.Mcp;

public sealed partial class McpServer
{
    private sealed record MassReviewEntry(MassLinkReviewPlan Plan, object SessionIdentity, DateTimeOffset Expires);
    private readonly Dictionary<string, MassReviewEntry> _massLinkReviews = new(StringComparer.Ordinal);

    private static JsonObject MassLinkReviewSchema()
    {
        var schema = Schema(StrProp("review_token", "Existing review token to update or inspect; omit for a fresh review."),
            StrProp("device_address", "Optional device scope for a fresh review, e.g. 1.1.5."));
        ((JsonObject)schema["properties"]!)["targets"] = new JsonObject
        {
            ["type"] = "object", ["maxProperties"] = MassLinkReview.MaximumRows,
            ["description"] = "Review row id -> existing group id from targets; null clears a target. Never mutates the project.",
            ["additionalProperties"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
        };
        return schema;
    }

    private static JsonObject MassLinkApplySchema()
    {
        var schema = Schema(StrProp("review_token", "Bound token returned by mass_link_review.", true));
        ((JsonObject)schema["properties"]!)["row_ids"] = new JsonObject
        {
            ["type"] = "array", ["maxItems"] = MassLinkReview.MaximumRows, ["uniqueItems"] = true,
            ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "Exact ready row ids to apply. Omit to apply every ready row.",
        };
        return schema;
    }

    private object MassLinkReviewTool(JsonElement args)
    {
        var project = Current;
        foreach (var token in _massLinkReviews.Where(pair => pair.Value.Expires <= DateTimeOffset.UtcNow).Select(pair => pair.Key).ToArray()) _massLinkReviews.Remove(token);
        var reviewToken = OptionalString(args, "review_token");
        MassReviewEntry review;
        if (reviewToken is null)
        {
            var identity = new object();
            WorkbenchScope? scope = OptionalString(args, "device_address") is { } address
                ? new(WorkbenchScopeKind.Device, LinkDevice(project, address).Id, IncludeInactive: true) : null;
            review = new(MassLinkReview.Preview(project, identity, scope), identity, DateTimeOffset.UtcNow.AddMinutes(10));
            _massLinkReviews.Clear();
            reviewToken = Guid.NewGuid().ToString("N");
            _massLinkReviews.Add(reviewToken, review);
        }
        else
        {
            review = MassReview(reviewToken);
            if (OptionalString(args, "device_address") is not null) throw new ToolException("Choose the device scope when creating a fresh review.");
        }
        var changes = new List<KeyValuePair<string, string?>>();
        if (args.TryGetProperty("targets", out var targets))
        {
            if (targets.ValueKind != JsonValueKind.Object) throw new ToolException("'targets' must map row ids to group ids or null.");
            foreach (var pair in targets.EnumerateObject())
            {
                if (pair.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new ToolException("Review targets must be group id strings or null.");
                changes.Add(new(pair.Name, pair.Value.ValueKind == JsonValueKind.Null ? null : pair.Value.GetString()));
                if (changes.Count > MassLinkReview.MaximumRows) throw new ToolException("Too many review target changes.");
            }
        }
        try { MassLinkReview.SelectTargets(review.Plan, project, review.SessionIdentity, changes); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw new ToolException(error.Message); }
        return new
        {
            reviewToken, expires = review.Expires,
            counts = Enum.GetValues<MassLinkReviewState>().ToDictionary(state => state.ToString().ToLowerInvariant(), state => review.Plan.Rows.Count(row => row.State == state)),
            rows = review.Plan.Rows.Select(row => new
            {
                row.Id, row.Key, row.DeviceName, row.DeviceAddress, row.ObjectNumber, row.ObjectText, row.DatapointType,
                state = row.State.ToString().ToLowerInvariant(), row.CanApply, row.SelectedGroupId,
                validation = row.Validation is { } validation ? new { reason = validation.Reason.ToString(), validation.Message } : null,
                candidates = row.Candidates.Select(candidate => new
                {
                    candidate.GroupId, candidate.Score, candidate.Reason,
                    validation = new { reason = candidate.Validation.Reason.ToString(), candidate.Validation.Message },
                }),
            }),
            targets = review.Plan.Targets,
        };
    }

    private object MassLinkApplyTool(JsonElement args)
    {
        var token = Required(args, "review_token");
        var review = MassReview(token);
        string[]? ids = null;
        if (args.TryGetProperty("row_ids", out var selected))
        {
            if (selected.ValueKind != JsonValueKind.Array || selected.GetArrayLength() > MassLinkReview.MaximumRows)
                throw new ToolException("'row_ids' must be a bounded array of exact review row ids.");
            if (selected.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(item.GetString())))
                throw new ToolException("'row_ids' must contain nonempty row id strings.");
            ids = selected.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        MutationResult result;
        try { result = MassLinkReview.Apply(review.Plan, Current, review.SessionIdentity, _undo, ids); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw new ToolException(error.Message); }
        if (!result.Ok) throw new ToolException(string.Join("; ", result.Errors.Select(error => error.Code + ": " + error.Reason)));
        _massLinkReviews.Remove(token);
        return new { applied = result.Changed, compatibilityVerified = result.Changed > 0, warnings = result.Warnings, undo = History() };
    }

    private MassReviewEntry MassReview(string token) => _massLinkReviews.TryGetValue(token, out var review) && review.Expires > DateTimeOffset.UtcNow
        ? review : throw new ToolException("Create a fresh mass_link_review first; the token is missing or expired.");
}
