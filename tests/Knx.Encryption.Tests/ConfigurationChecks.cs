using System.Text;
using Knx;

internal static class ConfigurationChecks
{
    private const string Variable = "BUSCHORD_ARCHIVE_PROTECTION";
    private static ArchiveProtectionSettings First() => new()
    {
        Salt = "buschord-synthetic-test-salt", Iterations = 4096, HashAlgorithm = "SHA256", KeyLength = 32, PasswordEncoding = "utf-8",
    };
    private static ArchiveProtectionSettings Second() => new()
    {
        Salt = "buschord-second-synthetic-salt", Iterations = 8192, HashAlgorithm = "SHA512", KeyLength = 48, PasswordEncoding = "utf-16le",
    };
    private static InvalidDataException? Refused(Action action)
    { try { action(); return null; } catch (InvalidDataException error) { return error; } }
    private sealed class ProfileChangingReplacements(Dictionary<string, byte[]> values, Action change) : IReadOnlyDictionary<string, byte[]>
    {
        private bool _changed;
        public IEnumerable<string> Keys
        { get { if (!_changed) { _changed = true; change(); } return values.Keys; } }
        public IEnumerable<byte[]> Values => values.Values;
        public int Count => values.Count;
        public byte[] this[string key] => values[key];
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out byte[] value) => values.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<string, byte[]>> GetEnumerator() => values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static void Run(string directory, Action<bool, string> check,
        Func<Dictionary<string, byte[]>, string, byte[]> protectedZip,
        Func<Dictionary<string, byte[]>, byte[]> plainZip)
    {
        var previous = Environment.GetEnvironmentVariable(Variable);
        var path = Path.Combine(directory, "configuration", "archive-protection.json");
        var missing = Path.Combine(directory, "configuration", "not-created.json");
        try
        {
            Environment.SetEnvironmentVariable(Variable, missing);
            var defaults = ArchiveProtectionSettings.Load();
            check(defaults.Salt is { Length: > 0 } && defaults.Iterations == 65536 && defaults.HashAlgorithm == "SHA256"
                && defaults.KeyLength == 32 && defaults.PasswordEncoding == "utf-16le" && !File.Exists(missing), "missing profile selects the built-in defaults without creating a file");
            check(Convert.FromBase64String(EncryptedArchive.DeriveZipPassword("buschord-test")).Length == 32,
                "built-in defaults derive a password without local configuration");
            var defaultContent = new Dictionary<string, byte[]> { ["default.txt"] = [1, 4, 9] };
            var defaultZip = protectedZip(defaultContent, "owned-default-password");
            check(EncryptedArchive.ReadAll(defaultZip, "owned-default-password")["default.txt"].SequenceEqual(new byte[] { 1, 4, 9 })
                && !File.Exists(missing), "built-in profile reads a protected synthetic archive without writing settings");
            var defaultRewrite = EncryptedArchive.Rewrite(defaultZip, new Dictionary<string, byte[]> { ["default.txt"] = [2, 5, 10] }, "owned-default-password");
            check(EncryptedArchive.ReadAll(defaultRewrite, "owned-default-password")["default.txt"].SequenceEqual(new byte[] { 2, 5, 10 }),
                "built-in profile preserves protected synthetic rewrites");
            var ordinary = plainZip(new() { ["entry.txt"] = Encoding.UTF8.GetBytes("plain fixture") });
            check(EncryptedArchive.ReadAll(ordinary, "unneeded-password")["entry.txt"].SequenceEqual(Encoding.UTF8.GetBytes("plain fixture")),
                "plain archive ignores a superfluous password without any profile");
            var plainRewrite = EncryptedArchive.Rewrite(ordinary, new Dictionary<string, byte[]> { ["entry.txt"] = [1, 2, 3] }, "unneeded-password");
            check(EncryptedArchive.ReadAll(plainRewrite, null)["entry.txt"].SequenceEqual(new byte[] { 1, 2, 3 }), "plain archive rewrite also needs no profile");

            First().Save(path); var initial = ArchiveProtectionSettings.Load(path);
            check(initial.Salt == First().Salt && initial.Iterations == 4096 && initial.HashAlgorithm == "SHA256" && initial.KeyLength == 32
                && initial.PasswordEncoding == "utf-8", "configuration explicit save and load preserve every profile member");
            check(!File.Exists(missing), "explicit configuration path takes precedence over the environment path");
            Environment.SetEnvironmentVariable(Variable, path);
            check(ArchiveProtectionSettings.Load().Salt == First().Salt, "configuration environment override selects a file rather than settings data");
            // Owned synthetic profile vectors.
            check(EncryptedArchive.DeriveZipPassword("buschord-test") == "RtXNKI/aNyiHKPXRo7G5ABRQzia1zG7xoMMqWUnBLCw=", "synthetic UTF8 ASCII derivation vector");
            check(EncryptedArchive.DeriveZipPassword("Grüße🔑") == "rLDBXOiPYM6bT+loeq4tMidR9leGzzA2+VsWsG+i08w=", "synthetic UTF8 Unicode derivation vector");
            Second().Save(path);
            check(EncryptedArchive.DeriveZipPassword("archive-configuration") == "n/cCArRQg/A4veV7pn83bC6IunG3Rc95WOO67IEaKC4QRFGkbFrXlgo2iGBSOrmG", "synthetic UTF16LE SHA512 derivation vector");
            var second = ArchiveProtectionSettings.Load();
            check(second.Salt == Second().Salt && second.Iterations == 8192 && second.HashAlgorithm == "SHA512" && second.KeyLength == 48
                && second.PasswordEncoding == "utf-16le", "profile replacement is observed by the next operation without a stale cache");

            var content = new Dictionary<string, byte[]> { ["data.txt"] = Encoding.UTF8.GetBytes("owned protected fixture"), ["opaque.bin"] = [0, 255, 1, 128] };
            foreach (var profile in new[] { First(), Second(), new ArchiveProtectionSettings
            { Salt = "buschord-third-synthetic-salt", Iterations = 2048, HashAlgorithm = "SHA384", KeyLength = 24, PasswordEncoding = "utf-8" } })
            {
                profile.Save(path); var zip = protectedZip(content, "synthetic-password"); var original = (byte[])zip.Clone();
                var read = EncryptedArchive.ReadAll(zip, "synthetic-password");
                check(content.All(p => read[p.Key].SequenceEqual(p.Value)), "synthetic protected archive roundtrip " + profile.HashAlgorithm);
                var rewritten = EncryptedArchive.Rewrite(zip, new Dictionary<string, byte[]> { ["data.txt"] = [9, 8, 7], ["added.bin"] = [6, 5] }, "synthetic-password");
                var reopened = EncryptedArchive.ReadAll(rewritten, "synthetic-password");
                check(reopened["data.txt"].SequenceEqual(new byte[] { 9, 8, 7 }) && reopened["added.bin"].SequenceEqual(new byte[] { 6, 5 })
                    && reopened["opaque.bin"].SequenceEqual(content["opaque.bin"]) && zip.SequenceEqual(original), "synthetic protected rewrite preserves source and unchanged content " + profile.HashAlgorithm);
            }

            First().Save(path); var protectedSource = protectedZip(content, "synthetic-password");
            var protectedBefore = (byte[])protectedSource.Clone();
            var changedDuringRewrite = EncryptedArchive.Rewrite(protectedSource,
                new ProfileChangingReplacements(new() { ["data.txt"] = [4, 3, 2] }, () => Second().Save(path)), "synthetic-password");
            check(ArchiveProtectionSettings.Load().Salt == Second().Salt
                && Refused(() => EncryptedArchive.ReadAll(changedDuringRewrite, "synthetic-password")) is not null,
                "rewrite retains its captured profile when the sidecar changes after source authentication");
            First().Save(path); var capturedRead = EncryptedArchive.ReadAll(changedDuringRewrite, "synthetic-password");
            check(capturedRead["data.txt"].SequenceEqual(new byte[] { 4, 3, 2 }) && capturedRead["opaque.bin"].SequenceEqual(content["opaque.bin"])
                && protectedSource.SequenceEqual(protectedBefore), "one captured rewrite profile authenticates both replaced and preserved entries");
            Second().Save(path);
            check(Refused(() => EncryptedArchive.ReadAll(protectedSource, "synthetic-password"))?.Message == EncryptedArchive.WrongPasswordMessage,
                "a different valid profile cannot silently decrypt an earlier protected archive");
            check(Refused(() => EncryptedArchive.Rewrite(protectedSource, new Dictionary<string, byte[]> { ["data.txt"] = [3] }, "synthetic-password")) is not null
                && protectedSource.SequenceEqual(protectedBefore), "profile mismatch refuses protected rewrite without changing source bytes");
            Environment.SetEnvironmentVariable(Variable, missing);
            check(Refused(() => EncryptedArchive.ReadAll(protectedSource, null))?.Message == EncryptedArchive.PasswordRequiredMessage,
                "a built-in profile still requires the project password");
            check(Refused(() => EncryptedArchive.ReadAll(protectedSource, "synthetic-password"))?.Message == EncryptedArchive.WrongPasswordMessage,
                "built-in defaults do not guess a different synthetic custom profile");
            check(Refused(() => EncryptedArchive.Rewrite(protectedSource, new Dictionary<string, byte[]>(), "synthetic-password"))?.Message == EncryptedArchive.WrongPasswordMessage
                && protectedSource.SequenceEqual(protectedBefore), "a default versus custom profile mismatch refuses rewrite atomically");

            Environment.SetEnvironmentVariable(Variable, path);
            new ArchiveProtectionSettings { Salt = "" }.Save(path);
            check(ArchiveProtectionSettings.Load().Salt == "" && Refused(() => EncryptedArchive.DeriveZipPassword("p"))?.Message == ArchiveProtectionSettings.MissingSaltMessage,
                "empty salt can be saved as a disabled profile");
            new ArchiveProtectionSettings().Save(path);
            check(ArchiveProtectionSettings.Load().Salt is null, "null salt remains null rather than becoming a guessed value");
            File.WriteAllText(path, "{\"SaLt\":\"owned-synthetic\",\"Iterations\":2,\"HashAlgorithm\":\"SHA256\",\"KeyLength\":16,\"PasswordEncoding\":\"utf-8\"}");
            check(ArchiveProtectionSettings.Load().Salt == "owned-synthetic", "case-insensitive configuration members accept the public property names");
            foreach (var raw in new[]
            {
                "", "{", "[]", "null", "{\"salt\":1}", "{\"salt\":\"x\",\"Salt\":\"y\"}", "{\"extra\":true}",
                "{\"iterations\":0}", "{\"iterations\":1000001}", "{\"iterations\":1.5}", "{\"iterations\":\"2\"}",
                "{\"keyLength\":0}", "{\"keyLength\":129}", "{\"hashAlgorithm\":\"SHA1\"}", "{\"hashAlgorithm\":null}",
                "{\"passwordEncoding\":\"ascii\"}", "{\"salt\":\"invalid\\u0000text\"}", "{\"salt\":\"invalid\\ntext\"}",
            })
            {
                File.WriteAllText(path, raw);
                check(Refused(() => ArchiveProtectionSettings.Load()) is not null, "invalid configuration shape or profile is controlled");
            }
            check(EncryptedArchive.ReadAll(ordinary, "unneeded-password")["entry.txt"].SequenceEqual(Encoding.UTF8.GetBytes("plain fixture")),
                "plain archive also ignores an invalid unrelated profile");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)' ', 16 * 1024 + 1).ToArray());
            check(Refused(() => ArchiveProtectionSettings.Load()) is not null, "configuration file reading stops at the bounded byte limit");
            First().Save(path); var validBytes = File.ReadAllBytes(path);
            foreach (var invalid in new[]
            {
                new ArchiveProtectionSettings { Salt = new string('s', 4097) }, new ArchiveProtectionSettings { Salt = "invalid\0text" },
                new ArchiveProtectionSettings { Salt = "\ud800" }, new ArchiveProtectionSettings { HashAlgorithm = "SHA1" },
                new ArchiveProtectionSettings { PasswordEncoding = "utf-32" }, new ArchiveProtectionSettings { Iterations = -1 },
                new ArchiveProtectionSettings { KeyLength = 129 },
            })
                check(Refused(() => invalid.Save(path)) is not null && File.ReadAllBytes(path).SequenceEqual(validBytes),
                    "invalid save leaves the earlier configuration byte-for-byte intact");
            const string marker = "owned-error-redaction-marker";
            var invalidSecret = new ArchiveProtectionSettings { Salt = marker, Iterations = 0 };
            var error = Refused(() => invalidSecret.Save(path));
            check(error is not null && !error.ToString().Contains(marker, StringComparison.Ordinal), "configuration errors never echo the salt value");
            check(Refused(() => EncryptedArchive.DeriveZipPassword(new string('p', 65537))) is not null, "password input is bounded before derivation");
            check(Refused(() => EncryptedArchive.DeriveZipPassword("\ud800")) is not null, "invalid password text is rejected without encoding replacement");
            Environment.SetEnvironmentVariable(Variable, "");
            check(Refused(() => ArchiveProtectionSettings.Load()) is not null, "an invalid environment path does not fall back to another profile");
        }
        finally { Environment.SetEnvironmentVariable(Variable, previous); }
    }
}
