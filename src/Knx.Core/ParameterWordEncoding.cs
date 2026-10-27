namespace Knx;

/// <summary>Encoded word and profile facts; source ownership is checked separately by the caller.</summary>
internal readonly record struct ParameterWordEncoding(int Bits, uint Word, bool Single, bool Packed, bool Dense,
    ParameterPatchIssue? Issue = null);
