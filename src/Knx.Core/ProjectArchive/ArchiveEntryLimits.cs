using System.Text.RegularExpressions;

namespace Knx;

internal static class ArchiveEntryLimits
{
    internal const int ApplicationXml = 128 * 1024 * 1024;
    internal const int OtherEntry = 64 * 1024 * 1024;
    private static readonly Regex ApplicationPath = new(@"\A(M-[0-9a-fA-F]{4})/\1_A-[A-Za-z0-9_.-]+\.xml\z", RegexOptions.CultureInvariant);
    internal static bool IsApplication(string name) => ApplicationPath.IsMatch(name);
    internal static int For(string name) => IsApplication(name) ? ApplicationXml : OtherEntry;
}
