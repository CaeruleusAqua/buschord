namespace Knx.Bus.Security;

public sealed partial class DataSecureCounterStore
{
    /// <summary>Validates the complete existing ordinary state before any transport I/O; never allocates or creates counters.</summary>
    public Task ValidateReadyForGroupsAsync(IReadOnlyCollection<IndividualAddress> permittedSenders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(permittedSenders);
        var required = permittedSenders.Select(sender => SenderIndex(sender)).ToHashSet();
        return TransactionAsync(async (file, token) =>
        {
            await RequireReadyAsync(file, token).ConfigureAwait(false);
            var local = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
            if (local.Value == KnxDataSecureCodec.MaximumSequence) throw Failure(DataSecureCounterFailure.Exhausted, "The ordinary send highwater is exhausted.");
            for (var index = 2; index < _manifest.Senders.Length + 2; index++)
            {
                var page = await ReadCounterAsync(file, index, token).ConfigureAwait(false);
                if (_manifest.Senders[index - 2] == _identity.LocalAddress.Raw && page.Value > local.Value)
                    throw Failure(DataSecureCounterFailure.SynchronizationRequired, "The local receive floor exceeds the ordinary send highwater.");
                if (required.Contains(index) && page.Value == KnxDataSecureCodec.MaximumSequence)
                    throw Failure(DataSecureCounterFailure.Exhausted, "A selected receive floor is exhausted.");
            }
            return true;
        }, ct);
    }
}
