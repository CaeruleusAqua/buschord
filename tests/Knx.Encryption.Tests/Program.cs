using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;

var directory = Path.Combine(Path.GetTempPath(), "opencode", "buschord-encryption-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
int checks = 0;
try
{
    if (args.Contains("--recovery-only", StringComparer.Ordinal))
    {
        RecoveryPersistenceChecks.Run((name, ok) => Check(ok, name));
        Console.WriteLine($"ALL {checks} RECOVERY CHECKS PASSED");
        return;
    }
    RecoveryPersistenceChecks.Run((name, ok) => Check(ok, name));
    Check(ArchiveEntryLimits.For("M-0001/M-0001_A-large.xml") == 128 * 1024 * 1024, "canonical applications receive the separate archive entry budget");
    foreach (var name in new[] { "M-0001/M-0001_A-large.xml\n", "M-0002/M-0001_A-large.xml", "M-0001/images/M-0001_A-large.xml", "M-0001/opaque.bin" })
        Check(ArchiveEntryLimits.For(name) == 64 * 1024 * 1024, "noncanonical paths keep the ordinary entry budget");
    ConfigurationChecks.Run(directory, Check, (entries, password) => AesZip(entries, password), entries => PlainZip(entries));
    AesCtrChunkChecks.Run(Check);

    // 2. Synthetic ETS6-shaped project (schema /21, WinZip AES-256 AE-1), password "test".
    //    Generated here so no ETS, manufacturer, customer or credential data is tracked.
    var fixture = Path.Combine(directory, "synthetic-ets6.knxproj");
    var fixtureBytes = PlainZip(new()
    {
        ["P-0001.zip"] = AesZip(new()
        {
            ["0.xml"] = Encoding.UTF8.GetBytes(Synthetic21Project()),
            ["project.xml"] = Encoding.UTF8.GetBytes(Synthetic21Information()),
        }, "test"),
    });
    File.WriteAllBytes(fixture, fixtureBytes);
    var project = Knxproj.Load(fixture, "test");
    var devices = project.Installations.SelectMany(i => i.Areas.SelectMany(a => a.Lines.SelectMany(l =>
        l.Devices.Concat(l.Segments.SelectMany(s => s.Devices)).Concat(a.Devices)))).ToList();
    var addresses = project.Installations.SelectMany(i => i.MainRanges.SelectMany(FlattenRanges)).SelectMany(r => r.Addresses).ToList();
    Check(project.Id == "P-0001" && project.Name == "Synthetic project", "/21 project id and name from project.xml");
    Check(project.Guid == "11111111-2222-3333-4444-555555555555" && project.ProjectStart is not null, "/21 project metadata");
    Check(project.Installations.Count == 1 && project.Installations[0].GroupAddressStyle == GroupAddressStyle.ThreeLevel, "/21 installation and style");
    Check(devices.Count == 3 && string.Join(",", devices.Select(d => d.Address.ToString())) == "1.1.0,1.1.5,1.1.6",
        "/21 device numbers assembled to individual addresses");
    Check(addresses.Count == 3 && string.Join(",", addresses.Select(a => a.Address.Raw)) == "256,257,258", "/21 numeric group addresses");
    Check(project.Installations[0].Locations.Roots.Single().Name == "Synthetic building", "/21 building tree");
    Check(devices[1].ComObjectRefs.Count == 8
        && devices[1].ComObjectRefs[0].Links.Single() == addresses[0].Id,
        "/21 device links resolve to canonical group-address ids");
    var binaryProject = Knxproj.Load(fixture, "test");
    var binaryDevice = ProjectQuery.DeviceList(binaryProject).ElementAt(1);
    var binaryBytes = new byte[] { 35, 35, 48, 49, 0, 255, 17 };
    binaryDevice.BinaryData["FunctionTexts"] = binaryBytes;
    binaryDevice.ParameterValues["synthetic-raw"] = Convert.ToBase64String(new byte[] { 0, 255, 17, 0 });
    binaryDevice.Channels.Add(new() { Id = "native-channel-original", RefId = "CH-1", Name = "Synthetic channel" });
    binaryDevice.ComObjectRefs[0].ChannelId = "native-channel-original";
    ApplicationChannels.Refresh(binaryProject);
    var copyLocation = ProjectQuery.Devices(binaryProject).Single(d => ReferenceEquals(d.Device, binaryDevice));
    var binaryCopy = ProjectOperations.CopyDevice(binaryProject, binaryDevice, copyLocation.Line!);
    Check(binaryCopy.Ok && binaryCopy.Value!.Channels.Single().Id != "native-channel-original"
        && binaryCopy.Value.ComObjectRefs[0].ChannelId == binaryCopy.Value.Channels.Single().Id,
        "native copy allocates a unique channel instance and remaps its exact object pin");
    var binaryOutput = Path.Combine(directory, "encrypted-binary-roundtrip.knxproj");
    Knxproj.Save(binaryProject, binaryOutput, "test");
    var binaryReloaded = Knxproj.Load(binaryOutput, "test");
    var binaryAfter = ProjectQuery.DeviceList(binaryReloaded).Single(d => d.Id == binaryDevice.Id);
    Check(Knxproj.IsPasswordProtected(binaryReloaded) && binaryAfter.BinaryData["FunctionTexts"].SequenceEqual(binaryBytes)
        && binaryAfter.ParameterValues["synthetic-raw"] == "AP8RAA==", "protected native save retains binary payload and raw Base64 bytes");
    var binaryCopyAfter = ProjectQuery.DeviceList(binaryReloaded).Single(d => d.Id == binaryCopy.Value!.Id);
    Check(binaryCopyAfter.ComObjectRefs[0].ChannelId == binaryCopyAfter.Channels.Single().Id
        && binaryCopyAfter.Channels.Single().RefId == "CH-1"
        && binaryAfter.ComObjectRefs[0].ChannelId == "native-channel-original",
        "protected save/reload preserves original and copied native pins independently");
    Check(File.ReadAllBytes(fixture).SequenceEqual(fixtureBytes), "binary roundtrip leaves the protected source archive byte-identical");

    // /21 also loads as a plain archive (same XML, no encryption).
    var decrypted21 = EncryptedArchive.ReadAll(Unzip(fixtureBytes)["P-0001.zip"], "test");
    var plain21 = Path.Combine(directory, "plain-21.knxproj");
    File.WriteAllBytes(plain21, PlainZip(decrypted21));
    var plain21Project = Knxproj.Load(plain21);
    Check(plain21Project.Name == "Synthetic project" && plain21Project.Installations[0].Areas[1].Lines[1].Segments[0].Devices.Count == 3,
        "plain /21 archive loads");
    var plain21Before = File.ReadAllBytes(plain21);
    var plain21Copy = Path.Combine(directory, "plain-21-copy.knxproj");
    plain21Project.Name = "Edited native project";
    var editedDevice = ProjectQuery.DeviceList(plain21Project).Single(d => d.Address.Device == 5);
    editedDevice.Address = new(1, 1, 9); editedDevice.ParameterValues["R-synthetic"] = "Native edited value";
    editedDevice.ComObjectRefs[0].Number = 71; editedDevice.ComObjectRefs[0].Device = "Send";
    ProjectQuery.GroupAddresses(plain21Project).First().Address = new(260);
    Knxproj.Save(plain21Project, plain21Copy);
    var plain21Roundtrip = Knxproj.Load(plain21Copy);
    Check(plain21Roundtrip.Name == "Edited native project" && ProjectQuery.DeviceList(plain21Roundtrip).Any(d => d.Address == new IndividualAddress(1, 1, 9)), "/21 native save preserves metadata and device address edits");
    var plain21Entries = Unzip(File.ReadAllBytes(plain21Copy));
    using var nativeXmlStream = new MemoryStream(plain21Entries["0.xml"]);
    var plain21Xml = XDocument.Load(nativeXmlStream);
    Check(plain21Xml.Root!.Name.NamespaceName.EndsWith("/21") && plain21Xml.Descendants().Single(e => e.Name.LocalName == "DeviceInstance" && (string?)e.Attribute("Id") == editedDevice.Id).Attribute("Address")!.Value == "9", "/21 writer emits a native device number, not a full address");
    Check(plain21Xml.Descendants().First(e => e.Name.LocalName == "GroupAddress").Attribute("Address")!.Value == "260", "/21 writer emits a numeric group address");
    Check(plain21Xml.Descendants().Single(e => e.Name.LocalName == "ParameterInstanceRef").Name.NamespaceName.EndsWith("/21"), "/21 parameter edits use native ParameterInstanceRefs");
    Check(!plain21Xml.Root!.DescendantsAndSelf().Any(e => e.Name.NamespaceName == "urn:buschord:project:1") && !plain21Xml.Descendants().Attributes().Any(a => a.Name.NamespaceName == "urn:buschord:project:1"), "editor facts do not contaminate native /21 XML");
    Check(ProjectQuery.DeviceList(plain21Roundtrip).Single(d => d.Id == editedDevice.Id).ComObjectRefs[0].Number == 71
        && ProjectQuery.DeviceList(plain21Roundtrip).Single(d => d.Id == editedDevice.Id).ComObjectRefs[0].Device == "Send"
        && plain21Entries.ContainsKey("BusChord/project.xml"), "editor number and direction overrides survive through a separate sidecar");
    Check(File.ReadAllBytes(plain21).SequenceEqual(plain21Before), "/21 Save As leaves source identical");

    var inline21 = XDocument.Parse(Synthetic21Project());
    var inlineInfo = XDocument.Parse(Synthetic21Information()).Descendants().Single(e => e.Name.LocalName == "ProjectInformation");
    inlineInfo.Add(new XComment("opaque inline metadata")); inline21.Root!.Elements().Single().AddFirst(inlineInfo);
    var inlinePath = Path.Combine(directory, "inline-21.knxproj");
    File.WriteAllBytes(inlinePath, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(inline21.ToString()) }));
    var inlineProject = Knxproj.Load(inlinePath); inlineProject.Name = "Inline edited"; Knxproj.Save(inlineProject, inlinePath);
    var inlineEntries = Unzip(File.ReadAllBytes(inlinePath));
    using var inlineSource = new MemoryStream(inlineEntries["0.xml"]);
    var inlineSaved = XDocument.Load(inlineSource);
    Check(!inlineEntries.ContainsKey("project.xml") && inlineSaved.DescendantNodes().OfType<XComment>().Any(c => c.Value == "opaque inline metadata")
        && Knxproj.Load(inlinePath).Name == "Inline edited", "inline native metadata placement and opaque content survive edits");
    var sourceLessPath = Path.Combine(directory, "snapshot-21.knxproj");
    Knxproj.Save(Knxproj.FromXml(inline21), sourceLessPath);
    Check(Knxproj.Load(sourceLessPath).Name == "Synthetic project", "a standalone native /21 snapshot saves without an archive source");

    // 3. Password errors are distinguishable and user-readable.
    ExpectMessage(() => Knxproj.Load(fixture), "requires the project password", "missing password");
    try { Knxproj.Load(fixture); }
    catch (Exception ex) { Check(Knxproj.IsPasswordError(ex), "password error classification"); }
    ExpectMessage(() => Knxproj.Load(fixture, "not-the-password"), "Wrong project password", "wrong password");
    // 4. Protected save requires re-authentication and never silently downgrades to plaintext.
    var saveTarget = Path.Combine(directory, "copy.knxproj");
    ExpectMessage(() => Knxproj.Save(project, saveTarget), "requires the project password", "save of encrypted /21 project requires password");
    Check(!File.Exists(saveTarget), "refused save creates no destination");
    project.Name = "Protected edited project";
    var protectedDevice = ProjectQuery.DeviceList(project).First(); protectedDevice.ParameterValues["R-synthetic"] = "encrypted edit";
    Knxproj.Save(project, saveTarget, "test");
    Check(Knxproj.IsPasswordProtected(Knxproj.Load(saveTarget, "test")) && Knxproj.Load(saveTarget, "test").Name == "Protected edited project", "protected /21 save remains protected and editable");
    Check(ProjectQuery.DeviceList(Knxproj.Load(saveTarget, "test")).First().ParameterValues["R-synthetic"] == "encrypted edit", "encrypted native parameter edit roundtrips");
    ExpectMessage(() => Knxproj.Load(saveTarget), "requires the project password", "saved archive cannot be opened without its password");
    ExpectMessage(() => Knxproj.Load(saveTarget, "wrong"), "Wrong project password", "saved archive rejects a wrong password");
    var protectedBefore = File.ReadAllBytes(saveTarget);
    ExpectMessage(() => Knxproj.Save(project, saveTarget, "wrong"), "Wrong project password", "save authenticates the original archive before replacement");
    Check(File.ReadAllBytes(saveTarget).SequenceEqual(protectedBefore), "wrong save password leaves destination byte-identical");
    Check(File.ReadAllBytes(fixture).SequenceEqual(fixtureBytes), "fixture unchanged");

    // 5. Tampered authentication code of the synthetic fixture fails with the password message.
    var outer = Unzip(fixtureBytes);
    var inner = outer["P-0001.zip"];
    var innerOffset = LocalOffset(inner, "0.xml");
    var innerNameLength = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(innerOffset + 26, 2));
    var innerExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(innerOffset + 28, 2));
    var innerCompressed = (int)BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(innerOffset + 18, 4));
    inner[innerOffset + 30 + innerNameLength + innerExtraLength + innerCompressed - 1] ^= 0xFF;
    outer["P-0001.zip"] = inner;
    var tampered = Path.Combine(directory, "tampered.knxproj");
    File.WriteAllBytes(tampered, PlainZip(outer));
    ExpectMessage(() => Knxproj.Load(tampered, "test"), "Wrong project password", "tampered HMAC");

    // 6. Synthetic WinZip AES fixtures; an independent external archive tool is used for the optional audit below.
    var synthetic = Path.Combine(directory, "synthetic-aes1.knxproj");
    File.WriteAllBytes(synthetic, AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("AES synthetic")), ["project.xml"] = "<x/>"u8.ToArray() }, "secret"));
    var syntheticProject = Knxproj.Load(synthetic, "secret");
    Check(syntheticProject.Name == "AES synthetic" && syntheticProject.Id == "P-1", "synthetic AE-1 loads");
    ExpectMessage(() => Knxproj.Load(synthetic), "requires the project password", "synthetic missing password");
    ExpectMessage(() => Knxproj.Load(synthetic, "wrong"), "Wrong project password", "synthetic wrong password");
    ExpectMessage(() => Knxproj.Save(syntheticProject, Path.Combine(directory, "synthetic-copy.knxproj")), "requires the project password", "encrypted project save requires password");
    var syntheticCopy = Path.Combine(directory, "synthetic-copy.knxproj");
    syntheticProject.Name = "Re-encrypted root archive"; Knxproj.Save(syntheticProject, syntheticCopy, "secret");
    Check(Knxproj.Load(syntheticCopy, "secret").Name == "Re-encrypted root archive", "AES protected root /23 archive also saves");

    // Authenticated optional root master metadata shares the immutable part cache, not credentials.
    var masterBytes = Encoding.UTF8.GetBytes("""
        <KNX xmlns="http://knx.org/xml/project/20"><MasterData><MaskVersions>
        <MaskVersion Id="MV-07B0" MaskVersion="1968"><HawkConfigurationData><Features>
        <Feature Name="FirstAppObjectIdx" Value="6"/></Features></HawkConfigurationData></MaskVersion>
        </MaskVersions></MasterData></KNX>
        """);
    var masterArchive = AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("Cached master")), ["knx_master.xml"] = masterBytes }, "secret");
    var masterPath = Path.Combine(directory, "protected-master.knxproj"); File.WriteAllBytes(masterPath, masterArchive);
    var masterProject = Knxproj.Load(masterPath, "secret");
    var cachedMaster = ParameterProfileContext.Read(Knxproj.LoadMasterData(masterProject), "MV-07B0");
    Check(cachedMaster.Selection is { Status: MaskProfileStatus.Supported, FirstAppObjectIndex: 6 }
        && cachedMaster.MasterDataHash == Convert.ToHexStringLower(SHA256.HashData(masterBytes)),
        "authenticated root master loads with exact source bytes without another password");
    var mutableMaster = Knxproj.LoadMasterData(masterProject)!;
    mutableMaster.Descendants().Single(e => e.Name.LocalName == "Feature").SetAttributeValue("Value", "99");
    File.Delete(masterPath);
    Check(ParameterProfileContext.Read(Knxproj.LoadMasterData(masterProject), "MV-07B0").Selection == cachedMaster.Selection,
        "master cache is detached from caller XML mutation and the removed source file");
    var masterHistory = new UndoStack(); masterHistory.Reset(masterProject); masterProject.Name = "Edited cached master"; masterHistory.Push(masterProject);
    var masterUndo = masterHistory.Undo()!;
    Check(ParameterProfileContext.Read(Knxproj.LoadMasterData(masterUndo), "MV-07B0").Selection == cachedMaster.Selection,
        "undo preserves authenticated master metadata without credentials");
    var masterSaved = Path.Combine(directory, "protected-master-copy.knxproj");
    ExpectMessage(() => Knxproj.Save(masterProject, masterSaved), "requires the project password", "master cache does not bypass protected save authentication");
    Knxproj.Save(masterProject, masterSaved, "secret");
    var masterReloaded = Knxproj.Load(masterSaved, "secret");
    Check(Knxproj.IsPasswordProtected(masterReloaded) && masterReloaded.Name == "Edited cached master"
        && ParameterProfileContext.Read(Knxproj.LoadMasterData(masterReloaded), "MV-07B0").Selection == cachedMaster.Selection,
        "protected Open Save Reload keeps optional master accessible after each authenticated Open");
    Check(EncryptedArchive.ReadAll(File.ReadAllBytes(masterSaved), "secret")["knx_master.xml"].SequenceEqual(masterBytes)
        && LocalRecord(masterArchive, "knx_master.xml").SequenceEqual(LocalRecord(File.ReadAllBytes(masterSaved), "knx_master.xml")),
        "master caching changes neither original raw metadata nor its encrypted local record");
    Check(Knxproj.LoadMasterData(syntheticProject) is null, "an authenticated cache knows missing root master metadata without a password retry");
    foreach (var (label, data) in new[] { ("malformed", "<broken"u8.ToArray()),
        ("DTD", "<!DOCTYPE KNX [<!ELEMENT KNX ANY>]><KNX/>"u8.ToArray()), ("oversized", new byte[16 * 1024 * 1024 + 1]) })
    {
        var optionalPath = Path.Combine(directory, "optional-master-" + label + ".knxproj");
        File.WriteAllBytes(optionalPath, AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("Optional " + label)), ["knx_master.xml"] = data }, "secret"));
        var optionalProject = Knxproj.Load(optionalPath, "secret");
        var unavailable = false;
        try { unavailable = Knxproj.LoadMasterData(optionalProject) is null; }
        catch (System.Xml.XmlException) { unavailable = true; }
        optionalProject.Name = "Still editable " + label;
        Knxproj.Save(optionalProject, optionalPath, "secret");
        Check(unavailable && Knxproj.Load(optionalPath, "secret").Name == optionalProject.Name,
            "optional " + label + " master metadata cannot block authenticated project Open or edits");
    }

    var aes2 = Path.Combine(directory, "synthetic-aes2.knxproj");
    File.WriteAllBytes(aes2, AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("AE-2")) }, "secret", version: 2));
    Check(Knxproj.Load(aes2, "secret").Name == "AE-2", "synthetic AE-2 loads");
    var aes2Project = Knxproj.Load(aes2, "secret"); aes2Project.Name = "AE-2 edited";
    Knxproj.Save(aes2Project, aes2, "secret");
    Check(Knxproj.Load(aes2, "secret").Name == "AE-2 edited", "AE-2 replacement retains its protection");

    var crcData = "123456789"u8.ToArray();
    Check(FixtureCrc32(crcData) == 0xCBF43926u, "independent CRC fixture matches the standard check value");
    foreach (var strength in new byte[] { 1, 2, 3 })
    {
        var crcArchive = AesZip(new() { ["crc.bin"] = crcData }, "secret", 1, strength);
        var crcOriginal = crcArchive.ToArray();
        Check(EncryptedArchive.ReadAll(crcArchive, "secret")["crc.bin"].SequenceEqual(crcData),
            $"AE-1 strength {strength} accepts its correct plaintext CRC");
        var crcCentral = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(crcArchive.AsSpan(crcArchive.Length - 6, 4)));
        var badCentral = crcArchive.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badCentral.AsSpan(crcCentral + 16, 4), 0xCBF43927u);
        Check(LocalRecord(badCentral, "crc.bin").SequenceEqual(LocalRecord(crcArchive, "crc.bin")),
            $"AE-1 strength {strength} CRC mutation leaves ciphertext and authentication unchanged");
        ExpectMessage(() => EncryptedArchive.ReadAll(badCentral, "secret"), "CRC-32 mismatch",
            $"AE-1 strength {strength} rejects a wrong central CRC with a valid HMAC");
        var badBoth = badCentral.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(badBoth.AsSpan(14, 4), 0xCBF43927u);
        ExpectMessage(() => EncryptedArchive.ReadAll(badBoth, "secret"), "CRC-32 mismatch",
            $"AE-1 strength {strength} compares decompressed data rather than just matching headers");
        Check(crcArchive.SequenceEqual(crcOriginal), $"AE-1 strength {strength} reads leave source bytes unchanged");
    }
    Check(EncryptedArchive.ReadAll(AesZip(new() { ["empty.bin"] = [] }, "secret"), "secret")["empty.bin"].Length == 0,
        "AE-1 accepts the genuine zero CRC of empty content");
    Check(EncryptedArchive.ReadAll(AesZip(new() { ["crc.bin"] = crcData }, "secret", 2), "secret")["crc.bin"].SequenceEqual(crcData),
        "AE-2 accepts nonempty content with its specified zero CRC");
    var descriptorArchive = WithDataDescriptor(AesZip(new() { ["crc.bin"] = crcData }, "secret"));
    var descriptorOriginal = descriptorArchive.ToArray();
    Check(EncryptedArchive.ReadAll(descriptorArchive, "secret")["crc.bin"].SequenceEqual(crcData),
        "streamed AE-1 accepts zero local CRC and sizes with a valid data descriptor");
    var descriptorCentral = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptorArchive.AsSpan(descriptorArchive.Length - 6, 4)));
    var descriptorBadCrc = descriptorArchive.ToArray();
    BinaryPrimitives.WriteUInt32LittleEndian(descriptorBadCrc.AsSpan(descriptorCentral + 16, 4), 0xCBF43927u);
    ExpectMessage(() => EncryptedArchive.ReadAll(descriptorBadCrc, "secret"), "CRC-32 mismatch",
        "streamed AE-1 still checks the central CRC after decryption and decompression");
    var badAuthentication = descriptorBadCrc.ToArray();
    badAuthentication[descriptorCentral - 17] ^= 1; // Last MAC byte, immediately before the 16-byte descriptor.
    ExpectMessage(() => EncryptedArchive.ReadAll(badAuthentication, "secret"), "Wrong project password",
        "authentication failure takes precedence over an AE-1 CRC mismatch");
    Check(descriptorArchive.SequenceEqual(descriptorOriginal), "streamed AE-1 reads leave source bytes unchanged");

    foreach (var version in new ushort[] { 1, 2 })
        foreach (var strength in new byte[] { 1, 2, 3 })
        {
            var matrixPath = Path.Combine(directory, $"aes-{version}-{strength}.knxproj");
            var opaque = new byte[] { 0, 17, 255, 1, 42, 0 };
            var matrixBytes = AesZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("Before")), ["opaque/数据.bin"] = opaque }, "test", version, strength);
            File.WriteAllBytes(matrixPath, matrixBytes);
            var matrixProject = Knxproj.Load(matrixPath, "test"); matrixProject.Name = "After";
            Knxproj.Save(matrixProject, matrixPath, "test");
            var savedMatrix = File.ReadAllBytes(matrixPath);
            Check(Knxproj.Load(matrixPath, "test").Name == "After", $"AES version {version} strength {strength} edit roundtrip");
            Check(EncryptedArchive.ReadAll(savedMatrix, "test")["opaque/数据.bin"].SequenceEqual(opaque), "opaque encrypted content remains readable");
            Check(LocalRecord(matrixBytes, "opaque/数据.bin").SequenceEqual(LocalRecord(savedMatrix, "opaque/数据.bin")), "unchanged encrypted local record is byte-identical");
            if (Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_EXPORT_AUDIT") is { Length: > 0 } matrixAudit)
            {
                Directory.CreateDirectory(matrixAudit); File.Copy(matrixPath, Path.Combine(matrixAudit, $"aes-{version}-{strength}.zip"), true);
            }
        }

    var history = new UndoStack(); history.Reset(project); project.Name = "Before history edit"; history.Push(project);
    var historyRestored = history.Undo()!;
    Check(historyRestored.Name == "Protected edited project" && Knxproj.IsPasswordProtected(historyRestored), "native metadata and protection survive undo");
    var historyPath = Path.Combine(directory, "history.knxproj"); Knxproj.Save(historyRestored, historyPath, "test");
    Check(Knxproj.Load(historyPath, "test").Name == "Protected edited project", "native protected archive saves after undo");

    // Native instance records must change with Count; dormant references remain recoverable outside native 0.xml.
    var nativeModule = XDocument.Parse(Synthetic21Project());
    XNamespace n21 = "http://knx.org/xml/project/21";
    const string moduleApplicationId = "M-0001_A-native";
    const string moduleObjectId = "MD-1_M-1_MI-2_O-1_R-1";
    const string moduleParameterId = moduleApplicationId + "_MD-1_M-1_MI-2_P-1_R-1";
    var nativeModuleDevice = nativeModule.Descendants(n21 + "DeviceInstance").Single(d => (string?)d.Attribute("Address") == "5");
    nativeModuleDevice.SetAttributeValue("ProductRefId", moduleApplicationId); nativeModuleDevice.Attribute("Hardware2ProgramRefId")!.Remove();
    nativeModuleDevice.Element(n21 + "ComObjectInstanceRefs")!.ReplaceWith(new XElement(n21 + "ComObjectInstanceRefs", new XComment("opaque object container"),
        new XElement(n21 + "ComObjectInstanceRef", new XAttribute("RefId", moduleObjectId), new XAttribute("Text", "Explicit module object"), new XAttribute("Links", "GA-1"))));
    nativeModuleDevice.AddFirst(new XElement(n21 + "ParameterInstanceRefs",
        new XElement(n21 + "ParameterInstanceRef", new XAttribute("RefId", "R-count"), new XAttribute("Value", "2")),
        new XElement(n21 + "ParameterInstanceRef", new XAttribute("RefId", moduleParameterId), new XAttribute("Value", "9"))));
    nativeModuleDevice.Add(new XElement(n21 + "ModuleInstances", new XComment("opaque module container"), Enumerable.Range(1, 2).Select(i => new XElement(n21 + "ModuleInstance",
        new XAttribute("Id", "MD-1_M-1_MI-" + i), new XAttribute("RefId", "MD-1_M-1"), new XAttribute("RepeatIndex", $"2x{i} 3x1"),
        new XElement(n21 + "Arguments", new XComment("opaque arguments"), new XElement(n21 + "Argument", new XAttribute("RefId", "MD-1_A-1"), new XAttribute("Value", "L-1"))),
        new XComment("opaque module payload " + i)))));
    var moduleXml = """
        <KNX><ApplicationProgram Id="M-0001_A-native"><Static><ParameterTypes><ParameterType Id="T"><TypeNumber Type="unsignedInt" SizeInBit="8" /></ParameterType></ParameterTypes>
        <Parameters><Parameter Id="P-count" ParameterType="T" Value="2" /></Parameters><ParameterRefs><ParameterRef Id="R-count" RefId="P-count" /></ParameterRefs>
        <Allocators><Allocator Id="M-0001_A-native_L-1" Start="3" maxInclusive="100" /></Allocators>
        <ModuleDefs><ModuleDef Id="M-0001_A-native_MD-1"><Arguments><Argument Id="M-0001_A-native_MD-1_A-1" Allocates="2" Alignment="1" /></Arguments><Static>
        <Parameters><Parameter Id="M-0001_A-native_MD-1_P-1" ParameterType="T" Value="1" /></Parameters><ParameterRefs><ParameterRef Id="M-0001_A-native_MD-1_P-1_R-1" RefId="M-0001_A-native_MD-1_P-1" /></ParameterRefs>
        <ComObjects><ComObject Id="M-0001_A-native_MD-1_O-1" Number="0" BaseNumber="M-0001_A-native_MD-1_A-1" Text="Module output" ObjectSize="1 Bit" /></ComObjects><ComObjectRefs><ComObjectRef Id="M-0001_A-native_MD-1_O-1_R-1" RefId="M-0001_A-native_MD-1_O-1" /></ComObjectRefs></Static>
        <Dynamic><ParameterRefRef RefId="M-0001_A-native_MD-1_P-1_R-1" /><ComObjectRefRef RefId="M-0001_A-native_MD-1_O-1_R-1" /></Dynamic></ModuleDef></ModuleDefs></Static>
        <Dynamic><ParameterRefRef RefId="R-count" /><Repeat Id="REP" Count="0" ParameterRefId="R-count"><Module Id="M-0001_A-native_MD-1_M-1" RefId="M-0001_A-native_MD-1"><NumericArg RefId="M-0001_A-native_MD-1_A-1" AllocatorRefId="M-0001_A-native_L-1" /></Module></Repeat></Dynamic>
        </ApplicationProgram></KNX>
        """;
    var nativeModulePath = Path.Combine(directory, "native-module.knxproj");
    File.WriteAllBytes(nativeModulePath, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(nativeModule.ToString()),
        ["project.xml"] = Encoding.UTF8.GetBytes(Synthetic21Information()), ["M-0001/M-0001_A-native.xml"] = Encoding.UTF8.GetBytes(moduleXml), ["opaque.bin"] = [0, 255, 17] }));
    // A changed application must not reuse native instance records just because both saved value maps are empty.
    const string switchedApplicationId = "M-0002_A-switched";
    var switchBase = new XDocument(nativeModule);
    var switchBaseDevice = switchBase.Descendants(n21 + "DeviceInstance").Single(d => (string?)d.Attribute("ProductRefId") == moduleApplicationId);
    switchBaseDevice.Element(n21 + "ParameterInstanceRefs")!.Remove(); switchBaseDevice.Element(n21 + "ComObjectInstanceRefs")!.Remove();
    var switchPath = Path.Combine(directory, "native-switch.knxproj");
    File.WriteAllBytes(switchPath, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(switchBase.ToString()),
        ["project.xml"] = Encoding.UTF8.GetBytes(Synthetic21Information()), ["M-0001/M-0001_A-native.xml"] = Encoding.UTF8.GetBytes(moduleXml),
        ["M-0002/M-0002_A-switched.xml"] = Encoding.UTF8.GetBytes(moduleXml.Replace(moduleApplicationId, switchedApplicationId, StringComparison.Ordinal).Replace("_MD-1", "_MD-9", StringComparison.Ordinal)) }));
    foreach (var switchProgram in new[] { false, true })
    {
        var switched = Knxproj.Load(switchPath); var switchedDevice = ProjectQuery.DeviceList(switched).Single(d => d.ProductRefId == moduleApplicationId);
        if (switchProgram) switchedDevice.Hardware2ProgramRefId = switchedApplicationId; else switchedDevice.ProductRefId = switchedApplicationId;
        var switchedOutput = Path.Combine(directory, "native-switched-" + switchProgram + ".knxproj"); Knxproj.Save(switched, switchedOutput);
        using var switchedZip = ZipFile.OpenRead(switchedOutput); using var switchedSource = switchedZip.GetEntry("0.xml")!.Open();
        var switchedInstances = XDocument.Load(switchedSource).Descendants(n21 + "ModuleInstance").ToArray();
        Check(switchedInstances.Length == 2 && switchedInstances.All(e => (string?)e.Attribute("RefId") == "MD-9_M-1"),
            "native module records refresh after a " + (switchProgram ? "program" : "product") + " reference change with unchanged empty saved values");
    }
    const string argumentTarget = "M-0002_A-arguments";
    string ArgumentFixture(string suffix, bool newArgumentId = false, bool opaque = false, bool overrideValue = false, bool noTargetModules = false)
    {
        var sourceApplication = XDocument.Parse(moduleXml);
        sourceApplication.Descendants("Parameter").Single(p => (string?)p.Attribute("Id") == "P-count").SetAttributeValue("Value", "1");
        sourceApplication.Descendants("ComObject").Single().SetAttributeValue("DatapointType", "DPST-1-1");
        var targetApplication = XDocument.Parse(sourceApplication.ToString().Replace(moduleApplicationId, argumentTarget, StringComparison.Ordinal));
        var numeric = targetApplication.Descendants("NumericArg").Single(); numeric.Attribute("AllocatorRefId")!.Remove(); numeric.SetAttributeValue("Value", "5");
        if (newArgumentId)
            foreach (var attribute in targetApplication.Descendants().Attributes())
                attribute.Value = attribute.Value.Replace("_MD-1_A-1", "_MD-1_A-2", StringComparison.Ordinal);
        if (noTargetModules) { targetApplication.Descendants("ModuleDefs").Remove(); targetApplication.Descendants("Repeat").Remove(); }
        var sourceData = new XDocument(switchBase);
        sourceData.Descendants(n21 + "ModuleInstance").Where(e => (string?)e.Attribute("Id") == "MD-1_M-1_MI-2").Remove();
        var argument = sourceData.Descendants(n21 + "Argument").Single();
        if (opaque) argument.SetAttributeValue(XName.Get("State", "urn:synthetic-vendor"), "Unmapped opaque argument state");
        if (overrideValue) argument.SetAttributeValue("Value", "Unknown allocator override");
        var sidecar = new XDocument(new XElement(XName.Get("Project", "urn:buschord:project:1"), new XAttribute("Version", 1), new XAttribute("Id", "P-0001"),
            new XElement(XName.Get("RetiredReferenceContainer", "urn:buschord:project:1"), new XAttribute("DeviceId", (string)switchBaseDevice.Attribute("Id")!),
                new XElement(n21 + "ComObjectInstanceRefs", new XComment("Source application reference comment")))));
        var fixturePath = Path.Combine(directory, "native-arguments-" + suffix + ".knxproj");
        File.WriteAllBytes(fixturePath, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(sourceData.ToString()),
            ["project.xml"] = Encoding.UTF8.GetBytes(Synthetic21Information()), ["M-0001/M-0001_A-native.xml"] = Encoding.UTF8.GetBytes(sourceApplication.ToString()),
            ["M-0002/M-0002_A-arguments.xml"] = Encoding.UTF8.GetBytes(targetApplication.ToString()), ["BusChord/project.xml"] = Encoding.UTF8.GetBytes(sidecar.ToString()) }));
        return fixturePath;
    }
    XDocument Part(string archivePath, string part)
    { using var archive = ZipFile.OpenRead(archivePath); using var input = archive.GetEntry(part)!.Open(); return XDocument.Load(input); }
    foreach (var newArgumentId in new[] { false, true })
    {
        var argumentPath = ArgumentFixture(newArgumentId.ToString(), newArgumentId);
        var argumentProject = Knxproj.Load(argumentPath); var argumentDevice = ProjectQuery.DeviceList(argumentProject).Single(d => d.ProductRefId == moduleApplicationId);
        var plan = ProductReplacer.Preview(argumentProject, argumentDevice, argumentTarget, argumentTarget,
            Knxproj.LoadApplicationProgram(argumentProject, moduleApplicationId)!, Knxproj.LoadApplicationProgram(argumentProject, argumentTarget)!);
        _ = ProductReplacer.Apply(argumentProject, plan); Knxproj.Save(argumentProject, argumentPath);
        var targetXml = Part(argumentPath, "0.xml"); var targetArguments = targetXml.Descendants(n21 + "Argument").ToArray();
        var expectedArgument = newArgumentId ? "MD-1_A-2" : "MD-1_A-1";
        Check(targetArguments.Length == 1 && (string?)targetArguments[0].Attribute("RefId") == expectedArgument && (string?)targetArguments[0].Attribute("Value") == "5",
            "new application emits fresh literal arguments with " + (newArgumentId ? "new" : "identical") + " relative argument identity");
        XNamespace bc = "urn:buschord:project:1";
        var argumentSidecar = Part(argumentPath, "BusChord/project.xml");
        Check((int?)argumentSidecar.Root!.Attribute("Version") == 2
            && argumentSidecar.Descendants(bc + "RetiredModule").Any(e => (string?)e.Attribute("ApplicationId") == moduleApplicationId && (bool?)e.Attribute("Historical") == true)
            && argumentSidecar.DescendantNodes().OfType<XComment>().Any(c => c.Value == "Source application reference comment")
            && !targetXml.DescendantNodes().OfType<XComment>().Any(c => c.Value.StartsWith("Source application", StringComparison.Ordinal) || c.Value.StartsWith("opaque module", StringComparison.Ordinal)),
            "source module/reference payload stays application-scoped history outside the target native data");
        argumentProject = Knxproj.Load(argumentPath); argumentDevice = ProjectQuery.DeviceList(argumentProject).Single(d => d.ProductRefId == argumentTarget);
        argumentDevice.ParameterValues["R-count"] = "0"; Knxproj.Save(argumentProject, argumentPath);
        Check(!Part(argumentPath, "0.xml").Descendants(n21 + "ModuleInstances").Any(), "target Count zero does not revive source module history");
        argumentProject = Knxproj.Load(argumentPath); argumentDevice = ProjectQuery.DeviceList(argumentProject).Single(d => d.ProductRefId == argumentTarget);
        argumentDevice.ParameterValues["R-count"] = "1"; Knxproj.Save(argumentProject, argumentPath);
        var regrownArguments = Part(argumentPath, "0.xml").Descendants(n21 + "Argument").ToArray();
        Check(regrownArguments.Length == 1 && (string?)regrownArguments[0].Attribute("RefId") == expectedArgument && (string?)regrownArguments[0].Attribute("Value") == "5",
            "target Count regrowth retains target arguments without rehydrating source values or references");
    }
    var noModulesPath = ArgumentFixture("without-modules", noTargetModules: true);
    var noModulesProject = Knxproj.Load(noModulesPath); var noModulesDevice = ProjectQuery.DeviceList(noModulesProject).Single(d => d.ProductRefId == moduleApplicationId);
    var noModulesPlan = ProductReplacer.Preview(noModulesProject, noModulesDevice, argumentTarget, argumentTarget,
        Knxproj.LoadApplicationProgram(noModulesProject, moduleApplicationId)!, Knxproj.LoadApplicationProgram(noModulesProject, argumentTarget)!);
    _ = ProductReplacer.Apply(noModulesProject, noModulesPlan); Knxproj.Save(noModulesProject, noModulesPath);
    Check(!Part(noModulesPath, "0.xml").Descendants(n21 + "ModuleInstances").Any(), "migration to a module-free application removes source native instance records");
    var opaquePath = ArgumentFixture("opaque", opaque: true); var opaqueProject = Knxproj.Load(opaquePath);
    var opaqueDevice = ProjectQuery.DeviceList(opaqueProject).Single(d => d.ProductRefId == moduleApplicationId); var opaqueBefore = Knxproj.ToXml(opaqueProject).ToString();
    ExpectMessage(() => ProductReplacer.Preview(opaqueProject, opaqueDevice, argumentTarget, argumentTarget,
        Knxproj.LoadApplicationProgram(opaqueProject, moduleApplicationId)!, Knxproj.LoadApplicationProgram(opaqueProject, argumentTarget)!),
        "unmapped native XML payload", "Core preview rejects unmapped opaque module argument payload before Apply");
    Check(Knxproj.ToXml(opaqueProject).ToString() == opaqueBefore, "opaque-source migration preview leaves the project unchanged");
    var opaqueTarget = Path.Combine(directory, "opaque-destination.knxproj"); var sentinel = "destination stays unchanged"u8.ToArray(); File.WriteAllBytes(opaqueTarget, sentinel);
    opaqueDevice.Hardware2ProgramRefId = argumentTarget;
    ExpectMessage(() => Knxproj.Save(opaqueProject, opaqueTarget), "unmapped native XML payload", "native save also rejects unmapped source payload before replacement");
    Check(File.ReadAllBytes(opaqueTarget).SequenceEqual(sentinel), "opaque source refusal preserves the existing destination byte-for-byte");
    var overridePath = ArgumentFixture("override", overrideValue: true); var overrideProject = Knxproj.Load(overridePath);
    var overrideDevice = ProjectQuery.DeviceList(overrideProject).Single(d => d.ProductRefId == moduleApplicationId);
    ExpectMessage(() => ProductReplacer.Preview(overrideProject, overrideDevice, argumentTarget, argumentTarget,
        Knxproj.LoadApplicationProgram(overrideProject, moduleApplicationId)!, Knxproj.LoadApplicationProgram(overrideProject, argumentTarget)!),
        "override the evaluated source application", "source argument overrides require proven migration instead of being guessed");
    var protectedSnapshotPath = Path.Combine(directory, "protected-manufacturer.knxproj");
    File.WriteAllBytes(protectedSnapshotPath, AesZip(Unzip(File.ReadAllBytes(nativeModulePath)), "test"));
    var protectedSnapshot = Knxproj.Load(protectedSnapshotPath, "test"); File.Delete(protectedSnapshotPath);
    Check(Knxproj.LoadApplicationProgram(protectedSnapshot, moduleApplicationId)?.Descendants().Any(e => e.Name.LocalName == "ApplicationProgram") == true,
        "authenticated encrypted manufacturer parts remain available without a retained password or source file");
    var moduleProject = Knxproj.Load(nativeModulePath);
    Check(Knxproj.LoadApplicationProgram(moduleProject, moduleApplicationId)?.Descendants().Any(e => e.Name.LocalName == "ApplicationProgram") == true,
        "manufacturer application lookup works from the immutable project snapshot");
    var moduleDevice = ProjectQuery.DeviceList(moduleProject).Single(d => d.ProductRefId == moduleApplicationId);
    moduleDevice.ParameterValues["R-count"] = "1"; Knxproj.Save(moduleProject, nativeModulePath);
    using (var archive = ZipFile.OpenRead(nativeModulePath))
    using (var source = archive.GetEntry("0.xml")!.Open())
    {
        var savedXml = XDocument.Load(source);
        Check(savedXml.Descendants(n21 + "ModuleInstance").Select(e => (string?)e.Attribute("Id")).SequenceEqual(new[] { "MD-1_M-1_MI-1" }), "native Count reduction synchronizes module instance records");
        Check(!savedXml.Descendants(n21 + "ComObjectInstanceRef").Any(e => (string?)e.Attribute("RefId") == moduleObjectId)
            && !savedXml.Descendants(n21 + "ParameterInstanceRef").Any(e => (string?)e.Attribute("RefId") == moduleParameterId), "dormant module references are absent from native 0.xml");
    }
    moduleProject = Knxproj.Load(nativeModulePath); moduleDevice = ProjectQuery.DeviceList(moduleProject).Single(d => d.ProductRefId == moduleApplicationId);
    Check(moduleDevice.ParameterValues[moduleParameterId] == "9" && moduleDevice.ComObjectRefs.Single().Links.Count == 1
        && moduleDevice.ComObjectRefs.Single().Text == "Explicit module object", "dormant module values, links and overrides roundtrip through sidecar");
    Check(!moduleProject.Workbench.Objects[new Knx.Workbench.ObjectKey(moduleDevice.Id, moduleObjectId).ToString()].Active, "dormant imported object stays inactive");
    moduleDevice.ParameterValues["R-count"] = "0"; Knxproj.Save(moduleProject, nativeModulePath);
    using (var archive = ZipFile.OpenRead(nativeModulePath))
    using (var source = archive.GetEntry("0.xml")!.Open())
        Check(!XDocument.Load(source).Descendants(n21 + "ModuleInstances").Any(), "native Count zero removes the schema-required nonempty module container");
    if (Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_EXPORT_AUDIT") is { Length: > 0 } zeroAudit)
    { Directory.CreateDirectory(zeroAudit); File.Copy(nativeModulePath, Path.Combine(zeroAudit, "native-modules-zero.knxproj"), true); }
    moduleProject = Knxproj.Load(nativeModulePath); moduleDevice = ProjectQuery.DeviceList(moduleProject).Single(d => d.ProductRefId == moduleApplicationId);
    moduleDevice.ParameterValues["R-count"] = "3"; Knxproj.Save(moduleProject, nativeModulePath);
    using (var archive = ZipFile.OpenRead(nativeModulePath))
    using (var source = archive.GetEntry("0.xml")!.Open())
    {
        var savedXml = XDocument.Load(source);
        var instances = savedXml.Descendants(n21 + "ModuleInstance").ToList();
        Check(instances.Count == 3 && (string?)instances[2].Attribute("RepeatIndex") == "2x3 3x1", "native regrowth writes ordered identities and current RepeatIndex");
        Check(instances[1].DescendantNodes().OfType<XComment>().Any(c => c.Value == "opaque module payload 2"), "regrowth restores opaque native module payload");
        Check(savedXml.DescendantNodes().OfType<XComment>().Any(c => c.Value == "opaque module container")
            && instances[1].DescendantNodes().OfType<XComment>().Any(c => c.Value == "opaque arguments")
            && savedXml.DescendantNodes().OfType<XComment>().Any(c => c.Value == "opaque object container"), "native module, argument and reference containers preserve opaque nodes");
        Check(instances.All(i => (string?)i.Element(n21 + "Arguments")!.Element(n21 + "Argument")!.Attribute("Value") == "L-1"), "native module arguments retain allocator identifiers instead of computed numbers");
    }
    var regrown = Knxproj.Load(nativeModulePath);
    Check(regrown.Workbench.Objects[new Knx.Workbench.ObjectKey(moduleDevice.Id, moduleObjectId).ToString()].Active
        && ProjectQuery.DeviceList(regrown).Single(d => d.Id == moduleDevice.Id).ComObjectRefs.Single().Number == 5, "native regrowth restores activity and current inherited numbers");
    if (Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_EXPORT_AUDIT") is { Length: > 0 } nativeAudit)
    { Directory.CreateDirectory(nativeAudit); File.Copy(nativeModulePath, Path.Combine(nativeAudit, "native-modules.knxproj"), true); }

    if (Environment.GetEnvironmentVariable("BUSCHORD_NATIVE_EXPORT_AUDIT") is { Length: > 0 } auditDirectory)
    {
        Directory.CreateDirectory(auditDirectory);
        var binaryInnerZip = Unzip(File.ReadAllBytes(binaryOutput))["P-0001.zip"];
        File.WriteAllBytes(Path.Combine(auditDirectory, "binary21.zip"), binaryInnerZip);
        var binaryInner = EncryptedArchive.ReadAll(binaryInnerZip, "test");
        File.WriteAllBytes(Path.Combine(auditDirectory, "binary21-0.xml"), binaryInner["0.xml"]);
        File.WriteAllBytes(Path.Combine(auditDirectory, "binary21-project.xml"), binaryInner["project.xml"]);
        File.Copy(plain21Copy, Path.Combine(auditDirectory, "plain21.knxproj"), true);
        File.WriteAllBytes(Path.Combine(auditDirectory, "protected21.zip"), Unzip(File.ReadAllBytes(saveTarget))["P-0001.zip"]);
        File.Copy(syntheticCopy, Path.Combine(auditDirectory, "protected23.zip"), true);
        File.Copy(aes2, Path.Combine(auditDirectory, "aes2.zip"), true);
    }

    // 7. Plain archives behave as before, with or without a password argument.
    var plain = Path.Combine(directory, "plain.knxproj");
    File.WriteAllBytes(plain, PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("Plain")) }));
    Check(Knxproj.Load(plain).Name == "Plain", "plain archive loads");
    Check(Knxproj.Load(plain, "ignored").Name == "Plain", "plain archive ignores password");

    // 8. Unsupported encryption is named, not misreported as a wrong password.
    var zipCrypto = Path.Combine(directory, "zipcrypto.knxproj");
    var zipCryptoBytes = PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("ZipCrypto")) });
    FlagAsEncrypted(zipCryptoBytes, method: 8);
    File.WriteAllBytes(zipCrypto, zipCryptoBytes);
    ExpectMessage(() => Knxproj.Load(zipCrypto, "anything"), "ZipCrypto", "ZipCrypto rejected");

    var noAesExtra = Path.Combine(directory, "no-aes-extra.knxproj");
    var noAesExtraBytes = PlainZip(new() { ["0.xml"] = Encoding.UTF8.GetBytes(MinimalXml("NoAes")) });
    FlagAsEncrypted(noAesExtraBytes, method: 99);
    File.WriteAllBytes(noAesExtra, noAesExtraBytes);
    ExpectMessage(() => Knxproj.Load(noAesExtra, "anything"), "AES extra field", "missing AES extra field rejected");

    Native20EncryptionChecks.Run(directory, Check, AesZip);
    Native22EncryptionChecks.Run(directory, Check, AesZip);
    Console.WriteLine($"ALL {checks} ENCRYPTION CHECKS PASSED");
}
finally
{
    var ownedDirectory = Path.GetFullPath(directory);
    var ownedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode"));
    var leaf = Path.GetFileName(ownedDirectory);
    const string prefix = "buschord-encryption-";
    if (!string.Equals(Path.GetDirectoryName(ownedDirectory), ownedParent, StringComparison.OrdinalIgnoreCase)
        || !leaf.StartsWith(prefix, StringComparison.Ordinal)
        || !Guid.TryParseExact(leaf[prefix.Length..], "N", out _))
        throw new InvalidOperationException("The encryption test cleanup path is outside its owned temporary directory.");
    if (Directory.Exists(ownedDirectory)) Directory.Delete(ownedDirectory, recursive: true);
}

