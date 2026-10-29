namespace Knx.Bus.Security;

public sealed partial class DataSecureCounterStore
{
    /// <summary>Validates all existing pages without reserving a nonce, creating state or clearing a restore gate.</summary>
    public Task ValidateReadyForPeerAsync(IndividualAddress peer, CancellationToken ct = default)
    {
        if (peer == _identity.LocalAddress) throw Failure(DataSecureCounterFailure.ContextMismatch, "The peer must differ from the local sender.");
        var peerIndex = SenderIndex(peer);
        return TransactionAsync(async (file, token) =>
        {
            await RequireReadyAsync(file, token).ConfigureAwait(false);
            var local = await ReadCounterAsync(file, 1, token).ConfigureAwait(false);
            if (local.Value == KnxDataSecureCodec.MaximumSequence) throw Failure(DataSecureCounterFailure.Exhausted, "The local send counter is exhausted.");
            for (var index = 2; index < _manifest.Senders.Length + 2; index++)
            {
                var page = await ReadCounterAsync(file, index, token).ConfigureAwait(false);
                if (_manifest.Senders[index - 2] == _identity.LocalAddress.Raw && page.Value > local.Value)
                    throw Failure(DataSecureCounterFailure.SynchronizationRequired, "The local receive floor exceeds the retained send highwater.");
                if (index == peerIndex && page.Value == KnxDataSecureCodec.MaximumSequence)
                    throw Failure(DataSecureCounterFailure.Exhausted, "The peer receive floor is exhausted.");
            }
            return true;
        }, ct);
    }
}
