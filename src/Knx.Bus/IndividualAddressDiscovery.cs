using System.Diagnostics;
using Knx;

namespace Knx.Bus;

public enum AddressObservationState { NoResponse, ResponsesObserved, MultipleAddresses, MalformedResponse, ResponseLimitReached }

/// <summary>Bounded wire observations. Response count and distinct addresses do not establish physical uniqueness or address availability.</summary>
public sealed class IndividualAddressObservation
{
    public AddressObservationState State { get; }
    public IReadOnlyList<IndividualAddress> Addresses { get; }
    public int ValidResponses { get; }
    public int MalformedResponses { get; }
    public string? QueriedSerial { get; }
    public TimeSpan Elapsed { get; }
    internal IndividualAddressObservation(AddressObservationState state, IndividualAddress[] addresses, int valid, int malformed,
        string? serial, TimeSpan elapsed)
    { State = state; Addresses = Array.AsReadOnly(addresses); ValidResponses = valid; MalformedResponses = malformed; QueriedSerial = serial; Elapsed = elapsed; }
}

/// <summary>Connectionless, read-only address discovery on an already owned connection. It performs no assignment, restart or state initialization.</summary>
public static class IndividualAddressDiscovery
{
    public const int MaximumResponses = 256;

    public static Task<IndividualAddressObservation> ReadProgrammingModeAsync(IBusDiagnosticSession session, IndividualAddress clientAddress,
        TimeSpan observationWindow, CancellationToken ct = default)
        => ReadAsync(session, clientAddress, observationWindow, null, ct);

    public static Task<IndividualAddressObservation> ReadSerialAsync(IBusDiagnosticSession session, IndividualAddress clientAddress,
        ReadOnlyMemory<byte> serial, TimeSpan observationWindow, CancellationToken ct = default)
    {
        // Validate and own the selector before any subscription or send.
        var request = Management.IndividualAddressSerialReadTelegram(serial.Span);
        return ReadAsync(session, clientAddress, observationWindow, request.Data, ct);
    }

    private static async Task<IndividualAddressObservation> ReadAsync(IBusDiagnosticSession session, IndividualAddress clientAddress,
        TimeSpan window, byte[]? serial, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (clientAddress.Raw == 0 || clientAddress.Device is 0 or 255) throw new ArgumentOutOfRangeException(nameof(clientAddress));
        if (window < TimeSpan.FromMilliseconds(20) || window > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(window));
        ct.ThrowIfCancellationRequested();
        var gate = new object(); var addresses = new HashSet<ushort>();
        var valid = 0; var malformed = 0; var observed = 0; var overflow = false; var closed = false;
        var started = Stopwatch.GetTimestamp();
        void Received(BusTelegram received)
        {
            var telegram = received.Telegram;
            if (telegram is null || telegram.Apci != (serial is null ? Apci.IndividualAddressResponse : Apci.IndividualAddressSerialResponse)) return;
            if (telegram.IsGroupAddress ? telegram.Destination.Raw != 0 : telegram.Destination != clientAddress) return;
            var data = telegram.Data;
            if (serial is not null && data is { Length: >= 6 } && !data.AsSpan(0, 6).SequenceEqual(serial)) return;
            lock (gate)
            {
                if (closed || overflow) return;
                if (++observed > MaximumResponses) { overflow = true; return; }
                var shape = serial is null
                    ? data is { Length: 0 } && telegram.DataBits is null || data is [0] && telegram.DataBits == 6
                    : data is { Length: 8 } && telegram.DataBits is null && data[6] == 0 && data[7] == 0;
                if (!shape || telegram.Source.Raw == 0 || telegram.Source == clientAddress
                    || telegram.Source.Device == 255 && telegram.Source.Raw != ushort.MaxValue)
                { malformed++; return; }
                valid++; addresses.Add(telegram.Source.Raw);
            }
        }
        session.TelegramReceived += Received;
        try
        {
            var request = serial is null ? Management.IndividualAddressReadTelegram() : Management.IndividualAddressSerialReadTelegram(serial);
            await session.SendAsync(request, ct).ConfigureAwait(false);
            await Task.Delay(window, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) closed = true;
            session.TelegramReceived -= Received;
        }
        lock (gate)
        {
            var state = overflow ? AddressObservationState.ResponseLimitReached : malformed > 0 ? AddressObservationState.MalformedResponse
                : addresses.Count > 1 ? AddressObservationState.MultipleAddresses : valid > 0 ? AddressObservationState.ResponsesObserved : AddressObservationState.NoResponse;
            return new(state, addresses.Order().Select(value => new IndividualAddress(value)).ToArray(), valid, malformed,
                serial is null ? null : Convert.ToHexString(serial), Stopwatch.GetElapsedTime(started));
        }
    }
}
