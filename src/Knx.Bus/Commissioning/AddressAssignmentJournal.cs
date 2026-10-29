using System.Text.Json;
using System.Text.Json.Serialization;
using Knx.Bus.Connections;

namespace Knx.Bus.Commissioning;

/// <summary>Bounded private local intent/evidence. Checksums detect accidental change, not a malicious same-user actor or filesystem rollback.
/// Cold loading is always read-only reconciliation, even when the attempt marker is absent after a crash.</summary>
internal static class AddressAssignmentJournal
{
    private sealed record Envelope(string Sha256, AddressAssignmentIntent Intent);
    private sealed record Claim(string ReviewFingerprint, Guid Operation);
    private static readonly JsonSerializerOptions Options = new() { MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static string ValidateDirectory(string directory)
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory)) throw Invalid();
        directory = Path.GetFullPath(directory); SecureLocalFiles.NoLinks(directory);
        for (var parent = directory; parent is not null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(Path.Combine(parent, ".git")) || File.Exists(Path.Combine(parent, ".git"))) throw Invalid();
        return directory;
    }
    internal static string Create(string root, AddressAssignmentIntent intent)
    {
        var directory = Path.Combine(ValidateDirectory(root), "address-" + intent.Operation.ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateDirectory(directory);
        var path = Path.Combine(directory, "intent.json");
        WriteNew(path, new Envelope(AddressAssignmentIntent.Hash(intent), intent)); return path;
    }
    private static void WriteNew(string path, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 32768) throw Invalid();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(path, options); file.Write(bytes); file.Flush(true);
    }
    internal static void ClaimAttempt(string path, AddressAssignmentIntent intent)
    {
        ValidatePath(path);
        // CreateNew is the claim, rather than File.Move(false). Neither recovery nor another caller can reuse this intent to write.
        try { WriteNew(Path.Combine(Path.GetDirectoryName(path)!, "attempt.json"), new Claim(intent.Review.Fingerprint, intent.Operation)); }
        catch (IOException) { throw new AddressAssignmentException(AddressAssignmentFailure.AlreadyAttempted); }
    }
    internal static void Record(string path, AddressAssignmentResult result)
    {
        ValidatePath(path); var events = Path.Combine(Path.GetDirectoryName(path)!, "outcome.jsonl");
        if (File.Exists(events)) SecureLocalFiles.NoLinks(events);
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var file = new FileStream(events, options);
        if (file.Length > 65536) throw Invalid();
        file.Write(JsonSerializer.SerializeToUtf8Bytes(result)); file.WriteByte(10); file.Flush(true);
    }
    internal static async Task<AddressAssignmentIntent> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            ValidatePath(path);
            var bytes = await SecureLocalFiles.ReadPrivateAsync(path, 32768, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 }); Unique(document.RootElement);
            var envelope = document.RootElement.Deserialize<Envelope>(Options) ?? throw Invalid();
            var intent = envelope.Intent ?? throw Invalid();
            if (intent.Version != 1 || envelope.Sha256 != AddressAssignmentIntent.Hash(intent) || intent.Operation == Guid.Empty) throw Invalid();
            var review = intent.Review ?? throw Invalid(); var profile = intent.Profile();
            if (!Enum.IsDefined(review.Selection) || !IndividualAddress.TryParse(review.OldAddress, out var old) || old.Raw == 0
                || !IndividualAddress.TryParse(review.NewAddress, out var target) || target.Raw is 0 or ushort.MaxValue || target == profile.RequestedAddress || old == target
                || intent.ObservationMilliseconds is < 20 or > 3000 || review.SelectorResponses != 1
                || review.Occupancy != AddressOccupancyState.NoResponseReservationRequired || review.Identity is { IsValid: false }
                || ReviewHash(review) != review.Fingerprint) throw Invalid();
            _ = new AddressAssignmentRequest(profile, target, review.Selection, review.Reservation,
                review.Selection == AddressSelectionKind.ExactSerial ? review.Serial : null, review.IsolatedSetup, confirmPlainDevice: true,
                observationWindow: TimeSpan.FromMilliseconds(intent.ObservationMilliseconds), targetRole: review.TargetRole, confirmCouplerHardware: review.CouplerHardwareConfirmed,
                couplerHardwareReference: review.CouplerHardwareReference);
            if (review.Selection == AddressSelectionKind.ExactSerial && review.Identity is not null && review.Identity.Serial != review.Serial) throw Invalid();
            return intent;
        }
        catch (OperationCanceledException) { throw; }
        catch (AddressAssignmentException) { throw; }
        catch { throw Invalid(); }
    }
    internal static string ReviewHash(AddressAssignmentReview review) => AddressAssignmentIntent.Hash(review with { Fingerprint = "" });
    internal static bool HasClaim(string path, AddressAssignmentIntent intent)
    {
        var claimPath = Path.Combine(Path.GetDirectoryName(path)!, "attempt.json");
        if (!File.Exists(claimPath)) return false;
        SecureLocalFiles.NoLinks(claimPath);
        using var file = new FileStream(claimPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > 2048) throw Invalid();
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes); if (file.ReadByte() != -1) throw Invalid();
        using var document = JsonDocument.Parse(bytes); Unique(document.RootElement);
        var claim = document.RootElement.Deserialize<Claim>(Options);
        if (claim is null || claim.Operation != intent.Operation || claim.ReviewFingerprint != intent.Review.Fingerprint) throw Invalid();
        return true;
    }
    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path) != "intent.json") throw Invalid();
        ValidateDirectory(Path.GetDirectoryName(path)!); SecureLocalFiles.NoLinks(path);
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 4096) throw Invalid();
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw Invalid(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Unique(child);
    }
    private static AddressAssignmentException Invalid() => new(AddressAssignmentFailure.InvalidJournal);
}
