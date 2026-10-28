using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Metadata;

namespace Knx.App.Localization;

public sealed class UpdateUiStrings : INotifyPropertyChanged
{
    public static UpdateUiStrings Instance { get; } = new();
    private UpdateUiStrings() => Strings.Instance.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName != "Item[]") return;
        PropertyChanged?.Invoke(this, new("Item"));
        PropertyChanged?.Invoke(this, new("Item[]"));
    };
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Get(key);
    public static string Get(string key, params object[] arguments) =>
        LanguageCatalog.Resolve(Map, Overlays.Value, key, Strings.Language, arguments);
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> Overlays =
        new(() => LanguageCatalog.LoadOverlays(typeof(UpdateUiStrings).Assembly, "updates", Strings.Available));
    private static readonly Dictionary<string, (string De, string En)> Map = new()
    {
        ["Title"] = ("Updates", "Updates"),
        ["Intro"] = ("BusChord prüft signierte Updates und erhält offene Projekte. Ein Versionswechsel erfolgt beim nächsten sicheren Start oder nach normalem Beenden.", "BusChord checks signed updates and preserves open projects. Versions change at the next safe start or after a normal exit."),
        ["Mode"] = ("Updateverhalten", "Update policy"),
        ["Automatic"] = ("Automatisch herunterladen und installieren", "Download and install automatically"),
        ["CheckOnly"] = ("Nur nach Updates suchen", "Check for updates only"),
        ["Disabled"] = ("Automatische Suche aus", "Automatic checks off"),
        ["Channel"] = ("Updatekanal", "Update channel"),
        ["Stable"] = ("Stabile Versionen", "Stable releases"),
        ["Preview"] = ("Vorabversionen", "Preview releases"),
        ["PolicyHint"] = ("Automatische Prüfungen erfolgen beim Start und danach alle sechs Stunden. Offene Instanzen verhindern den Austausch der Programmdateien.", "Automatic checks run at startup and every six hours. Open instances prevent replacement of application files."),
        ["ManagedHint"] = ("Diese Installation wird vom Linux-Paketmanager aktualisiert. Der installierte Dienst aktualisiert ausschließlich BusChord. Seine systemweite Richtlinie verwaltet die Administration.", "The Linux package manager updates this installation. The installed service updates BusChord only. An administrator manages its system-wide policy."),
        ["FeedHint"] = ("Updatequelle und öffentliche Signierschlüssel gehören zum geprüften Programmpaket und sind hier nicht veränderbar.", "The reviewed application package supplies the update source and public signing keys; they cannot be changed here."),
        ["CheckNow"] = ("Jetzt prüfen", "Check now"),
        ["Retry"] = ("Fehlgeschlagenes Update erneut versuchen", "Retry failed update"),
        ["State"] = ("Status", "Status"),
        ["Version"] = ("Installierte Programmversion: {0}", "Installed application version: {0}"),
        ["Saved"] = ("Updateeinstellungen gespeichert.", "Update preferences saved."),
        ["SaveFailed"] = ("Updateeinstellungen konnten nicht gespeichert werden: {0}", "Update preferences could not be saved: {0}"),
        ["OperationFailed"] = ("Updatevorgang fehlgeschlagen: {0}", "Update operation failed: {0}"),
        ["Idle"] = ("Bereit zur Prüfung.", "Ready to check."),
        ["Checking"] = ("Updates werden geprüft…", "Checking for updates…"),
        ["Downloading"] = ("Das verifizierte Update wird heruntergeladen…", "Downloading the verified update…"),
        ["Ready"] = ("Update bereit. Es wird beim nächsten sicheren Start oder nach normalem Beenden installiert.", "Update ready. It will install at the next safe start or after a normal exit."),
        ["UpToDate"] = ("Kein neueres Update verfügbar.", "No newer update is available."),
        ["Available"] = ("Ein neueres Update ist verfügbar. Der automatische Modus lädt es herunter.", "A newer update is available. Automatic mode downloads it."),
        ["DisabledState"] = ("Automatische Updates sind ausgeschaltet. Eine manuelle Prüfung bleibt möglich.", "Automatic updates are off. You can still check manually."),
        ["Unavailable"] = ("Für diese Entwicklungs-, manuelle oder nicht unterstützte Installation ist kein automatischer Austausch eingerichtet.", "Automatic replacement is not configured for this development, manual or unsupported installation."),
        ["Failed"] = ("Das Update konnte nicht verarbeitet werden. Die bisherige Programmversion bleibt verfügbar.", "The update could not be processed. The existing application version remains available."),
        ["Cancelled"] = ("Updateprüfung abgebrochen.", "Update check cancelled."),
        ["PreferencesInvalid"] = ("Die lokalen Updateeinstellungen sind ungültig. Automatische Vorgänge bleiben bis zum Speichern gültiger Einstellungen ausgeschaltet.", "Local update preferences are invalid. Automatic operations remain off until valid preferences are saved."),
        ["ApplyFailed"] = ("Der Versionswechsel wurde zurückgestellt. Die bisherige Version bleibt geöffnet; ein bewusster neuer Versuch ist möglich.", "The version change was deferred. The existing version remains open; you can explicitly retry."),
        ["UserData"] = ("Die Installation enthält zusätzliche oder veränderte Dateien. Verschieben Sie eigene Daten aus dem Programmordner, bevor Sie das Update erneut versuchen.", "The installation contains additional or modified files. Move personal data out of the application directory before retrying the update."),
        ["StartupBlocked"] = ("Ein Update oder eine andere Installation verwendet die Programmdateien. Bitte starten Sie BusChord nach Abschluss erneut.", "An update or another installation is using the application files. Please reopen BusChord when it has finished."),
    };
}

public sealed class UpExtension : MarkupExtension
{
    public UpExtension() { }
    public UpExtension(string key) => Key = key;
    [ConstructorArgument("key")] public string Key { get; set; } = "";
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = UpdateUiStrings.Instance, Mode = BindingMode.OneWay };
}