void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++;
}

void ExpectMessage(Action action, string fragment, string name)
{
    try { action(); }
    catch (InvalidDataException e) when (e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase)) { checks++; return; }
    throw new Exception("FAIL: expected '" + fragment + "': " + name);
}

static string MinimalXml(string name) =>
    "<KNX xmlns=\"http://knx.org/xml/project/23\"><Project Id=\"P-1\" Name=\"" + name + "\"><Installations/></Project></KNX>";

// Synthetic /21 project: 2 areas, 3 devices on 1.1.0/1.1.5/1.1.6, 3 group
// addresses, 8 communication objects on the second device. No security data.
static string Synthetic21Project() => """
<?xml version="1.0" encoding="utf-8"?>
<KNX xmlns="http://knx.org/xml/project/21">
  <Project Id="P-0001">
    <Installations>
      <Installation Name="" BCUKey="0" DefaultLine="P-0001-0_L-3" IPRoutingLatencyTolerance="2000">
        <Topology>
          <Area Id="P-0001-0_A-1" Address="0" Puid="1">
            <Line Id="P-0001-0_L-1" Address="0" Puid="2">
              <Segment Id="P-0001-0_S-1" Number="0" MediumTypeRefId="MT-5" Puid="3" />
            </Line>
          </Area>
          <Area Id="P-0001-0_A-2" Address="1" Puid="4">
            <Line Id="P-0001-0_L-2" Address="0" Puid="5">
              <Segment Id="P-0001-0_S-2" Number="0" MediumTypeRefId="MT-5" Puid="6" />
            </Line>
            <Line Id="P-0001-0_L-3" Address="1" Puid="7">
              <Segment Id="P-0001-0_S-3" Number="0" MediumTypeRefId="MT-0" Puid="8">
                <DeviceInstance Id="P-0001-0_DI-1" Address="0" Name="Demo actuator" ProductRefId="M-9001_H-1_P-1" Hardware2ProgramRefId="M-9001_H-1_HP-1" Puid="14" />
                <DeviceInstance Id="P-0001-0_DI-2" Address="5" Name="Demo push button" ProductRefId="M-9002_H-2_P-2" Hardware2ProgramRefId="M-9002_H-2_HP-2" Puid="20">
                  <ComObjectInstanceRefs>
                    <ComObjectInstanceRef RefId="O-1_R-1" Text="Output 1" Links="GA-1" />
                    <ComObjectInstanceRef RefId="O-2_R-2" Text="Input 1" Links="GA-2" />
                    <ComObjectInstanceRef RefId="O-3_R-3" Text="Output 2" />
                    <ComObjectInstanceRef RefId="O-4_R-4" Text="Input 2" />
                    <ComObjectInstanceRef RefId="O-5_R-5" Text="Output 3" />
                    <ComObjectInstanceRef RefId="O-6_R-6" Text="Input 3" />
                    <ComObjectInstanceRef RefId="O-7_R-7" Text="Output 4" />
                    <ComObjectInstanceRef RefId="O-8_R-8" Text="Input 4" />
                  </ComObjectInstanceRefs>
                </DeviceInstance>
                <DeviceInstance Id="P-0001-0_DI-3" Address="6" Name="Demo sensor" ProductRefId="M-9003_H-3_P-3" Hardware2ProgramRefId="M-9003_H-3_HP-3" Puid="30" />
              </Segment>
            </Line>
          </Area>
        </Topology>
        <Locations>
          <Space Type="Building" Id="P-0001-0_BP-1" Name="Synthetic building" Puid="9">
            <Space Type="Floor" Id="P-0001-0_BP-2" Name="Ground floor" Puid="10">
              <Space Type="Room" Id="P-0001-0_BP-3" Name="Room A" Puid="11">
                <DeviceInstanceRef RefId="P-0001-0_DI-2" />
              </Space>
            </Space>
            <Space Type="Room" Id="P-0001-0_BP-4" Name="Utility" Puid="13">
              <DeviceInstanceRef RefId="P-0001-0_DI-1" />
            </Space>
          </Space>
        </Locations>
        <GroupAddresses>
          <GroupRanges>
            <GroupRange Id="P-0001-0_GR-1" RangeStart="1" RangeEnd="2047" Name="Main">
              <GroupRange Id="P-0001-0_GR-2" RangeStart="256" RangeEnd="511" Name="Middle">
                <GroupAddress Id="P-0001-0_GA-1" Address="256" Name="Switch" DatapointType="DPST-1-1" Puid="27" />
                <GroupAddress Id="P-0001-0_GA-2" Address="257" Name="Status" DatapointType="DPST-1-1" Puid="28" />
                <GroupAddress Id="P-0001-0_GA-3" Address="258" Name="Spare" Puid="29" />
              </GroupRange>
            </GroupRange>
          </GroupRanges>
        </GroupAddresses>
      </Installation>
    </Installations>
  </Project>
</KNX>
""";

