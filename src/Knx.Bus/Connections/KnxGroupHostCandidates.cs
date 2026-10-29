using System.Text.Json.Serialization;

namespace Knx.Bus.Connections;

/// <summary>Unverified public configuration metadata. It never carries a counter/key/password capability.</summary>
public sealed class KnxGroupHostCandidates
{
    [JsonIgnore] public string Path { get; }
    public string SourceFingerprint { get; }
    public IReadOnlyList<KnxGroupHostCandidate> Candidates { get; }
    internal KnxGroupHostCandidates(string path, string sourceFingerprint, IReadOnlyList<KnxGroupHostCandidate> candidates)
    { Path = path; SourceFingerprint = sourceFingerprint; Candidates = candidates; }
}
public sealed class KnxGroupHostCandidate
{
    public string Id { get; }
    [JsonIgnore] public KnxConnectionProfile ExpectedConnection { get; }
    [JsonIgnore] public string StatePath { get; }
    public string InstallationScope { get; }
    public string OrdinaryKeyEpoch { get; }
    public bool RequiresCurrentUnanchoredConfirmation { get; }
    public IReadOnlyList<KnxSecureGroupSelection> Groups { get; }
    public IReadOnlyList<GroupAddress> PlainGroups { get; }
    public IReadOnlyList<IndividualAddress> PlainIndividualPeers { get; }
    internal KnxGroupHostCandidate(string id, KnxConnectionProfile connection, string statePath, string installation, string epoch, bool currentUnanchored,
        KnxSecureGroupSelection[] groups, GroupAddress[] plainGroups, IndividualAddress[] plainPeers)
    {
        Id = id; ExpectedConnection = connection; StatePath = statePath; InstallationScope = installation; OrdinaryKeyEpoch = epoch;
        RequiresCurrentUnanchoredConfirmation = currentUnanchored; Groups = Array.AsReadOnly(groups);
        PlainGroups = Array.AsReadOnly(plainGroups); PlainIndividualPeers = Array.AsReadOnly(plainPeers);
    }
    public override string ToString() => Id;
}
