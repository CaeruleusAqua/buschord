using Knx.Workbench;

namespace Knx;

internal sealed record ObjectChannelDefinition(string? Id, string? Name, string? DeclarationId = null, string? NativeChannelId = null);

/// <summary>Channels follow the same selected dynamic placements as communication-object activity.</summary>
internal static class ApplicationChannels
{
    internal static ObjectChannelDefinition? Read(DynamicEvaluation evaluation, DeviceInstance device, string reference, string? program,
        string? nativeChannelId = null)
    {
        string Local(string id) => program is not null && id.StartsWith(program + "_", StringComparison.Ordinal) ? id[(program.Length + 1)..] : id;
        if (nativeChannelId is not null) return ReadNative(device, nativeChannelId, program, evaluation.ResolvedNames);
        if (!evaluation.ObjectActivityComplete) return null;
        var full = !string.IsNullOrEmpty(program) && !reference.StartsWith(program + "_", StringComparison.Ordinal) ? program + "_" + reference : reference;
        if (!evaluation.ObjectChannels.TryGetValue(full, out var channel)) evaluation.ObjectChannels.TryGetValue(reference, out channel);
        if (channel is null) return new(null, null);
        var matches = All(device.Channels).Where(c => Local(c.RefId ?? c.Id) == Local(channel)).Take(2).ToArray();
        if (matches.Length > 1) return new(null, null);
        var name = matches.FirstOrDefault()?.Name;
        return new(matches.FirstOrDefault()?.Id ?? Local(channel),
            !string.IsNullOrEmpty(name) ? name : evaluation.ResolvedNames.GetValueOrDefault(channel) ?? Local(channel), Local(channel));
    }

    internal static ObjectChannelDefinition ReadNative(DeviceInstance device, string nativeChannelId, string? program = null,
        IReadOnlyDictionary<string, string>? names = null)
    {
        // Native pins reference instance IDs, never application RefIds or localized suffixes.
        var pinned = All(device.Channels).Where(c => c.Id == nativeChannelId).Take(2).ToArray();
        if (pinned.Length != 1) return new(null, null, NativeChannelId: nativeChannelId);
        var instance = pinned[0];
        var declared = instance.RefId is { } reference && program is not null && reference.StartsWith(program + "_", StringComparison.Ordinal)
            ? reference[(program.Length + 1)..] : instance.RefId;
        var fullDeclared = declared is not null && program is not null ? program + "_" + declared : declared;
        var name = !string.IsNullOrEmpty(instance.Name) ? instance.Name
            : fullDeclared is not null ? names?.GetValueOrDefault(fullDeclared) ?? names?.GetValueOrDefault(declared!) : null;
        return new(instance.Id, name, declared, nativeChannelId);
    }

    /// <summary>Reconciles loaded grouping facts without recalculating inherited text or requiring an application.</summary>
    internal static void Refresh(Project project)
    {
        foreach (var device in ProjectQuery.DeviceList(project))
            foreach (var instance in device.ComObjectRefs)
            {
                var key = new ObjectKey(device.Id, instance.Id);
                var metadata = project.Workbench.Objects.GetValueOrDefault(key.ToString());
                var cached = project.ObjectTextDefinitions.GetValueOrDefault(key)?.Channel;
                // A cached native record is valid only while its raw pin still exists. A removed
                // pin needs a newly evaluated derived placement, otherwise it is unassigned.
                var channel = instance.ChannelId is { } pin ? ReadNative(device, pin)
                    : cached?.NativeChannelId is null ? cached : null;
                if (channel is null && instance.ChannelId is null && metadata?.EffectiveChannelSource == ObjectChannelSource.Native)
                    channel = new(null, null);
                if (channel is null) continue;
                if (channel.Id is not null && cached is not null && cached.NativeChannelId == channel.NativeChannelId && cached.Id == channel.Id)
                    channel = channel with { Name = channel.Name ?? cached.Name, DeclarationId = cached.DeclarationId ?? channel.DeclarationId };
                if (metadata is null) project.Workbench.Objects[key.ToString()] = metadata = new();
                Apply(metadata, channel);
            }
    }

    internal static void Apply(ObjectMetadata metadata, ObjectChannelDefinition channel)
    {
        if (metadata.EffectiveChannelSource != ObjectChannelSource.Manual)
            metadata.SetEvaluatedChannel(channel.Id, channel.NativeChannelId is null ? ObjectChannelSource.Derived : ObjectChannelSource.Native);
        metadata.StructuralDeclaredChannelId = channel.Id;
        metadata.StructuralDeclaredChannelName = channel.Name;
    }

    private static IEnumerable<ChannelInstance> All(IEnumerable<ChannelInstance> channels)
    {
        foreach (var channel in channels)
        {
            yield return channel;
            foreach (var child in All(channel.Channels)) yield return child;
        }
    }
}
