using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Knx;

internal enum NativeImageSpace { StandardMemory, RelativeMemory, PropertyData, PropertyCount }
internal sealed record NativeImageTarget(NativeImageSpace Space, byte? Lsm = null, PropertySourceSelector? Selector = null)
{
    internal bool IsValid => Space switch
    {
        NativeImageSpace.StandardMemory => Lsm is null && Selector is null,
        NativeImageSpace.RelativeMemory => Lsm is not null && Selector is null,
        NativeImageSpace.PropertyData or NativeImageSpace.PropertyCount => Lsm is null && (Selector switch
        {
            { Kind: PropertySourceKind.SystemProperty, NativeObjectIndex: not null, ApplicationObjectIndex: null,
                ObjectType: null, Occurrence: null } => true,
            { Kind: PropertySourceKind.AppProperty, NativeObjectIndex: null, ApplicationObjectIndex: not null,
                ObjectType: null, Occurrence: null } => true,
            { Kind: PropertySourceKind.ByType, NativeObjectIndex: null, ApplicationObjectIndex: null,
                ObjectType: not null, Occurrence: not null } => true,
            _ => false,
        }),
        _ => false,
    };
}
internal sealed record NativeImageRange(NativeImageTarget Target, uint ByteOffset, int Length)
{
    internal bool IsValid => Target.IsValid && Length > 0 && Length <= 1024 * 1024
        && (ulong)ByteOffset + (uint)Length <= (ulong)uint.MaxValue + 1;
    internal bool Covers(uint offset, int length) => IsValid && length >= 0 && offset >= ByteOffset
        && (ulong)offset + (uint)length <= (ulong)ByteOffset + (uint)Length;
}

internal enum NativeGeneralIssueCode
{
    InvalidRequest, SourceUnavailable, InvalidDeclaration, UnsupportedApplication,
    UnsupportedSource, UnsupportedType, InvalidValue, InvalidPolicy,
    UnsupportedMapping, AmbiguousMapping, UnsupportedScenario, UnsupportedControl,
    AmbiguousOwnership, MissingAllocation, UncoveredOwner, NoncommutativeValidity,
    ContextMismatch, ProcedureUnavailable, TableUnavailable, InputsChanged,
    ExternalConfigurationUnaccounted, UnaccountedBinaryData, UnsupportedNativeCarrier,
    LimitExceeded,
}
internal sealed record NativeGeneralIssue(NativeGeneralIssueCode Code,
    string? RefId = null, string? ResourceId = null, int? VariantOrdinal = null,
    ParameterPatchIssue? ParameterIssue = null, GeneratedTableIssue? ActivationIssue = null,
    LoadProcedureIssue? ProcedureIssue = null);
internal sealed class NativeGeneralRejected(NativeGeneralIssue issue) : Exception
{
    internal NativeGeneralIssue Issue { get; } = issue;
}
internal static class NativeGeneralHash
{
    internal static string Of<T>(T value)
    {
        using var digest = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write);
        JsonSerializer.Serialize(stream, value);
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(digest.Hash!);
    }
    internal static string Document(XNode document)
    {
        // Bound before serializing: this helper also accepts an already captured node.
        long characters = 0; var count = 0;
        var pending = new Stack<(XNode Node, int Depth)>(); pending.Push((document, 0));
        while (pending.TryPop(out var item))
        {
            if (++count > 1000000 || item.Depth > 128) Fail(NativeGeneralIssueCode.LimitExceeded);
            if (item.Node is XContainer container)
                foreach (var child in container.Nodes())
                {
                    if (pending.Count >= 1000000) Fail(NativeGeneralIssueCode.LimitExceeded);
                    pending.Push((child, item.Depth + 1));
                }
            characters += item.Node switch
            {
                XElement element => element.Name.ToString().Length
                    + element.Attributes().Sum(a => (long)a.Name.ToString().Length + a.Value.Length),
                XText text => text.Value.Length,
                XComment comment => comment.Value.Length,
                XProcessingInstruction instruction => instruction.Target.Length + instruction.Data.Length,
                _ => 0,
            };
            if (characters > 128 * 1024 * 1024) Fail(NativeGeneralIssueCode.LimitExceeded);
        }
        using var digest = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, digest, CryptoStreamMode.Write);
        using (var writer = XmlWriter.Create(stream, new() { Encoding = new UTF8Encoding(false), CloseOutput = false, Indent = false }))
            document.WriteTo(writer);
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(digest.Hash!);
    }
    [DoesNotReturn]
    internal static void Fail(NativeGeneralIssueCode code, string? reference = null, string? resource = null)
        => throw new NativeGeneralRejected(new(code, reference, resource));
}