static string Synthetic21Information() => """
<?xml version="1.0" encoding="utf-8"?>
<KNX xmlns="http://knx.org/xml/project/21">
  <Project Id="P-0001">
    <ProjectInformation Name="Synthetic project" GroupAddressStyle="ThreeLevel" ProjectStart="2026-01-01T00:00:00Z" Guid="11111111-2222-3333-4444-555555555555" />
  </Project>
</KNX>
""";

static IEnumerable<GroupRange> FlattenRanges(GroupRange range) =>
    new[] { range }.Concat(range.Ranges.SelectMany(FlattenRanges));

static Dictionary<string, byte[]> Unzip(byte[] bytes)
{
    using var buffer = new MemoryStream(bytes);
    using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
    return zip.Entries.ToDictionary(e => e.FullName, e =>
    {
        using var stream = e.Open();
        using var data = new MemoryStream();
        stream.CopyTo(data);
        return data.ToArray();
    });
}

static byte[] PlainZip(Dictionary<string, byte[]> entries)
{
    using var buffer = new MemoryStream();
    using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        foreach (var (name, bytes) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(bytes);
        }
    return buffer.ToArray();
}

// Local header offset of one entry, read from the central directory.
static int LocalOffset(byte[] zip, string name)
{
    var target = Encoding.UTF8.GetBytes(name);
    for (int i = 0; i + 46 < zip.Length; i++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i, 4)) == 0x02014b50 &&
            BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 28, 2)) == target.Length &&
            zip.AsSpan(i + 46, target.Length).SequenceEqual(target))
            return (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i + 42, 4));
    throw new Exception("entry not found: " + name);
}

