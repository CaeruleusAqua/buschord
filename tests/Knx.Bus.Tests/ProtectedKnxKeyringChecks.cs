using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Knx;
using Knx.Bus.Security;

internal static class ProtectedKnxKeyringChecks
{
    // Synthetic labels, addresses and envelopes, independently signed using Python hashlib
    // + cryptography. No complete foreign export or installation identifiers are included.
    // Public field pairs (Created, encrypted key/password, expected plaintext) are from:
    // Calimero-core eccdabc72b37203cfec66313432e750a24384178
    // test/io/calimero/secure/KeyringTest.java and test/resources/KeyringTest.knxkeys;
    // XKNX 82e1a851ad9e31669bd8b7dbed59e6066bb7a9f7 independently publishes the same fixture.
    // These are protocol data, not copied source code. KNX's published algorithm:
    // https://support.knx.org/hc/en-us/articles/18968368409874-Keyring-File-Format
    // https://support.knx.org/hc/en-us/articles/360001582259-Use-keyring-outside-ETS-Falcon-SDK
    private const string Project = "BusChord synthetic keyring";
    private const string Rich = """
        <Keyring xmlns="http://knx.org/xml/keyring/1" Project="BusChord synthetic keyring" CreatedBy="Synthetic vector" Created="2019-06-11T06:45:22" Signature="O6g27O2hyVUL9PI0O3E8zA==">
          <Backbone MulticastAddress="224.0.23.12" Latency="1000" Key="VGnz2DbdiMqN5EE4I7tqLw==" />
          <Interface Type="Tunneling" Host="1.2.0" UserID="4" IndividualAddress="1.2.10" Password="DQJOj7CrytX+4EV27SINp3xXa7HEfJQu+XiCt9U5AU8=" Authentication="//pj5KplK+4H7gcYVefuuCumcPVI0BBR/MXfFtaat2k=">
            <Group Address="4660" Senders="1.2.1 1.2.2" />
          </Interface>
          <Interface Type="USB" IndividualAddress="1.2.11"><Group Address="4660" Senders="1.2.2" /></Interface>
          <Interface Type="Backbone" IndividualAddress="1.2.12"><Group Address="17185" /></Interface>
          <GroupAddresses><Group Address="4660" Key="iA2KpI19ZlW0jseoXSycAg==" /><Group Address="17185" Key="pvyOR3j9CpJw9R2/WWVCZQ==" /></GroupAddresses>
          <Devices><Device IndividualAddress="1.2.0" ToolKey="GI0PDDbpgQUZK2JkTvR8pA==" ManagementPassword="ezGR1sf4vsWKWS8JMIJuzmrtEG2N9WLdyTDrwIZbNuM=" Authentication="qEUJHMW48qXne6MySIUPlWwd7RHLmh6gnS/EIzaTIew=" SequenceNumber="108" /></Devices>
        </Keyring>
        """;
    private const string Unicode = """
        <Keyring xmlns="http://knx.org/xml/keyring/1" Project="BusChord synthetic äüß🎛" CreatedBy="" Created="2019-06-11T06:45:22" Signature="3oSTjiS9klGr3uzPFjoZfA=="><GroupAddresses><Group Address="65535" Key="4L01a/iMYSYdPOfLZVHbYA==" /></GroupAddresses></Keyring>
        """;
    private static readonly XNamespace Ns = ProtectedKnxKeyringImporter.XmlNamespace;
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);
    private static ProtectedKnxKeyring Import(string xml = Rich, string password = "pwd", string project = Project)
        => ProtectedKnxKeyringImporter.Import(Bytes(xml), password, project);
    private static XElement Root() => XElement.Parse(Rich);
    private static XElement Tunnel(XElement root) => root.Elements(Ns + "Interface").First();
    private static XElement Device(XElement root) => root.Element(Ns + "Devices")!.Element(Ns + "Device")!;

    internal static int Run()
    {
        var checks = 0; var failures = 0;
        void Check(string name, bool pass) { checks++; Console.WriteLine($"{(pass ? "PASS" : "FAIL")} ProtectedKeyring {name}"); if (!pass) failures++; }
        void Reject(string name, string xml, ProtectedKeyringFailure expected = ProtectedKeyringFailure.InvalidFormat, string password = "pwd", string project = Project)
        {
            ProtectedKnxKeyring? result = null;
            try { result = Import(xml, password, project); Check(name, false); }
            catch (ProtectedKeyringImportException error) { Check(name, error.Failure == expected && !error.Message.Contains("DQJOj7") && (password.Length == 0 || !error.Message.Contains(password, StringComparison.Ordinal))); }
            finally { result?.Dispose(); }
        }
        void RejectSigned(string name, Action<XElement> mutate, ProtectedKeyringFailure expected = ProtectedKeyringFailure.InvalidFormat)
        { var root = Root(); mutate(root); Reject(name, SignForNegativeCase(root), expected); }
        void RefuseScope(string name, Action action)
        { try { action(); Check(name, false); } catch (InvalidOperationException) { Check(name, true); } }
        static bool Hex(ProtectedKeyringSecret? secret, string expected)
        {
            if (secret is null) return false;
            var bytes = secret.CopyBytes(); try { return Convert.ToHexString(bytes) == expected; } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        static bool Password(ProtectedKeyringSecret? secret, string expected)
        {
            if (secret is null) return false;
            var bytes = secret.CopyBytes(); try { return bytes.SequenceEqual(Bytes(expected)) && secret.Kind == ProtectedKeyringSecretKind.Utf8Password; } finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        using var ring = Import();
        Check("independent frozen signature accepts synthetic full-profile envelope", ring.ProjectName == Project && ring.Interfaces.Count == 3 && ring.Devices.Count == 1 && ring.GroupKeys.Count == 2);
        Check("published backbone AES vector", Hex(ring.Backbone?.Key, "96F034FCCF510760CBD63DA0F70D4A9D"));
        Check("backbone address and latency remain explicit", ring.Backbone is { MulticastAddress: "224.0.23.12", LatencyMilliseconds: 1000 });
        var tunnel = ring.SelectTunneling(new(0x1200), 4, new(0x120A));
        Check("published tunnel password vector", Password(tunnel.UserPassword, "user4"));
        Check("published tunnel authentication vector is raw password", Password(tunnel.DeviceAuthenticationPassword, "dev"));
        Check("published tool-key vector", Hex(ring.Devices[new(0x1200)].ToolKey, "AEAC47C4653ED0B25249B4AB3F474479"));
        Check("published management-password vector", Password(ring.Devices[new(0x1200)].ManagementPassword, "router1"));
        Check("published device-authentication vector", Password(ring.Devices[new(0x1200)].AuthenticationPassword, "dev"));
        Check("published group AES vector", Hex(ring.GroupKeys[new(4660)], "E14343050F4377E3159B90AFE0228216"));
        Check("independent synthetic group vector", Hex(ring.GroupKeys[new(17185)], "000102030405060708090A0B0C0D0E0F"));
        Check("historical exported sequence preserved", ring.Devices[new(0x1200)].ExportedSequenceNumber == 108);
        Check("sender policy preserved per interface and group", tunnel.Groups[new(4660)].Select(address => address.Raw).SequenceEqual(new ushort[] { 0x1201, 0x1202 }));
        Check("USB scope preserved without credentials", ring.Interfaces[new(0x120B)] is { Type: ProtectedKeyringInterfaceType.Usb, Host: null, UserId: null, UserPassword: null });
        Check("Backbone interface scope remains distinct", ring.Interfaces[new(0x120C)].Type == ProtectedKeyringInterfaceType.Backbone);
        Check("interface group selection excludes another interface group", ring.SelectInterfaceGroupKeys(new(0x120A)).Keys.Select(group => group.Raw).SequenceEqual(new ushort[] { 4660 }));
        RefuseScope("wrong tunnel host refused", () => ring.SelectTunneling(new(0x1300), 4, new(0x120A)));
        RefuseScope("wrong tunnel user refused", () => ring.SelectTunneling(new(0x1200), 5, new(0x120A)));
        RefuseScope("wrong tunnel address refused", () => ring.SelectTunneling(new(0x1200), 4, new(0x120B)));
        RefuseScope("unknown group interface refused", () => ring.SelectInterfaceGroupKeys(new(0x1210)));
        using (var unicode = Import(Unicode, "pāssw🔑rd", "BusChord synthetic äüß🎛"))
            Check("independent UTF8 metadata and password golden", Hex(unicode.GroupKeys[new(65535)], "000102030405060708090A0B0C0D0E0F") && unicode.CreatedBy == "");
        Reject("wrong password fails signature before release", Rich, ProtectedKeyringFailure.SignatureMismatch, "wrong password");
        Reject("wrong explicit project refused after verification", Rich, ProtectedKeyringFailure.ProjectMismatch, project: Project + " other");
        Reject("project label exact case", Rich, ProtectedKeyringFailure.ProjectMismatch, project: Project.ToUpperInvariant());
        Reject("tampered project fails integrity", Rich.Replace(Project, Project + "!"), ProtectedKeyringFailure.SignatureMismatch);
        Reject("tampered host fails integrity", Rich.Replace("Host=\"1.2.0\"", "Host=\"1.3.0\""), ProtectedKeyringFailure.SignatureMismatch);
        Reject("tampered sender policy fails integrity", Rich.Replace("1.2.1 1.2.2", "1.2.1 1.2.3"), ProtectedKeyringFailure.SignatureMismatch);
        Reject("tampered creation fails integrity", Rich.Replace("06:45:22", "06:45:23"), ProtectedKeyringFailure.SignatureMismatch);
        Reject("signature checked before encrypted-field decoding", Rich.Replace("VGnz2DbdiMqN5EE4I7tqLw==", "XXXXXXXXXXXXXXXXXXXXXX=="), ProtectedKeyringFailure.SignatureMismatch);
        Reject("unsigned XML refused", Rich.Replace(" Signature=\"O6g27O2hyVUL9PI0O3E8zA==\"", ""));
        Reject("wrong signature size refused", Rich.Replace("O6g27O2hyVUL9PI0O3E8zA==", "AAAA"));
        Reject("wrong namespace refused although unsigned namespace is excluded", Rich.Replace("/keyring/1", "/keyring/2"), ProtectedKeyringFailure.UnsupportedProfile);
        Reject("namespaced attribute refused", Rich.Replace("CreatedBy=", "xmlns:x=\"urn:other\" x:CreatedBy="), ProtectedKeyringFailure.UnsupportedProfile);
        Reject("nested namespace override refused", Rich.Replace("<Backbone ", "<Backbone xmlns=\"urn:other\" "), ProtectedKeyringFailure.UnsupportedProfile);
        Reject("processing instruction refused", "<?untrusted value?>" + Rich, ProtectedKeyringFailure.UnsupportedProfile);
        Reject("DTD and external entities refused", "<!DOCTYPE Keyring [<!ENTITY xxe SYSTEM 'file:///untrusted'>]>" + Rich);
        Reject("non-whitespace text cannot escape signature coverage", Rich.Replace("</Keyring>", "unsigned text</Keyring>"), ProtectedKeyringFailure.UnsupportedProfile);
        Reject("duplicate XML attribute refused", Rich.Replace("CreatedBy=", "Project=\"duplicate\" CreatedBy="));
        RejectSigned("unknown profile extension refused", root => root.SetAttributeValue("FutureKey", "value"), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("FDSK certificate backup remains explicitly unsupported", root => Device(root).SetAttributeValue("FDSK", "VGnz2DbdiMqN5EE4I7tqLw=="), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("duplicate backbone refused", root => root.Add(new XElement(root.Element(Ns + "Backbone")!)));
        RejectSigned("duplicate group key refused", root => root.Element(Ns + "GroupAddresses")!.Add(new XElement(root.Element(Ns + "GroupAddresses")!.Elements().First())));
        RejectSigned("duplicate device refused", root => root.Element(Ns + "Devices")!.Add(new XElement(Device(root))));
        RejectSigned("duplicate interface address refused", root => root.Add(new XElement(Tunnel(root))));
        RejectSigned("duplicate host user refused", root => { var duplicate = new XElement(Tunnel(root)); duplicate.SetAttributeValue("IndividualAddress", "1.2.14"); root.Add(duplicate); });
        RejectSigned("duplicate assigned group refused", root => Tunnel(root).Add(new XElement(Tunnel(root).Elements().First())));
        RejectSigned("duplicate sender refused", root => Tunnel(root).Elements().First().SetAttributeValue("Senders", "1.2.1 1.2.1"));
        RejectSigned("unbound group key reference refused", root => Tunnel(root).Elements().First().SetAttributeValue("Address", "22222"));
        RejectSigned("unknown interface type refused", root => Tunnel(root).SetAttributeValue("Type", "Tunnelling"), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("USB IP credentials refused", root => Tunnel(root).SetAttributeValue("Type", "USB"), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("user zero refused", root => Tunnel(root).SetAttributeValue("UserID", "0"));
        RejectSigned("user upper overflow refused", root => Tunnel(root).SetAttributeValue("UserID", "128"));
        RejectSigned("invalid individual component refused", root => Tunnel(root).SetAttributeValue("IndividualAddress", "1.2.256"));
        RejectSigned("broadcast individual address refused", root => Tunnel(root).SetAttributeValue("IndividualAddress", "1.2.255"), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("zero individual address refused", root => Tunnel(root).SetAttributeValue("IndividualAddress", "0.0.0"), ProtectedKeyringFailure.UnsupportedProfile);
        RejectSigned("zero group address refused", root => root.Element(Ns + "GroupAddresses")!.Elements().First().SetAttributeValue("Address", "0"));
        RejectSigned("group upper overflow refused", root => root.Element(Ns + "GroupAddresses")!.Elements().First().SetAttributeValue("Address", "65536"));
        RejectSigned("sequence upper overflow refused", root => Device(root).SetAttributeValue("SequenceNumber", "281474976710656"));
        RejectSigned("sequence is decimal rather than implicit hex", root => Device(root).SetAttributeValue("SequenceNumber", "FFFFFFFFFFFF"));
        RejectSigned("unicast backbone IP refused", root => root.Element(Ns + "Backbone")!.SetAttributeValue("MulticastAddress", "192.0.2.1"));
        RejectSigned("backbone latency overflow refused", root => root.Element(Ns + "Backbone")!.SetAttributeValue("Latency", "65536"));
        RejectSigned("raw hex key fallback refused", root => root.Element(Ns + "Backbone")!.SetAttributeValue("Key", "000102030405060708090A0B0C0D0E0F"));
        RejectSigned("password ciphertext wrong size refused", root => Tunnel(root).SetAttributeValue("Password", "VGnz2DbdiMqN5EE4I7tqLw=="));
        RejectSigned("all password padding bytes checked", root => Tunnel(root).SetAttributeValue("Password", "+FrJHnvAxlFC52F0wELTJVaRW4CBMnlu072m8sT7MuM="));
        RejectSigned("decrypted password UTF8 strict", root => Tunnel(root).SetAttributeValue("Password", "KUbuja0YxMIFfY0Fw3Tk09GZ7b9/oRdWdcuXXVGdLc0="));
        RejectSigned("late device failure returns no partially imported keyring", root => Device(root).SetAttributeValue("ManagementPassword", "+FrJHnvAxlFC52F0wELTJVaRW4CBMnlu072m8sT7MuM="));

        var emptyPassword = Root(); Tunnel(emptyPassword).SetAttributeValue("Password", "uGTy1wPZsi1uk07ew7ZcWYzFmseiOyRy97c4P326Gik=");
        using (var value = Import(SignForNegativeCase(emptyPassword))) Check("24-byte PKCS7 padding supports encrypted empty password", Password(value.Interfaces[new(0x120A)].UserPassword, ""));
        var longestPassword = Root(); Tunnel(longestPassword).SetAttributeValue("Password", "C+5FODmcic75tLRnChLiIMztrYmPP0FZogZ6Uyi9hXo=");
        using (var value = Import(SignForNegativeCase(longestPassword))) Check("23-byte password uses one padding octet", Password(value.Interfaces[new(0x120A)].UserPassword, "12345678901234567890123"));
        var maximumSequence = Root(); Device(maximumSequence).SetAttributeValue("SequenceNumber", "281474976710655");
        using (var value = Import(SignForNegativeCase(maximumSequence))) Check("48-bit maximum exported sequence preserved", value.Devices[new(0x1200)].ExportedSequenceNumber == 0xFFFFFFFFFFFF);
        var missingSequence = Root(); Device(missingSequence).Attribute("SequenceNumber")!.Remove();
        using (var value = Import(SignForNegativeCase(missingSequence))) Check("absent counter stays unknown rather than zero baseline", value.Devices[new(0x1200)].ExportedSequenceNumber is null);
        var dataOnly = Root(); foreach (var name in new[] { "UserID", "Password", "Authentication" }) Tunnel(dataOnly).Attribute(name)!.Remove();
        using (var value = Import(SignForNegativeCase(dataOnly)))
        { Check("scope-only tunneling import does not manufacture IP credentials", value.Interfaces[new(0x120A)].UserPassword is null); RefuseScope("scope-only tunnel cannot select secure credentials", () => value.SelectTunneling(new(0x1200), 4, new(0x120A))); }
        var authenticationOnly = Root(); foreach (var name in new[] { "UserID", "Password" }) Tunnel(authenticationOnly).Attribute(name)!.Remove();
        using (var value = Import(SignForNegativeCase(authenticationOnly)))
        { Check("public ETS authentication-only tunnel profile preserved", Password(value.Interfaces[new(0x120A)].DeviceAuthenticationPassword, "dev") && value.Interfaces[new(0x120A)].UserPassword is null); RefuseScope("partial tunnel cannot select secure credentials", () => value.SelectTunneling(new(0x1200), 4, new(0x120A))); }
        var noHost = Root(); Tunnel(noHost).Attribute("Host")!.Remove();
        using (var value = Import(SignForNegativeCase(noHost)))
            RefuseScope("missing host never guessed from device entries", () => value.SelectTunneling(new(0x1200), 4, new(0x120A)));

        var reordered = Root(); var rootAttrs = reordered.Attributes().ToArray(); reordered.RemoveAttributes(); foreach (var attr in rootAttrs.Reverse()) reordered.Add(attr);
        using (var value = Import(reordered.ToString())) Check("attribute order and formatting do not alter KNX signature", value.GroupKeys.Count == 2);
        using (var value = Import("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<!-- comment -->" + Rich)) Check("XML declaration and comments carry no imported semantics", value.Interfaces.Count == 3);
        using (var value = Import(Rich.Replace("UserID=\"4\"", "UserID=\"&#52;\""))) Check("decoded XML attributes follow canonical signature semantics", value.SelectTunneling(new(0x1200), 4, new(0x120A)).UserId == 4);
        var boundary = Root(); boundary.SetAttributeValue("CreatedBy", new string('x', 255));
        using (var value = Import(SignForNegativeCase(boundary))) Check("255 UTF8-byte canonical string admitted", value.CreatedBy!.Length == 255);
        Reject("256-byte canonical string bounded", Rich.Replace("Synthetic vector", new string('x', 256)), ProtectedKeyringFailure.LimitExceeded);
        Reject("UTF8 byte limit counts multibyte text", Rich.Replace("Synthetic vector", new string('ü', 128)), ProtectedKeyringFailure.LimitExceeded);
        Reject("XML depth bounded before recursive materialization", Rich.Replace("</Keyring>", "<a><b><c><d/></c></b></a></Keyring>"), ProtectedKeyringFailure.LimitExceeded);
        Reject("element count bounded", Rich.Replace("</Keyring>", string.Concat(Enumerable.Repeat("<Interface Type=\"USB\" IndividualAddress=\"1.2.10\"/>", 8192)) + "</Keyring>"), ProtectedKeyringFailure.LimitExceeded);
        Reject("attribute count bounded", Rich.Replace("CreatedBy=", string.Join(" ", Enumerable.Range(0, 17).Select(index => $"a{index}=\"x\"")) + " CreatedBy="), ProtectedKeyringFailure.LimitExceeded);
        Reject("raw XML byte size bounded", new string(' ', ProtectedKnxKeyringImporter.MaximumXmlBytes + 1), ProtectedKeyringFailure.LimitExceeded);
        Reject("empty XML refused", "", ProtectedKeyringFailure.LimitExceeded);
        Reject("empty export password is not an unsigned fallback", Rich, ProtectedKeyringFailure.LimitExceeded, "");
        Reject("oversized export password refused", Rich, ProtectedKeyringFailure.LimitExceeded, new string('x', 1025));

        var input = Bytes(Rich); var suppliedPassword = "pwd".ToCharArray();
        using (var value = ProtectedKnxKeyringImporter.Import(input, suppliedPassword, Project))
        {
            Check("caller password and source remain untouched", input.SequenceEqual(Bytes(Rich)) && suppliedPassword.SequenceEqual("pwd"));
            Array.Fill(input, (byte)0); Array.Fill(suppliedPassword, 'x');
            Check("input ownership detached", Hex(value.GroupKeys[new(4660)], "E14343050F4377E3159B90AFE0228216"));
            Check("exact source fingerprint exposed without source path", value.SourceFingerprint == Convert.ToHexString(SHA256.HashData(Bytes(Rich))));
        }
        using var stream = new MemoryStream(Bytes(Rich));
        using (var value = ProtectedKnxKeyringImporter.Import(stream, "pwd", Project)) Check("bounded stream import", value.Interfaces.Count == 3 && stream.CanRead);
        using var oversized = new CountingInputStream(ProtectedKnxKeyringImporter.MaximumXmlBytes + 100);
        try { using var value = ProtectedKnxKeyringImporter.Import(oversized, "pwd", Project); Check("stream sentinel limit", false); }
        catch (ProtectedKeyringImportException error) { Check("stream sentinel limit", error.Failure == ProtectedKeyringFailure.LimitExceeded && oversized.BytesRead == ProtectedKnxKeyringImporter.MaximumXmlBytes + 1); }

        var disposable = Import(); var ownedSecret = disposable.GroupKeys[new(4660)]; var detached = ownedSecret.CopyBytes();
        var backing = (byte[])typeof(ProtectedKeyringSecret).GetField("_bytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ownedSecret)!;
        var mutated = ownedSecret.CopyBytes(); Array.Clear(mutated);
        Check("exported key copies are detached", Hex(ownedSecret, "E14343050F4377E3159B90AFE0228216"));
        try { ((IDictionary<GroupAddress, ProtectedKeyringSecret>)disposable.GroupKeys).Clear(); Check("key lookup scope immutable", false); }
        catch (NotSupportedException) { Check("key lookup scope immutable", true); }
        try { ((IList<IndividualAddress>)disposable.Interfaces[new(0x120A)].Groups[new(4660)])[0] = new(0x1301); Check("sender scope immutable", false); }
        catch (NotSupportedException) { Check("sender scope immutable", true); }
        Check("secret ToString contains no plaintext", ownedSecret.ToString() == "[protected keyring secret]");
        disposable.Dispose(); disposable.Dispose();
        Check("Dispose zeros owned key storage", backing.All(value => value == 0));
        Check("Dispose preserves caller ownership of earlier copy", Convert.ToHexString(detached) == "E14343050F4377E3159B90AFE0228216");
        RefuseScope("disposed secret cannot be copied", () => ownedSecret.CopyBytes());
        RefuseScope("disposed keyring cannot select credentials", () => disposable.SelectTunneling(new(0x1200), 4, new(0x120A)));
        CryptographicOperations.ZeroMemory(detached);
        using (var concurrent = Import())
        {
            var secret = concurrent.GroupKeys[new(4660)]; var partial = 0;
            var copies = Task.Run(() => { for (var index = 0; index < 100; index++) { try { var copy = secret.CopyBytes(); if (Convert.ToHexString(copy) != "E14343050F4377E3159B90AFE0228216") Interlocked.Increment(ref partial); CryptographicOperations.ZeroMemory(copy); } catch (ObjectDisposedException) { } } });
            concurrent.Dispose(); copies.GetAwaiter().GetResult(); Check("concurrent Dispose exports no partially zeroed key", partial == 0);
        }
        Console.WriteLine($"ProtectedKeyring: {checks - failures}/{checks} PASS");
        return failures;
    }

    // Re-sign mutated negative test documents so parser/decryption errors are reached.
    // Positive interop assertions above use frozen independently generated signatures instead.
    private static string SignForNegativeCase(XElement root)
    {
        using var output = new MemoryStream();
        void Text(string value) { var bytes = Bytes(value); output.WriteByte(checked((byte)bytes.Length)); output.Write(bytes); }
        void Element(XElement element)
        {
            output.WriteByte(1); Text(element.Name.LocalName);
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration && attribute.Name.LocalName != "Signature").OrderBy(attribute => attribute.Name.LocalName, StringComparer.Ordinal))
            { Text(attribute.Name.LocalName); Text(attribute.Value); }
            foreach (var child in element.Elements()) Element(child);
            output.WriteByte(2);
        }
        Element(root);
        var passwordHash = Convert.FromHexString("C9596C2A178D798683D29AA4526D6099"); // independently generated public-password KDF result
        try { Text(Convert.ToBase64String(passwordHash)); root.SetAttributeValue("Signature", Convert.ToBase64String(SHA256.HashData(output.ToArray()).AsSpan(0, 16))); }
        finally { CryptographicOperations.ZeroMemory(passwordHash); }
        return root.ToString();
    }

    private sealed class CountingInputStream(int length) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { var read = Math.Min(count, length - BytesRead); Array.Fill(buffer, (byte)' ', offset, read); BytesRead += read; return read; }
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
