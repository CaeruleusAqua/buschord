namespace Knx.Bus;

/// <summary>Explicit initializer invoked after a tunnel has its actual assigned address and before publishing management access.</summary>
public interface ICemiSecurityInitializer
{
    bool IsInitialized { get; }
    Task InitializeAsync(IndividualAddress assignedAddress,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> boundedControlSend, CancellationToken ct = default);
}
