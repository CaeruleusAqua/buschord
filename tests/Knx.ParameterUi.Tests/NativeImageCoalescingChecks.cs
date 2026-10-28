using System.Reflection;
using Knx;

internal static class NativeImageCoalescingChecks
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static Type Core(string name) => typeof(Project).Assembly.GetType("Knx." + name, true)!;
    private static object New(string name, params object?[] arguments)
        => Activator.CreateInstance(Core(name), Any, null, arguments, null)!;
    private static T Get<T>(object value, string property) => (T)value.GetType().GetProperty(property, Any)!.GetValue(value)!;
    private static object Target(string space = "StandardMemory", byte? lsm = null, PropertySourceSelector? property = null)
        => New("NativeImageTarget", Enum.Parse(Core("NativeImageSpace"), space), lsm, property);
    private static object Range(uint offset, int length, object? target = null)
        => New("NativeImageRange", target ?? Target(), offset, length);
    private static object Segment(uint offset, byte[] data, byte[]? mask, object? target = null, int? length = null)
        => New("NativeImageMutableSegment", Range(offset, length ?? data.Length, target), data, mask);
    private static object Ledger(long reserved = 0)
    {
        var result = New("NativeImagePayloadLedger");
        result.GetType().GetMethod("Reserve", Any)!.Invoke(result, [reserved]); return result;
    }
    private static object Coalesce(object? earlier, object? incoming, object? ledger)
        => Core("NativeImageCoalescer").GetMethod("Coalesce", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [earlier, incoming, ledger])!;
    private static string? Mask(object segment) => Get<byte[]?>(segment, "Mask") is { } mask ? Convert.ToHexString(mask) : null;
    private static string Data(object segment) => Convert.ToHexString(Get<byte[]>(segment, "Data"));
    private sealed record Vector(string Name, uint EarlierOffset, string EarlierData, string? EarlierMask,
        uint IncomingOffset, string IncomingData, string? IncomingMask, uint ResultOffset, string ResultData, string? ResultMask);
    private static Vector[] Vectors() =>
    [
        new("same-start region and code prefix", 100,"00000000","00000000",100,"A5000000","F0000000",100,"A0000000","F0000000"),
        new("whole512 region and code",100,new string('0',1024),new string('0',1024),100,"A5"+new string('0',1022),"F0"+new string('0',1022),100,"A0"+new string('0',1022),"F0"+new string('0',1022)),
        new("pointer4 and shorter factory2",0,"00000000","00000000",0,"DEAD","0000",0,"DEAD0000","00000000"),
        new("pointer4 and longer factory8",0,"00000000","00000000",0,"DEADBEEF10203040","0000000000000000",0,"DEADBEEF10203040","0000000000000000"),
        new("shifted factory extends pointer",0,"00000000","00000000",2,"DEADBEEF10203040","0000000000000000",0,"0000DEADBEEF10203040","00000000000000000000"),
        new("zero masks preserve raw OR",0,"F00F","0000",0,"0FF0","0000",0,"FFFF","0000"),
        new("conflicting valid bits follow OR",0,"F00F","FFFF",0,"0FF0","FFFF",0,"FFFF","FFFF"),
        new("disjoint partial masks",0,"A57C","F00F",0,"3AC3","0FF0",0,"AACC","FFFF"),
        new("zero-mask overlap and raw tail",0,"ABCD","8000",1,"EF12","000F",0,"ABEF12","80000F"),
        new("explicit containment retains exclusive bytes",0,"10203040","F0000FF0",1,"A5C3","F00F",0,"10A00340","F0F00FF0"),
        new("right-null containment overwrites and validates",0,"10203040","01020304",1,"A5C3",null,0,"10A5C340","01FFFF04"),
        new("left-null containment ignores explicit right",0,"10203040",null,1,"A5C3","F00F",0,"10203040",null),
        new("both-null containment copies raw right",0,"10203040",null,1,"A5C3",null,0,"10A5C340",null),
        new("explicit extension preserves raw partial tail",0,"10203040","F0000FF0",2,"A5C37E99","F00FF000",0,"1020A0437E99","F000FFFFF000"),
        new("right-null extension validates new bytes",0,"10203040","01020304",2,"A5C37E99",null,0,"1020A5C37E99","0102FFFFFFFF"),
        new("left-null extension creates valid old prefix",0,"10203040",null,2,"A5C37E99","F00FF000",0,"1020B0437E99","FFFFFFFFF000"),
        new("both-null extension remains null",0,"10203040",null,2,"A5C37E99",null,0,"1020A5C37E99",null),
        new("left-null extension with unknown right tail",0,"11223344",null,2,"AA55CCDD","00000000",0,"11223344CCDD","FFFFFFFF0000"),
        new("incoming lower-start explicit extension",2,"A5C37E99","F00FF000",0,"10203040","F0000FF0",0,"1020A0437E99","F000FFFFF000"),
        new("incoming lower-start null extension",2,"A5C37E99","F00FF000",0,"10203040",null,0,"1020B0437E99","FFFFFFFFF000"),
        new("incoming lower-start explicit with old null",2,"A5C37E99",null,0,"10203040","01020304",0,"1020A5C37E99","0102FFFFFFFF"),
        new("incoming lower-start both-null",2,"A5C37E99",null,0,"10203040",null,0,"1020A5C37E99",null),
        new("same-start incoming null longer",0,"A5C3","F00F",0,"10203040",null,0,"10203040",null),
        new("same-start incoming null shorter",0,"A5C37E99","F00FF000",0,"1020",null,0,"B0237E99","FFFFF000"),
        new("same-start incoming explicit shorter old null",0,"A5C37E99",null,0,"1020","F00F",0,"A5C37E99","FFFFFFFF"),
        new("same-start incoming explicit longer old null",0,"A5C3",null,0,"10203040","F00FF000",0,"A5C33040","FFFFF000"),
        new("same-start both-null old right wins",0,"A5C3",null,0,"1020",null,0,"A5C3",null),
        new("same-start incoming explicit old null",0,"A5C3",null,0,"1020","F00F",0,"A5C3","FFFF"),
        new("same-start incoming null old explicit",0,"A5C3","F00F",0,"1020",null,0,"1020",null),
        new("prepared code extends region to right",100,"00000000","00000000",102,"A5B6C700","F00FF000",100,"0000A006C700","0000F00FF000"),
        new("longer same-start code keeps exclusive raw tail",100,"00000000","00000000",100,"A5B600D8E900","F00F00F0F000",100,"A00600D0E900","F00F00F0F000"),
        new("complete maskless code extends region",100,"00000000","00000000",102,"A5B6C7D8",null,100,"0000A5B6C7D8","0000FFFFFFFF"),
        new("supplied prefix and unknown padding stay distinct",100,"00000000","00000000",102,"A5000000","FF000000",100,"0000A5000000","0000FF000000"),
    ];

    internal static int Run(string directory)
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool ok)
        { checks++; Console.WriteLine((ok ? "PASS " : "FAIL ") + "NativeImageCoalescing " + name); if (!ok) failures++; }
        void Reject(string name, object? earlier, object? incoming, string code, object? ledger = null)
        {
            ledger ??= Ledger(); var allocated = Get<long>(ledger, "AllocatedBytes");
            var arrays = new List<(byte[] Buffer, byte[] Before)>();
            foreach (var segment in new[] { earlier, incoming }.Where(s => s is not null))
                foreach (var property in new[] { "Data", "Mask" })
                    if (Get<byte[]?>(segment!, property) is { } array) arrays.Add((array, (byte[])array.Clone()));
            string? actual = null;
            try { _ = Coalesce(earlier, incoming, ledger); }
            catch (TargetInvocationException ex)
            {
                var issue = ex.InnerException?.GetType().GetProperty("Issue", Any)?.GetValue(ex.InnerException);
                actual = issue is null ? ex.InnerException?.GetType().Name : Get<object>(issue, "Code").ToString();
            }
            Check(name, actual == code && allocated == Get<long>(ledger, "AllocatedBytes")
                && arrays.All(a => a.Buffer.SequenceEqual(a.Before)));
        }
        foreach (var vector in Vectors())
        {
            var earlier = Segment(vector.EarlierOffset, Convert.FromHexString(vector.EarlierData),
                vector.EarlierMask is null ? null : Convert.FromHexString(vector.EarlierMask));
            var incoming = Segment(vector.IncomingOffset, Convert.FromHexString(vector.IncomingData),
                vector.IncomingMask is null ? null : Convert.FromHexString(vector.IncomingMask));
            var ledger = Ledger(); var result = Coalesce(earlier, incoming, ledger); var range = Get<object>(result, "Range");
            var left = vector.EarlierOffset < vector.IncomingOffset ? earlier : incoming;
            var grows = vector.ResultData.Length / 2 > Get<byte[]>(left, "Data").Length;
            var expectedAllocation = grows ? vector.ResultData.Length / 2 * (vector.ResultMask is null ? 1L : 2L) : 0;
            Check(vector.Name, Data(result) == vector.ResultData && Mask(result) == vector.ResultMask
                && Get<uint>(range, "ByteOffset") == vector.ResultOffset && Get<int>(range, "Length") == vector.ResultData.Length / 2
                && Get<long>(ledger, "AllocatedBytes") == expectedAllocation
                && Equals(Get<object>(range, "Target"), Get<object>(Get<object>(left, "Range"), "Target")));
        }

        Reject("null earlier is rejected before changing incoming", null, Segment(0, [1], null), "InvalidDeclaration");
        Reject("null incoming is rejected before changing earlier", Segment(0, [1], null), null, "InvalidDeclaration");
        Reject("zero-size segment is invalid", Segment(0, [], null), Segment(0, [1], null), "InvalidDeclaration");
        Reject("negative length is invalid", Segment(0, [1], null, length: -1), Segment(0, [2], null), "InvalidDeclaration");
        Reject("data length must equal range", Segment(0, [1, 2], null, length: 1), Segment(0, [2], null), "InvalidDeclaration");
        Reject("short mask is invalid", Segment(0, [1, 2], [0]), Segment(0, [2], null), "InvalidDeclaration");
        Reject("long mask is invalid", Segment(0, [1], [0, 0]), Segment(0, [2], null), "InvalidDeclaration");
        Reject("null data is controlled", New("NativeImageMutableSegment", Range(0, 1), null, null), Segment(0, [2], null), "InvalidDeclaration");
        Reject("null range is controlled", New("NativeImageMutableSegment", null, new byte[] { 1 }, null), Segment(0, [2], null), "InvalidDeclaration");
        Reject("null target is controlled", New("NativeImageMutableSegment", New("NativeImageRange", null, 0u, 1), new byte[] { 1 }, null),
            Segment(0, [2], null), "InvalidDeclaration");
        Reject("invalid relative target is controlled", Segment(0, [1], null, Target("RelativeMemory")), Segment(0, [2], null), "InvalidDeclaration");
        Reject("different valid logical spaces do not coalesce", Segment(0, [1], null), Segment(0, [2], null, Target("RelativeMemory", 1)), "UnsupportedMapping");
        var property1 = Target("PropertyData", property: new(PropertySourceKind.SystemProperty, 7, NativeObjectIndex: 1));
        var property2 = Target("PropertyData", property: new(PropertySourceKind.SystemProperty, 7, NativeObjectIndex: 2));
        Reject("distinct property selectors do not coalesce", Segment(0, [1], [0], property1), Segment(0, [2], [0], property2), "UnsupportedMapping");
        Reject("touching ranges remain separate", Segment(0, [1, 2], [0, 0]), Segment(2, [3], [0]), "UnsupportedScenario");
        Reject("a gap cannot be filled by invented bytes", Segment(0, [1], [0]), Segment(2, [3], [0]), "UnsupportedScenario");
        Reject("incoming lower range with a gap is also refused", Segment(2, [3], [0]), Segment(0, [1], [0]), "UnsupportedScenario");
        Reject("unsigned end overflow is rejected before mutation", Segment(uint.MaxValue, [1, 2], [0, 0]), Segment(uint.MaxValue, [3], [0]), "InvalidDeclaration");
        Reject("a single range above1MiB is invalid before allocation", Segment(0, [1], null, length: 1024 * 1024 + 1),
            Segment(0, [2], null), "InvalidDeclaration");
        var missingLedgerData = new byte[] { 0xA5 }; var missingLedgerMask = new byte[] { 0xF0 }; string? missingLedgerCode = null;
        try { _ = Coalesce(Segment(0, missingLedgerData, missingLedgerMask), Segment(0, [0], [0]), null); }
        catch (TargetInvocationException ex)
        {
            var issue = ex.InnerException?.GetType().GetProperty("Issue", Any)?.GetValue(ex.InnerException);
            missingLedgerCode = issue is null ? ex.InnerException?.GetType().Name : Get<object>(issue, "Code").ToString();
        }
        Check("missing ledger is a controlled pre-mutation failure", missingLedgerCode == "InvalidRequest" && missingLedgerData[0] == 0xA5 && missingLedgerMask[0] == 0xF0);

        var data = new byte[] { 1, 2 }; var mask = new byte[] { 0, 0 };
        Reject("same segment cannot be consumed twice", Segment(0, data, mask), Segment(0, data, mask), "AmbiguousOwnership");
        Reject("shared data with different masks is invalid", Segment(0, data, [0, 0]), Segment(0, data, [255, 255]), "AmbiguousOwnership");
        Reject("shared masks with different data are invalid", Segment(0, [1, 2], mask), Segment(0, [3, 4], mask), "AmbiguousOwnership");
        Reject("earlier data cannot also be its mask", Segment(0, data, data), Segment(0, [3, 4], [0, 0]), "AmbiguousOwnership");
        Reject("incoming data cannot also be its mask", Segment(0, [3, 4], [0, 0]), Segment(0, data, data), "AmbiguousOwnership");
        Reject("earlier mask cannot be incoming data", Segment(0, data, mask), Segment(0, mask, [0, 0]), "AmbiguousOwnership");
        Reject("earlier data cannot be incoming mask", Segment(0, data, mask), Segment(0, [3, 4], data), "AmbiguousOwnership");

        const long limit = 16L * 1024 * 1024;
        Reject("combined growth is checked before either allocation", Segment(0, [0x10, 0x20], null), Segment(1, [0xA5, 0xC3, 0x7E], [0xF0, 0x0F, 0xF0]),
            "LimitExceeded", Ledger(limit - 7));
        var exact = Ledger(limit - 8);
        var exactResult = Coalesce(Segment(0, [0x10, 0x20], null), Segment(1, [0xA5, 0xC3, 0x7E], [0xF0, 0x0F, 0xF0]), exact);
        Check("exact combined growth budget includes prefix materialization", Get<long>(exact, "AllocatedBytes") == limit
            && Data(exactResult) == "10A0C37E" && Mask(exactResult) == "FFFF0FF0");
        Reject("null-null growth still budgets data", Segment(0, [1, 2], null), Segment(1, [3, 4, 5], null), "LimitExceeded", Ledger(limit - 3));
        var nullLedger = Ledger(limit - 4); var nullResult = Coalesce(Segment(0, [1, 2], null), Segment(1, [3, 4, 5], null), nullLedger);
        Check("null-null growth allocates only the counted data array", Data(nullResult) == "01030405" && Mask(nullResult) is null && Get<long>(nullLedger, "AllocatedBytes") == limit);
        var fullLedger = Ledger(limit); var noGrowth = Coalesce(Segment(0, [0x10, 0x20], [0, 0]), Segment(1, [0xA5], [0xF0]), fullLedger);
        Check("contained merge needs no payload allocation at a full ledger", Data(noGrowth) == "10A0" && Mask(noGrowth) == "00F0" && Get<long>(fullLedger, "AllocatedBytes") == limit);

        var leftData = new byte[] { 0x10, 0x20, 0x30 }; var leftMask = new byte[] { 0, 0, 0 };
        var rightData = new byte[] { 0xA5 }; var rightMask = new byte[] { 0xF0 };
        var inplace = Coalesce(Segment(0, leftData, leftMask), Segment(1, rightData, rightMask), Ledger());
        Check("containment mutates only the owned spatial left buffers", ReferenceEquals(Get<byte[]>(inplace, "Data"), leftData)
            && ReferenceEquals(Get<byte[]?>(inplace, "Mask"), leftMask) && leftData.SequenceEqual(new byte[] { 0x10, 0xA0, 0x30 })
            && rightData[0] == 0xA5 && rightMask[0] == 0xF0);
        var tiedOld = new byte[] { 0xA5, 0xC3 }; var tiedNew = new byte[] { 0x10, 0x20 };
        var tied = Coalesce(Segment(0, tiedOld, null), Segment(0, tiedNew, null), Ledger());
        Check("same start selects incoming buffer but old right data", ReferenceEquals(Get<byte[]>(tied, "Data"), tiedNew)
            && tiedOld.SequenceEqual(new byte[] { 0xA5, 0xC3 }) && tiedNew.SequenceEqual(tiedOld));
        var growData = new byte[] { 0x10, 0x20 }; var growMask = new byte[] { 0, 0 };
        var growIncoming = new byte[] { 0xA5, 0xC3, 0x7E }; var growIncomingMask = new byte[] { 0xF0, 0x0F, 0xF0 };
        var grown = Coalesce(Segment(0, growData, growMask), Segment(1, growIncoming, growIncomingMask), Ledger());
        Check("growth owns new data and mask while preserving both input arrays", !ReferenceEquals(Get<byte[]>(grown, "Data"), growData)
            && !ReferenceEquals(Get<byte[]?>(grown, "Mask"), growMask) && growData.SequenceEqual(new byte[] { 0x10, 0x20 })
            && growMask.All(b => b == 0) && growIncoming.SequenceEqual(new byte[] { 0xA5, 0xC3, 0x7E })
            && growIncomingMask.SequenceEqual(new byte[] { 0xF0, 0x0F, 0xF0 }));
        var high = Coalesce(Segment(uint.MaxValue - 1, [0x11, 0x22], [0, 0]), Segment(uint.MaxValue, [0xA5], [0xF0]), Ledger());
        Check("half-open unsigned end at2pow32 is valid without wrapping", Get<uint>(Get<object>(high, "Range"), "ByteOffset") == uint.MaxValue - 1
            && Get<int>(Get<object>(high, "Range"), "Length") == 2 && Data(high) == "11A0" && Mask(high) == "00F0");

        const int maximum = 1024 * 1024;
        var maximumData = new byte[maximum - 1]; var maximumMask = new byte[maximum - 1];
        Reject("valid individual ranges cannot form a union above1MiB", Segment(0, maximumData, maximumMask),
            Segment(maximum - 2, [0xA5, 0x5A, 0], [0xF0, 0x0F, 0]), "LimitExceeded");
        var maximumLedger = Ledger(); var maximumResult = Coalesce(Segment(0, maximumData, maximumMask),
            Segment(maximum - 2, [0xA5, 0x5A], [0xF0, 0x0F]), maximumLedger);
        var expectedData = new byte[maximum]; expectedData[^2] = 0xA0; expectedData[^1] = 0x5A;
        var expectedMask = new byte[maximum]; expectedMask[^2] = 0xF0; expectedMask[^1] = 0x0F;
        Check("exact1MiB union preserves full arrays and raw tail with a2MiB charge", Get<byte[]>(maximumResult, "Data").SequenceEqual(expectedData)
            && Get<byte[]?>(maximumResult, "Mask")!.SequenceEqual(expectedMask) && Get<long>(maximumLedger, "AllocatedBytes") == 2L * maximum);
        Check("large growth does not mutate any old prefix buffer", maximumData.All(b => b == 0) && maximumMask.All(b => b == 0));
        Console.WriteLine($"NativeImageCoalescing: {checks} checks, {failures} failures");
        return failures;
    }
}
