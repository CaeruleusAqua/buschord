using Knx;

var failures = 0;
void Check(string name, bool ok)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
    if (!ok) failures++;
}

foreach (var invalid in new[] { "16.1.1", "1.16.1", "255.255.255", "1.1.256", "-1.0.1", "1.2", "" })
{
    Check($"TryParse rejects {invalid}", !IndividualAddress.TryParse(invalid, out _));
    try { IndividualAddress.Parse(invalid); Check($"Parse rejects {invalid}", false); }
    catch (FormatException) { Check($"Parse rejects {invalid}", true); }
}
try { _ = new IndividualAddress(16, 0, 1); Check("Constructor rejects area overflow", false); }
catch (ArgumentOutOfRangeException) { Check("Constructor rejects area overflow", true); }
try { _ = new IndividualAddress(0, 16, 1); Check("Constructor rejects line overflow", false); }
catch (ArgumentOutOfRangeException) { Check("Constructor rejects line overflow", true); }

Check("Maximum individual address", IndividualAddress.Parse("15.15.255").Raw == ushort.MaxValue);
Check("Coupler and zero address remain representable", IndividualAddress.Parse("1.1.0").Device == 0
    && IndividualAddress.Parse("0.0.0").Raw == 0);
Check("Valid addresses are not altered", IndividualAddress.Parse("1.0.25").ToString() == "1.0.25");
Console.WriteLine(failures == 0 ? "ADDRESS CHECKS OK" : $"{failures} failures");
return failures == 0 ? 0 : 1;