// Marks every entry as encrypted with the given method, without writing any encrypted data.
static void FlagAsEncrypted(byte[] zip, ushort method)
{
    for (int i = 0; i + 46 < zip.Length; i++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(i, 4)) == 0x02014b50)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 8, 2), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(i + 8, 2)) | 1));
            BinaryPrimitives.WriteUInt16LittleEndian(zip.AsSpan(i + 10, 2), method);
        }
}

// Synthetic WinZip AES archive (write direction, AE-1/AE-2, AES-256, Deflate).
static byte[] AesZip(Dictionary<string, byte[]> entries, string userPassword, ushort version = 1, byte strength = 3)
{
    var keyLength = strength switch { 1 => 16, 2 => 24, 3 => 32, _ => throw new ArgumentOutOfRangeException(nameof(strength)) };
    var saltLength = keyLength / 2;
    // The fixture uses the explicitly selected synthetic protection profile.
    var zipPassword = EncryptedArchive.DeriveZipPassword(userPassword);
    using var output = new MemoryStream();
    using var central = new MemoryStream();
    foreach (var (name, data) in entries)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true)) deflate.Write(data);
        var deflateBytes = compressed.ToArray();
        var salt = RandomNumberGenerator.GetBytes(saltLength);
        var derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(zipPassword), salt, 1000, HashAlgorithmName.SHA1, 2 * keyLength + 2);
        var cipher = AesCtr(derived[..keyLength], deflateBytes);
        var mac = HMACSHA1.HashData(derived[keyLength..(2 * keyLength)], cipher).AsSpan(0, 10).ToArray();
        var extra = new byte[] { 0x01, 0x99, 0x07, 0x00, (byte)version, 0x00, (byte)'A', (byte)'E', strength, 0x08, 0x00 };
        var body = salt.Concat(derived[(2 * keyLength)..]).Concat(cipher).Concat(mac).ToArray();
        var crc = version == 2 ? 0 : FixtureCrc32(data);

        var localOffset = (uint)output.Length;
        WriteU32(output, 0x04034b50); WriteU16(output, 20); WriteU16(output, 0x0801); WriteU16(output, 99);
        WriteU16(output, 0); WriteU16(output, 0x21); WriteU32(output, crc); WriteU32(output, (uint)body.Length);
        WriteU32(output, (uint)data.Length); WriteU16(output, nameBytes.Length); WriteU16(output, extra.Length);
        output.Write(nameBytes); output.Write(extra); output.Write(body);

        WriteU32(central, 0x02014b50); WriteU16(central, 20); WriteU16(central, 20); WriteU16(central, 0x0801);
        WriteU16(central, 99); WriteU16(central, 0); WriteU16(central, 0x21); WriteU32(central, crc);
        WriteU32(central, (uint)body.Length); WriteU32(central, (uint)data.Length); WriteU16(central, nameBytes.Length);
        WriteU16(central, extra.Length); WriteU16(central, 0); WriteU16(central, 0); WriteU16(central, 0);
        WriteU32(central, 0); WriteU32(central, localOffset);
        central.Write(nameBytes); central.Write(extra);
    }
    var centralOffset = (uint)output.Length;
    central.WriteTo(output);
    WriteU32(output, 0x06054b50); WriteU16(output, 0); WriteU16(output, 0);
    WriteU16(output, entries.Count); WriteU16(output, entries.Count);
    WriteU32(output, (uint)central.Length); WriteU32(output, centralOffset); WriteU16(output, 0);
    return output.ToArray();
}

