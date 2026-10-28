using System.Security.Cryptography;
using System.Text.Json;
using Knx.Updates;

if (args.Length == 0) { Console.Error.WriteLine("Commands: keygen <private-path-outside-repo> <public-path>; sign <package> <version> <rid> <stable|preview> <sequence> <release-base-https-url> <key-id> <private-key-path|env:NAME> <output>"); return 2; }
try
{
    if (args is ["inventory", var publishedDirectory, var inventoryApp, var inventoryVersion, var inventoryRid, var inventoryChannel])
    {
        InstalledCodeInventory.Create(publishedDirectory, inventoryApp, inventoryVersion, inventoryRid, inventoryChannel);
        Console.WriteLine("Created scoped installed-code inventory."); return 0;
    }
    if (args is ["check-inventory", var installedDirectory, var installedApp, var installedVersion, var installedRid])
    {
        var checkedTree = InstalledCodeInventory.Check(installedDirectory, installedApp, installedVersion, installedRid);
        Console.WriteLine(JsonSerializer.Serialize(checkedTree)); return checkedTree.Safe ? 0 : 1;
    }
    if (args is ["refresh", var previousFeed, var existingPackage, var refreshKeyId, var refreshKeySource, var refreshedOutput])
    {
        var envelope = File.ReadAllText(previousFeed);
        var signed = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelope) ?? throw new InvalidDataException("Missing original signed feed.");
        var old = JsonSerializer.Deserialize<UpdateManifest>(Convert.FromBase64String(signed.Payload)) ?? throw new InvalidDataException("Missing original payload.");
        // Validate the old signature at its issue time, then recheck the original package bytes/scope.
        // A compromised release host cannot use refresh to get altered bytes signed.
        var original = UpdateManifests.Verify(envelope, ProductionUpdateTrust.Value, old.Rid, old.Channel, "0.0.0", old.PublishedAt.AddSeconds(1));
        await TrustedUpdateSource.VerifyPackageAsync(original, existingPackage, CancellationToken.None);
        using var refreshKey = ECDsa.Create();
        refreshKey.ImportFromPem(refreshKeySource.StartsWith("env:", StringComparison.Ordinal)
            ? Environment.GetEnvironmentVariable(refreshKeySource[4..]) ?? throw new InvalidOperationException("Missing refresh signing key.")
            : File.ReadAllText(refreshKeySource));
        var now = DateTimeOffset.UtcNow;
        var fresh = old with { Sequence = Math.Max(now.ToUnixTimeSeconds(), checked(old.Sequence + 1)), PublishedAt = now, ExpiresAt = now.AddDays(7) };
        var refreshed = UpdateManifests.Sign(fresh, refreshKeyId, refreshKey);
        UpdateManifests.Verify(refreshed, ProductionUpdateTrust.Value, old.Rid, old.Channel, "0.0.0", now);
        File.WriteAllText(refreshedOutput, refreshed); Console.WriteLine("Refreshed an unchanged authenticated update package."); return 0;
    }
    if (args is ["verify-release", var releaseFeed, var releasePackage])
    {
        var envelope = File.ReadAllText(releaseFeed);
        var signed = JsonSerializer.Deserialize<SignedUpdateEnvelope>(envelope) ?? throw new InvalidDataException("Missing original signed feed.");
        var old = JsonSerializer.Deserialize<UpdateManifest>(Convert.FromBase64String(signed.Payload)) ?? throw new InvalidDataException("Missing original payload.");
        var verified = UpdateManifests.Verify(envelope, ProductionUpdateTrust.Value, old.Rid, old.Channel, "0.0.0", old.PublishedAt.AddSeconds(1));
        await TrustedUpdateSource.VerifyPackageAsync(verified, releasePackage, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { old.AppId, Version = old.Asset.Version, old.Rid, old.Channel })); return 0;
    }
    if (args is ["show-trust"])
    {
        Console.WriteLine(JsonSerializer.Serialize(new { ProductionUpdateTrust.Value.Configured, Feed = ProductionUpdateTrust.Value.Feed?.AbsoluteUri, KeyIds = ProductionUpdateTrust.Value.PublicKeys.Keys }));
        return 0;
    }
    if (args is ["trust-source", var feedBase, var trustedId, var publicKeyFile, var generatedSource])
    {
        var feed = new Uri(feedBase.EndsWith('/') ? feedBase : feedBase + '/'); UpdateManifests.ValidateHttps(feed);
        using var publicKey = ECDsa.Create(); publicKey.ImportFromPem(File.ReadAllText(publicKeyFile));
        if (publicKey.KeySize != 256 || publicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") throw new CryptographicException("P-256 public key required.");
        var source = "namespace Knx.Updates; public static class ProductionUpdateTrust { public static UpdateTrust Value => new(new System.Uri(" +
            JsonSerializer.Serialize(feed.AbsoluteUri) + "), new System.Collections.Generic.Dictionary<string,string> { [" + JsonSerializer.Serialize(trustedId) + "] = " +
            JsonSerializer.Serialize(publicKey.ExportSubjectPublicKeyInfoPem()) + " }); }";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(generatedSource))!); File.WriteAllText(generatedSource, source);
        Console.WriteLine("Generated compiled public update trust source."); return 0;
    }
    if (args is ["keygen", var privatePath, var publicPath])
    {
        var full = Path.GetFullPath(privatePath);
        var repo = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory)) + Path.DirectorySeparatorChar;
        if (full.StartsWith(repo, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Private signing keys must remain outside the repository.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(publicPath))!);
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using (var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream)) writer.Write(generated.ExportPkcs8PrivateKeyPem());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(full, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(publicPath, generated.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("Created a P-256 key pair. The private key was not printed."); return 0;
    }
    if (args is ["public-key", var existingKey, var destination])
    {
        using var key = ECDsa.Create(); key.ImportFromPem(File.ReadAllText(existingKey));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.WriteAllText(destination, key.ExportSubjectPublicKeyInfoPem());
        Console.WriteLine("Exported public signing key."); return 0;
    }
    if (args is ["sign", var package, var version, var rid, var channel, var sequenceText, var baseUrl, var keyId, var keySource, var output])
    {
        var uri = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + '/'); UpdateManifests.ValidateHttps(uri);
        using var key = ECDsa.Create();
        var pem = keySource.StartsWith("env:", StringComparison.Ordinal) ? Environment.GetEnvironmentVariable(keySource[4..])
            : File.ReadAllText(Path.GetFullPath(keySource));
        key.ImportFromPem(pem ?? throw new InvalidOperationException("The signing key is not configured."));
        using var input = File.OpenRead(package);
        var asset = new UpdateAsset(Path.GetFileName(package), version, new Uri(uri, Path.GetFileName(package)).AbsoluteUri,
            Convert.ToHexString(SHA256.HashData(input)), input.Length);
        var now = DateTimeOffset.UtcNow;
        var manifest = new UpdateManifest(1, "BusChord", rid, channel, long.Parse(sequenceText, System.Globalization.CultureInfo.InvariantCulture), now, now.AddDays(7), asset);
        UpdatePackageScope.Verify(package, manifest);
        var signed = UpdateManifests.Sign(manifest, keyId, key);
        var trust = new UpdateTrust(uri, new Dictionary<string, string> { [keyId] = key.ExportSubjectPublicKeyInfoPem() });
        UpdateManifests.Verify(signed, trust, rid, channel, "0.0.0", now);
        if (ProductionUpdateTrust.Value.Configured)
            UpdateManifests.Verify(signed, ProductionUpdateTrust.Value, rid, channel, "0.0.0", now);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!); File.WriteAllText(output, signed);
        Console.WriteLine("Signed update feed: " + Path.GetFileName(output)); return 0;
    }
    Console.Error.WriteLine("Invalid release command arguments."); return 2;
}
catch (Exception error) { Console.Error.WriteLine("Release operation failed: " + error.GetType().Name + ". Check paths, scope, version and signing configuration."); return 1; }