internal sealed record NativeRootOwner(string ParameterId, int OriginalDeclarationOrdinal,
    string DeclarationHash, NativeImageTarget Target, uint ByteOffset, byte BitOffset,
    uint BitLength, bool Referenced, PropertySourceSelector? RawPropertySelector,
    PropertyPdtLayout? PdtLayout, string OwnerKey, string MappingHash);
internal sealed record NativeRootReference(string RefId, string ParameterId,
    string ParameterTypeId, int SourceOrdinal, NativeRootOwner? Owner,
    bool Always, bool CustomerAdjustable, string DeclarationHash);
internal enum NativeRootTaskDisposition { NotScheduled, Scheduled }
internal enum NativeRootCustomerPolicy { IncludeAll, ExcludeAdjustable }
internal enum NativeRootValidityEffect { None, FieldBits, WholeBytes }
internal sealed record NativeRootWriteClaim(string RefId, string OwnerKey,
    NativeImageTarget Target, uint ByteOffset, byte BitOffset, uint BitLength,
    bool WritesData, uint? EncodedWord, NativeRootValidityEffect ValiditySet,
    NativeRootValidityEffect ValidityClear);
internal sealed record NativeRootWriterRecord(string RefId, string ParameterId,
    string ParameterTypeId, RootParameterDisposition Disposition, bool Active,
    int NormalReferenceCount, bool LegacyPatchAlways, bool CustomerAdjustable,
    RootParameterValueOrigin CurrentValueOrigin, string CurrentValueHash,
    string? RecordValueHash, bool NativeRecordEmitted, bool Allocates, bool WritesData,
    bool CustomerExcluded, NativeRootWriteClaim? Claim);

internal sealed class NativeRootSourcePolicy
{
    internal ParameterPatchByteOrder ByteOrder { get; }
    internal RootParameterDownloadBehavior DownloadBehavior { get; }
    internal RootParameterMaskPolicy MaskPolicy { get; }
    internal string? MinEtsVersion { get; }
    internal uint PackedMinEtsVersion { get; }
    internal bool OptionsDeclared { get; }
    internal string? DeclaredDownloadBehavior { get; }
    internal string? DeclaredByteOrder { get; }
    internal string Fingerprint { get; }
    internal NativeRootSourcePolicy(ParameterPatchByteOrder order, RootParameterDownloadBehavior behavior,
        RootParameterMaskPolicy mask, string? version, uint packedVersion, bool options,
        string? declaredBehavior, string? declaredOrder)
    {
        ByteOrder = order; DownloadBehavior = behavior; MaskPolicy = mask; MinEtsVersion = version;
        PackedMinEtsVersion = packedVersion; OptionsDeclared = options;
        DeclaredDownloadBehavior = declaredBehavior; DeclaredByteOrder = declaredOrder;
        Fingerprint = NativeGeneralHash.Of(new { version = 1, ByteOrder, DownloadBehavior, MaskPolicy,
            MinEtsVersion, PackedMinEtsVersion, OptionsDeclared, DeclaredDownloadBehavior, DeclaredByteOrder });
    }
}