// Independent single-entry streaming layout: final sizes/CRC live in the central
// directory and descriptor, while bit 3 makes the local fields zero.
static byte[] WithDataDescriptor(byte[] zip)
{
    var end = zip.Length - 22;
    if (BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10, 2)) != 1)
        throw new ArgumentException("The descriptor fixture requires exactly one entry.", nameof(zip));
    var central = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16, 4)));
    var result = new byte[zip.Length + 16];
    zip.AsSpan(0, central).CopyTo(result);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(central, 4), 0x08074b50);
    zip.AsSpan(central + 16, 12).CopyTo(result.AsSpan(central + 4, 12));
    zip.AsSpan(central).CopyTo(result.AsSpan(central + 16));
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(6, 2),
        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(6, 2)) | 8));
    result.AsSpan(14, 12).Clear();
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(central + 16 + 8, 2),
        (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(central + 16 + 8, 2)) | 8));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(end + 16 + 16, 4), (uint)(central + 16));
    return result;
}

static byte[] LocalRecord(byte[] zip, string name)
{
    var offset = LocalOffset(zip, name);
    var length = 30 + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 26, 2))
        + BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(offset + 28, 2)) + (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(offset + 18, 4));
    return zip.AsSpan(offset, length).ToArray();
}

static uint FixtureCrc32(byte[] data)
{
    uint state = uint.MaxValue;
    foreach (var value in data)
    {
        state ^= value;
        for (var bit = 0; bit < 8; bit++) state = (state & 1) == 0 ? state >> 1 : (state >> 1) ^ 0xEDB88320u;
    }
    return ~state;
}

static void WriteU16(Stream stream, int value)
{
    Span<byte> bytes = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
    stream.Write(bytes);
}

static void WriteU32(Stream stream, uint value)
{
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
}

static byte[] AesCtr(byte[] key, byte[] input)
{
    using var aes = Aes.Create();
    // Independent WinZip CTR fixture: AES receives counter blocks, never input data.
    // https://www.winzip.com/en/support/aes-encryption/ (III.4-5)
    aes.Key = key;
    var output = new byte[input.Length];
    var counter = new byte[16];
    var keyStream = new byte[16];
    counter[0] = 1;
    for (int position = 0; position < input.Length; position += 16)
    {
        aes.EncryptEcb(counter, keyStream, PaddingMode.None);
        for (int i = 0; i < Math.Min(16, input.Length - position); i++)
            output[position + i] = (byte)(input[position + i] ^ keyStream[i]);
        for (int i = 0; i < counter.Length; i++) if (++counter[i] != 0) break;
    }
    return output;
}
