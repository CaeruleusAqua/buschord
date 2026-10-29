using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Knx.Bus.Connections;

namespace Knx.Bus.Commissioning;

public enum AddressSelectionKind { ExactSerial, IsolatedProgrammingMode }
public enum AddressDeviceRole { EndDevice, Coupler }
public enum AddressOccupancyState { NoResponseReservationRequired, ResponseObserved, MalformedResponse }
public enum AddressAssignmentState { Idle, Reviewing, Reviewed, Applying, Reconciling, Verified, VerifiedUnrecorded, OriginalObserved, Unresolved, Cancelled, Failed }
public enum AddressAssignmentFailure { InvalidSelection, UnsupportedSecureProvisioning, PhysicalBindingRequired, AmbiguousResponse, TargetResponded, ReservationRequired, AlreadyAssigned, StaleReview, InvalidJournal, AlreadyAttempted, Occupied, Disposed, TransportFailed, RecoveryRequired }
public sealed class AddressAssignmentException(AddressAssignmentFailure failure) : IOException("Individual address operation refused: " + failure)
{ public AddressAssignmentFailure Failure { get; } = failure; }

/// <summary>Physical continuity is exact serial evidence, or an explicitly named and continuously isolated one-device setup. Response counts never prove isolation.</summary>
public sealed class AddressAssignmentRequest
{
    [JsonIgnore] public KnxConnectionProfile Connection { get; }
    public AddressSelectionKind Selection { get; }
    public string? Serial { get; }
    public string? IsolatedSetup { get; }
    public IndividualAddress NewAddress { get; }
    public string Reservation { get; }
    public TimeSpan ObservationWindow { get; }
    public AddressDeviceRole TargetRole { get; }
    public bool CouplerHardwareConfirmed { get; }
    public string? CouplerHardwareReference { get; }
    public AddressAssignmentRequest(KnxConnectionProfile connection, IndividualAddress newAddress, AddressSelectionKind selection,
        string reservation, string? exactSerial = null, string? isolatedSetup = null, bool confirmPlainDevice = false,
        bool requiresSecureProvisioning = false, TimeSpan? observationWindow = null, AddressDeviceRole targetRole = AddressDeviceRole.EndDevice, bool confirmCouplerHardware = false,
        string? couplerHardwareReference = null)
    {
        if (connection is null || connection.Kind != KnxTransportKind.PlainUdp || connection.GroupSecurity is not null || requiresSecureProvisioning)
            throw new AddressAssignmentException(AddressAssignmentFailure.UnsupportedSecureProvisioning);
        if (!confirmPlainDevice) throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
        if (!Enum.IsDefined(selection) || !Enum.IsDefined(targetRole) || newAddress.Raw is 0 or ushort.MaxValue || newAddress == connection.RequestedAddress)
            throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        if (targetRole == AddressDeviceRole.Coupler && (!confirmCouplerHardware || string.IsNullOrWhiteSpace(couplerHardwareReference) || couplerHardwareReference.Length > 1024)
            || newAddress.Device == 0 && (targetRole != AddressDeviceRole.Coupler || newAddress.Area == 0))
            throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
        if (targetRole == AddressDeviceRole.EndDevice && (confirmCouplerHardware || couplerHardwareReference is not null)) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        if (string.IsNullOrWhiteSpace(reservation) || reservation.Length > 1024) throw new AddressAssignmentException(AddressAssignmentFailure.ReservationRequired);
        if (selection == AddressSelectionKind.ExactSerial)
        {
            if (exactSerial is not { Length: 12 } || !exactSerial.All(char.IsAsciiHexDigit) || exactSerial == "000000000000" || exactSerial.Equals("FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase))
                throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
            var bytes = Convert.FromHexString(exactSerial); _ = Management.IndividualAddressSerialReadTelegram(bytes); Serial = Convert.ToHexString(bytes);
            if (isolatedSetup is not null) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        }
        else
        {
            if (exactSerial is not null || string.IsNullOrWhiteSpace(isolatedSetup) || isolatedSetup.Length > 1024)
                throw new AddressAssignmentException(AddressAssignmentFailure.PhysicalBindingRequired);
            IsolatedSetup = isolatedSetup;
        }
        var window = observationWindow ?? TimeSpan.FromMilliseconds(750);
        if (window < TimeSpan.FromMilliseconds(20) || window > TimeSpan.FromSeconds(3)) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidSelection);
        Connection = connection; Selection = selection; NewAddress = newAddress; Reservation = reservation; ObservationWindow = window;
        TargetRole = targetRole; CouplerHardwareConfirmed = confirmCouplerHardware;
        CouplerHardwareReference = couplerHardwareReference;
    }
}

public sealed record AddressPhysicalIdentity(ushort MaskVersion, string Manufacturer, string Serial)
{
    internal bool IsValid => Manufacturer is { Length: 4 } && Manufacturer.All(char.IsAsciiHexDigit)
        && Serial is { Length: 12 } && Serial.All(char.IsAsciiHexDigit) && Serial != "000000000000" && Serial != "FFFFFFFFFFFF";
}
public sealed record AddressOccupancyObservation(AddressOccupancyState State, int Responses);
public sealed record AddressAssignmentReview(string Fingerprint, string InterfaceId, string ClientAddress, string OldAddress, string NewAddress,
    AddressSelectionKind Selection, string? Serial, string? IsolatedSetup, string Reservation, AddressPhysicalIdentity? Identity,
    int SelectorResponses, AddressOccupancyState Occupancy, DateTimeOffset ReviewedAt)
{
    public AddressDeviceRole TargetRole { get; init; }
    public bool CouplerHardwareConfirmed { get; init; }
    public string? CouplerHardwareReference { get; init; }
}
public sealed record AddressAssignmentResult(AddressAssignmentState State, string IntentPath, string ReviewFingerprint,
    string OldAddress, string NewAddress, AddressPhysicalIdentity? Identity, bool WriteAttemptClaimed, bool WriteCallStarted)
{
    public bool AddressVerified => State == AddressAssignmentState.Verified;
    public bool RequiresReconciliation => State == AddressAssignmentState.Unresolved;
}

/// <summary>One owned gateway operation. Every read/write opens and joins a fresh bound tunnel; writing is a single no-retry application request.</summary>
public interface IAddressAssignmentOperation : IAsyncDisposable
{
    KnxConnectionProfile Profile { get; }
    Task<IndividualAddressObservation> DiscoverAsync(AddressSelectionKind kind, string? serial, TimeSpan window, CancellationToken ct);
    Task<AddressOccupancyObservation> ProbeAsync(IndividualAddress address, TimeSpan window, CancellationToken ct);
    Task<AddressPhysicalIdentity> ReadIdentityAsync(IndividualAddress address, CancellationToken ct);
    Task WriteOnceAsync(AddressSelectionKind kind, string? serial, IndividualAddress address, CancellationToken ct);
}

internal sealed record AddressAssignmentIntent(int Version, Guid Operation, string Gateway, int Port, string ClientAddress, string? LocalAddress,
    AddressAssignmentReview Review, int ObservationMilliseconds)
{
    internal KnxConnectionProfile Profile()
    {
        if (Version != 1 || !IPAddress.TryParse(Gateway, out var gateway) || !IndividualAddress.TryParse(ClientAddress, out var own)
            || Review.InterfaceId != $"knx-ip:{gateway}:{Port}" || Review.ClientAddress != own.ToString()) throw new AddressAssignmentException(AddressAssignmentFailure.InvalidJournal);
        return KnxConnectionProfile.Plain(new(gateway, Port), own, LocalAddress is null ? null : IPAddress.Parse(LocalAddress));
    }
    internal static string Hash(object value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
