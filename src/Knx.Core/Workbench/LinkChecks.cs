namespace Knx.Workbench;

/// <summary>Exact identities to check; no model references or retained validation context.</summary>
public readonly record struct LinkCheckRequest(ObjectKey Key, string GroupId);

/// <summary>Detached facts from one check. A mutation must validate the current project again.</summary>
public sealed record LinkCheckResult(ObjectKey Key, string GroupId, LinkValidation Validation, bool AlreadyLinked);
