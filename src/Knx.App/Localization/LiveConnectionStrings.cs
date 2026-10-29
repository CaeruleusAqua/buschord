namespace Knx.App.Localization;

public static class LiveConnectionStrings
{
    public static IReadOnlyList<string> Languages { get; } = ["de", "en", "fr", "es", "it", "nl", "pl", "zh-CN"];
    public static IReadOnlyCollection<string> Keys => Text.Keys;
    public static string Get(string key) => GetForLanguage(Strings.Language, key);
    public static string GetForLanguage(string language, string key)
    {
        var index = Languages.ToList().IndexOf(language); if (index < 0) index = 1;
        return Text.TryGetValue(key, out var values) ? values[index] : key;
    }
    private static readonly Dictionary<string, string[]> Text = new()
    {
        ["Title"] = ["Busverbindung", "Bus connection", "Connexion au bus", "Conexión al bus", "Connessione bus", "Busverbinding", "Połączenie z magistralą", "总线连接"],
        ["Gateway"] = ["Gateway (IPv4)", "Gateway (IPv4)", "Passerelle (IPv4)", "Pasarela (IPv4)", "Gateway (IPv4)", "Gateway (IPv4)", "Brama (IPv4)", "网关 (IPv4)"],
        ["Port"] = ["UDP-Port", "UDP port", "Port UDP", "Puerto UDP", "Porta UDP", "UDP-poort", "Port UDP", "UDP 端口"],
        ["TunnelAddress"] = ["Angeforderte Tunneladresse", "Requested tunnel address", "Adresse de tunnel demandée", "Dirección de túnel solicitada", "Indirizzo tunnel richiesto", "Aangevraagd tunneladres", "Żądany adres tunelu", "请求的隧道地址"],
        ["ReadOnly"] = ["Echt · nur lesen", "Real · read only", "Réel · lecture seule", "Real · solo lectura", "Reale · sola lettura", "Echt · alleen lezen", "Rzeczywiste · tylko odczyt", "真实连接 · 只读"],
        ["Demo"] = ["Demo", "Demo", "Démo", "Demostración", "Demo", "Demo", "Demonstracja", "演示"],
        ["Real"] = ["Echter Bus", "Real bus", "Bus réel", "Bus real", "Bus reale", "Echte bus", "Rzeczywista magistrala", "真实总线"],
        ["Origin"] = ["Quelle", "Source", "Source", "Origen", "Origine", "Bron", "Źródło", "来源"],
        ["Connect"] = ["Verbinden", "Connect", "Connecter", "Conectar", "Connetti", "Verbinden", "Połącz", "连接"],
        ["CancelConnect"] = ["Verbindung abbrechen", "Cancel connection", "Annuler la connexion", "Cancelar conexión", "Annulla connessione", "Verbinding annuleren", "Anuluj połączenie", "取消连接"],
        ["Disconnect"] = ["Trennen", "Disconnect", "Déconnecter", "Desconectar", "Disconnetti", "Verbreken", "Rozłącz", "断开"],
        ["Close"] = ["Schließen", "Close", "Fermer", "Cerrar", "Chiudi", "Sluiten", "Zamknij", "关闭"],
        ["Disconnected"] = ["Nicht verbunden", "Disconnected", "Déconnecté", "Desconectado", "Disconnesso", "Niet verbonden", "Rozłączono", "未连接"],
        ["Connecting"] = ["Verbindung wird aufgebaut…", "Connecting…", "Connexion…", "Conectando…", "Connessione…", "Verbinden…", "Łączenie…", "正在连接…"],
        ["Connected"] = ["Verbunden", "Connected", "Connecté", "Conectado", "Connesso", "Verbonden", "Połączono", "已连接"],
        ["Disconnecting"] = ["Verbindung wird geschlossen…", "Disconnecting…", "Déconnexion…", "Desconectando…", "Disconnessione…", "Verbinding sluiten…", "Rozłączanie…", "正在断开…"],
        ["Failed"] = ["Verbindung fehlgeschlagen", "Connection failed", "Échec de connexion", "Error de conexión", "Connessione non riuscita", "Verbinding mislukt", "Połączenie nieudane", "连接失败"],
        ["ConnectFailed"] = ["Gateway konnte nicht verbunden werden.", "Could not connect to the gateway.", "Connexion à la passerelle impossible.", "No se pudo conectar a la pasarela.", "Impossibile connettersi al gateway.", "Kan niet verbinden met de gateway.", "Nie można połączyć z bramą.", "无法连接到网关。"],
        ["Cancelled"] = ["Verbindung abgebrochen", "Connection cancelled", "Connexion annulée", "Conexión cancelada", "Connessione annullata", "Verbinding geannuleerd", "Połączenie anulowane", "连接已取消"],
        ["LinkLost"] = ["Verbindung zum Gateway verloren", "Gateway connection lost", "Connexion à la passerelle perdue", "Conexión a la pasarela perdida", "Connessione al gateway persa", "Gatewayverbinding verloren", "Utracono połączenie z bramą", "网关连接已丢失"],
        ["Occupied"] = ["Dieses Gateway wird bereits in einem anderen Fenster verwendet.", "This gateway is already in use in another window.", "Cette passerelle est utilisée dans une autre fenêtre.", "Esta pasarela ya se utiliza en otra ventana.", "Il gateway è già in uso in un'altra finestra.", "Deze gateway is al in gebruik in een ander venster.", "Ta brama jest już używana w innym oknie.", "此网关已由另一个窗口使用。"],
        ["CleanupFailed"] = ["Verbindung konnte nicht vollständig geschlossen werden. Trennen erneut versuchen.", "Connection cleanup failed. Retry disconnect.", "Échec de fermeture. Réessayez la déconnexion.", "No se pudo cerrar la conexión. Reintente desconectar.", "Chiusura non riuscita. Riprova a disconnettere.", "Sluiten mislukt. Probeer opnieuw te verbreken.", "Nie udało się zamknąć połączenia. Ponów rozłączenie.", "连接清理失败。请重试断开。"],
        ["StaleProject"] = ["Projekt wurde gewechselt. Verbindung erneut wählen.", "Project changed. Select the connection again.", "Le projet a changé. Choisissez à nouveau la connexion.", "El proyecto cambió. Seleccione de nuevo la conexión.", "Il progetto è cambiato. Seleziona di nuovo la connessione.", "Project gewijzigd. Kies de verbinding opnieuw.", "Projekt się zmienił. Wybierz połączenie ponownie.", "项目已更改。请重新选择连接。"],
        ["Invalid"] = ["Eine gültige IPv4, Port 1–65535 und Tunneladresse mit Gerät 1–254 eingeben.", "Enter a valid IPv4, port 1–65535 and tunnel address with device 1–254.", "Saisissez une IPv4 valide, un port 1–65535 et une adresse de tunnel avec appareil 1–254.", "Introduzca IPv4 válida, puerto 1–65535 y dirección de túnel con dispositivo 1–254.", "Inserisci IPv4 valida, porta 1–65535 e indirizzo tunnel con dispositivo 1–254.", "Voer een geldig IPv4-adres, poort 1–65535 en tunneladres met apparaat 1–254 in.", "Podaj prawidłowy IPv4, port 1–65535 i adres tunelu z urządzeniem 1–254.", "请输入有效的 IPv4、1–65535 端口及设备号为 1–254 的隧道地址。"],
        ["GroupOnly"] = ["Standard-UDP-Tunnel · Gruppenmonitor. Kein vollständiger Hardware-Busmonitor; Schreiben ist gesperrt.", "Standard UDP tunnel · group monitor. Not a complete hardware bus monitor; writes are disabled.", "Tunnel UDP standard · moniteur de groupe. Ce n'est pas un moniteur matériel complet ; écriture désactivée.", "Túnel UDP estándar · monitor de grupos. No es un monitor físico completo; escritura desactivada.", "Tunnel UDP standard · monitor gruppi. Non è un monitor hardware completo; scrittura disabilitata.", "Standaard UDP-tunnel · groepsmonitor. Geen volledige hardwarebusmonitor; schrijven uitgeschakeld.", "Standardowy tunel UDP · monitor grup. Nie jest pełnym monitorem sprzętowym; zapis wyłączony.", "标准 UDP 隧道 · 组监视器。不是完整的硬件总线监视器；写入已禁用。"],
        ["ScanDisconnect"] = ["Vor dem echten Scan Verbindung trennen. Der Scan reserviert das Gateway separat.", "Disconnect before a real scan. The scan reserves the gateway separately.", "Déconnectez avant un scan réel. Le scan réserve la passerelle séparément.", "Desconecte antes de un escaneo real. El escaneo reserva la pasarela por separado.", "Disconnetti prima della scansione reale. La scansione riserva il gateway separatamente.", "Verbreek de verbinding vóór een echte scan. De scan reserveert de gateway apart.", "Rozłącz przed rzeczywistym skanem. Skan rezerwuje bramę osobno.", "真实扫描前请断开连接。扫描会单独占用网关。"],
        ["Configure"] = ["Verbindung wählen…", "Choose connection…", "Choisir la connexion…", "Elegir conexión…", "Scegli connessione…", "Verbinding kiezen…", "Wybierz połączenie…", "选择连接…"],
        ["Responded"] = ["Antwort erhalten", "Responded", "Réponse reçue", "Respondió", "Risposta ricevuta", "Antwoord ontvangen", "Odpowiedź", "已响应"],
        ["Unknown"] = ["Unbekannt · keine bestätigte Antwort", "Unknown · no confirmed reply", "Inconnu · aucune réponse confirmée", "Desconocido · sin respuesta confirmada", "Sconosciuto · nessuna risposta confermata", "Onbekend · geen bevestigd antwoord", "Nieznany · brak potwierdzonej odpowiedzi", "未知 · 无确认响应"],
        ["NotProbed"] = ["Nicht geprüft", "Not probed", "Non testé", "No comprobado", "Non verificato", "Niet onderzocht", "Nie sprawdzono", "未探测"],
        ["ScanCancelled"] = ["Scan abgebrochen · Teilergebnisse erhalten", "Scan cancelled · partial results retained", "Scan annulé · résultats partiels conservés", "Escaneo cancelado · resultados parciales conservados", "Scansione annullata · risultati parziali conservati", "Scan geannuleerd · deelresultaten behouden", "Skan anulowany · zachowano częściowe wyniki", "扫描已取消 · 已保留部分结果"],
    };
}
