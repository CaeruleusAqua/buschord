using System.Xml.Linq;
using Knx;

namespace Knx.App.Services;

public sealed record ProductMigrationInput(string OldReference, string NewReference, string? NewProgram,
    IReadOnlyDictionary<string, string>? ParameterMappings = null,
    IReadOnlyDictionary<string, string>? ObjectMappings = null,
    IReadOnlyDictionary<string, string>? ChannelMappings = null);

public partial class MainWindowViewModel
{
    private IReadOnlyList<ProductMigrationPlan>? _legacyMigration;
    private ProductMigrationInput? _legacyMigrationInput;

    public IReadOnlyList<ProductMigrationPlan> PreviewProductMigration(ProductMigrationInput input)
    {
        if (Project is not { } project) return [];
        var devices = ProductReplacer.Find(project, input.OldReference);
        if (devices.Count == 0) return [];
        var target = MigrationApplication(input.NewProgram ?? input.NewReference);
        var targetProgram = input.NewProgram ?? target.ProgramReference;
        var sources = new Dictionary<string, (XDocument Document, string ProgramReference, IReadOnlyDictionary<string, byte[]>? Parts)>();
        var prepared = new List<(DeviceInstance Device, XDocument Source, IReadOnlyDictionary<string, byte[]> Parts)>();
        long estimatedBytes = 0;
        foreach (var device in devices)
        {
            var reference = device.Hardware2ProgramRefId ?? device.ProductRefId;
            if (!sources.TryGetValue(reference, out var source)) sources[reference] = source = MigrationApplication(reference);
            var parts = MergeMigrationParts(source.Parts, target.Parts);
            estimatedBytes += 2L * (source.Document.ToString(SaveOptions.DisableFormatting).Length
                + target.Document.ToString(SaveOptions.DisableFormatting).Length) + parts.Values.Sum(p => (long)p.Length);
            if (estimatedBytes > 256L * 1024 * 1024)
                throw new InvalidDataException("Product migration exceeds the batch memory budget.");
            prepared.Add((device, source.Document, parts));
        }
        return prepared.Select(item => ProductReplacer.Preview(project, item.Device, input.NewReference, targetProgram, item.Source, target.Document,
                input.ParameterMappings, input.ObjectMappings, input.ChannelMappings,
                scripts: Knx.Scripting.IsolatedScriptRuntime.Shared, targetParts: item.Parts)).ToArray();
    }

    public IReadOnlyList<ProductReplaceResult> ApplyProductMigration(IReadOnlyList<ProductMigrationPlan> plans)
    {
        if (Project is not { } project) throw new InvalidOperationException(Strings.Get("LinkProjectChanged"));
        var results = ProductReplacer.Apply(project, plans.ToArray());
        if (results.Count > 0)
        {
            _catalogPrograms.Clear(); _archiveBlocks.Clear();
            _undo.Push(project); NotifyEditState(); Refresh();
        }
        return results;
    }

    private (XDocument Document, string ProgramReference, IReadOnlyDictionary<string, byte[]>? Parts) MigrationApplication(string reference)
    {
        if (Project is { } project && Knxproj.LoadApplicationProgram(project, reference) is { } embedded)
            return (embedded, reference, null);
        var matches = new List<(XDocument Document, string ProgramReference, IReadOnlyDictionary<string, byte[]>? Parts)>();
        foreach (var catalog in _catalog.Current?.Catalogs ?? [])
        {
            var programReference = reference;
            if (catalog.FindProduct(reference) is { } product)
            {
                var references = catalog.GetProgramReferencesFor(product).ToArray();
                if (references.Length != 1) throw new InvalidDataException("Select an explicit application reference for this product.");
                programReference = references[0];
            }
            if (catalog.LoadProgramDocument(programReference) is { } document) matches.Add((document, programReference, catalog.ReadManufacturerParts(programReference)));
        }
        if (matches.Count != 1) throw new InvalidDataException("Exactly one resolved manufacturer application is required for " + reference);
        return matches[0];
    }

    private static IReadOnlyDictionary<string, byte[]> MergeMigrationParts(IReadOnlyDictionary<string, byte[]>? source,
        IReadOnlyDictionary<string, byte[]>? target)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, value) in (source ?? new Dictionary<string, byte[]>()).Concat(target ?? new Dictionary<string, byte[]>()))
        {
            if (result.TryGetValue(key, out var previous) && !previous.AsSpan().SequenceEqual(value))
                throw new InvalidDataException("Conflicting source and target manufacturer archive parts: " + key);
            result[key] = value;
        }
        return result;
    }
}
