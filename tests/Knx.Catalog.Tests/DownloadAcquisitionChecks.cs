using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Knx.Catalog;
using Knx.Core.Catalog.Downloads;

namespace Knx.Catalog.Tests;

internal static class DownloadAcquisitionChecks
{
    internal static void Run(Action<string, bool, string?> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<string, bool, string?> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "BusChord-download-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var counter = 0;
        var fixture = Zip(("knxprod.xml", Xml("First")));
        var source = Source("fixture");
        var options = new CatalogDownloadOptions { MinimumHostIntervalMilliseconds = 0, MaximumRetryDelayMilliseconds = 0 };
        async Task Test(string name, Func<string, Task<bool>> action)
        {
            var path = Path.Combine(root, (++counter).ToString());
            Directory.CreateDirectory(path);
            try { check("Catalogue downloads: " + name, await action(path), null); }
            catch (Exception error) { check("Catalogue downloads: " + name, false, error.ToString()); }
        }
        CatalogDownloadService Service(string path, Func<HttpRequestMessage, HttpResponseMessage> respond,
            IReadOnlyList<CatalogDownloadSource>? sources = null, CatalogDownloadOptions? limits = null) =>
            new(new CatalogStore(Path.Combine(path, "library")), Path.Combine(path, "local"), new Handler(respond), limits ?? options, sources ?? [source]);
        await Test("unknown rights require personal evidence and never become a manufacturer grant", async path =>
        {
            var calls = 0;
            using var service = Service(path, _ => { calls++; return Ok(fixture); });
            var initial = await service.DownloadAsync([source.Id]);
            var refusesGrant = await Throws<InvalidOperationException>(() => service.RecordConsentAsync(source.Id, CatalogConsentKind.ManufacturerTerms, ""));
            var refusesEmpty = await Throws<ArgumentException>(() => service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "yes"));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "Written permission for my own local catalogue import.");
            var final = await service.DownloadAsync([source.Id]);
            return initial.Items.Single().Status == CatalogDownloadItemStatus.NotAuthorized && refusesGrant && refusesEmpty && calls == 1 &&
                service.Sources.Single().Rights == CatalogSourceRights.Unknown &&
                service.GetAuthorization(source) == CatalogSourceAuthorization.UserDeclaredAuthorization && final.CatalogsAdded == 1 &&
                final.Provenance.Single().ConsentKind == CatalogConsentKind.UserDeclaredAuthorization;
        });
        await Test("explicit restrictions cannot be overridden by a personal declaration", async path =>
        {
            var restricted = source with { Rights = CatalogSourceRights.Restricted };
            using var service = Service(path, _ => throw new Exception("Network must not be used"), [restricted]);
            return await Throws<InvalidOperationException>(() => service.RecordConsentAsync(source.Id,
                CatalogConsentKind.UserDeclaredAuthorization, "I claim a personal permission for this source.")) &&
                (await service.DownloadAsync([source.Id])).Items.Single().Status == CatalogDownloadItemStatus.NotAuthorized;
        });
        await Test("malformed partial-null local caches cannot authorize a download or prevent startup", async path =>
        {
            var local = Path.Combine(path, "local");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "consents.json"), "[null,{\"SourceId\":null},{\"Id\":\"fake\",\"SourceId\":\"fixture\",\"TermsRevision\":null,\"Kind\":999}]");
            Directory.CreateDirectory(Path.Combine(local, "reports"));
            File.WriteAllText(Path.Combine(local, "reports", "bad.json"), "{\"Id\":\"bad\",\"StartedAt\":\"2026-10-28T00:00:00Z\",\"FinishedAt\":\"2026-10-28T00:00:00Z\",\"Items\":[null],\"Provenance\":[]}");
            File.WriteAllText(Path.Combine(local, "registry.json"), "{\"RetrievedAt\":\"2026-10-28T00:00:00Z\",\"Entries\":[null],\"DirectoryCount\":1,\"CodeCount\":0,\"RightsNotice\":\"x\",\"SourceUrls\":[]}");
            using var service = Service(path, _ => throw new Exception("Invalid cache must not authorize network access"));
            var report = await service.DownloadAsync([source.Id]);
            return service.GetConsent(source.Id) is null && service.GetAuthorization(source) == CatalogSourceAuthorization.Missing &&
                report.Items.Single().Status == CatalogDownloadItemStatus.NotAuthorized && service.Registry.Load() is null &&
                service.LoadReports().Count == 1;
        });
        await Test("consent persists, revision changes invalidate it, and revoke removes eligibility", async path =>
        {
            using (var service = Service(path, _ => Ok(fixture)))
                await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            using var next = Service(path, _ => Ok(fixture));
            var loaded = next.GetAuthorization(source) == CatalogSourceAuthorization.UserDeclaredAuthorization;
            var stale = next.GetAuthorization(source with { TermsRevision = "new-scope" }) == CatalogSourceAuthorization.Missing;
            await next.RevokeConsentAsync(source.Id);
            return loaded && stale && next.GetAuthorization(source) == CatalogSourceAuthorization.Missing;
        });
        await Test("scope changes invalidate consent even when the manual revision is unchanged", async path =>
        {
            using var service = Service(path, _ => Ok(fixture));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var changed = new[]
            {
                source with { DownloadUri = new("https://catalogue.example.test/other") },
                source with { SourcePage = new("https://catalogue.example.test/other-source") },
                source with { TermsUri = new("https://catalogue.example.test/other-terms") },
                source with { AllowedHosts = ["catalogue.example.test", "cdn.example.test"] },
                source with { ManufacturerId = "M-0001" }, source with { Rights = CatalogSourceRights.ManufacturerGranted },
                source with { Availability = CatalogSourceAvailability.Bundle }, source with { Notice = "Changed scope" },
                source with { Formats = "different format" }, source with { NoticeVersion = "2" },
            };
            return changed.All(item => service.GetAuthorization(item) == CatalogSourceAuthorization.Missing) &&
                service.GetConsent(source.Id)!.ScopeHash == CatalogDownloadService.GetSourceScopeHash(source);
        });
        await Test("revocation during transfer produces a report and prevents library mutation", async path =>
        {
            CatalogDownloadService? reference = null;
            using var service = Service(path, _ =>
            {
                reference!.RevokeConsentAsync(source.Id).GetAwaiter().GetResult();
                return Ok(fixture);
            });
            reference = service;
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id]);
            return report.Items.Single().Status == CatalogDownloadItemStatus.NotAuthorized && report.CatalogsAdded == 0 && service.LoadReports().Count == 1;
        });
        await Test("a cache write failure does not misreport a successful library commit", async path =>
        {
            var cache = Path.Combine(path, "cache-is-a-file");
            File.WriteAllText(cache, "not a directory");
            using var service = new CatalogDownloadService(new CatalogStore(Path.Combine(path, "library"), cache),
                Path.Combine(path, "local"), new Handler(_ => Ok(fixture)), options, [source]);
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id]);
            return report.CatalogsAdded == 1 && File.Exists(report.Items.Single().LibraryPath);
        });
        await Test("a report-path failure returns the committed import and a persistence warning", async path =>
        {
            using var service = Service(path, _ => Ok(fixture));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            File.WriteAllText(Path.Combine(service.DataDirectory, "reports"), "A regular file occupies the report directory.");
            var report = await service.DownloadAsync([source.Id]);
            return report.CatalogsAdded == 1 && !string.IsNullOrWhiteSpace(report.PersistenceWarning) &&
                File.Exists(report.Items.Single().LibraryPath) && report.Provenance.Count == 1 &&
                Directory.EnumerateFiles(Path.Combine(service.DataDirectory, "provenance"), "*.json").Count() == 1;
        });
        await Test("a cancelled preserving import does not mutate the library", async path =>
        {
            var input = Path.Combine(path, "input.knxprod");
            File.WriteAllBytes(input, fixture);
            var library = Path.Combine(path, "library");
            var store = new CatalogStore(library);
            var first = store.ImportPreservingExisting(input);
            var before = File.ReadAllBytes(first.LibraryPath);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var refused = await Throws<OperationCanceledException>(() => Task.Run(() => store.ImportPreservingExisting(input, cancelled.Token)));
            return refused && before.SequenceEqual(File.ReadAllBytes(first.LibraryPath)) && !Directory.EnumerateFiles(library, "*.tmp").Any();
        });
        await Test("byte-identical imports skip and changed identity content preserves the installed bytes", async path =>
        {
            var current = fixture;
            using var service = Service(path, _ => Ok(current));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var first = await service.DownloadAsync([source.Id]);
            var installed = first.Items.Single().LibraryPath!;
            var before = SHA256.HashData(File.ReadAllBytes(installed));
            var second = await service.DownloadAsync([source.Id]);
            current = Zip(("knxprod.xml", Xml("Changed display text")));
            var third = await service.DownloadAsync([source.Id]);
            return first.CatalogsAdded == 1 && second.Items.Single().Status == CatalogDownloadItemStatus.IdenticalSkipped &&
                third.Items.Single().Status == CatalogDownloadItemStatus.ConflictKeptExisting &&
                before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(installed))) && service.LoadReports().Count == 3;
        });
        await Test("a vendor wrapper imports each catalogue and reports unsupported programs without executing them", async path =>
        {
            var secondFixture = Zip(("knxprod.xml", Xml("Second", "P-2")));
            var outer = ZipBytes(("first.knxprod", fixture), ("sub/second.knxprod", secondFixture),
                ("setup.exe", [1, 2, 3]), ("old.vd5", [4]));
            using var service = Service(path, _ => Ok(outer));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id]);
            return report.CatalogsAdded == 2 && report.Items.Count(i => i.Status == CatalogDownloadItemStatus.Unsupported) == 2;
        });
        await Test("foreign redirects and HTTPS downgrades are refused before a second request", async path =>
        {
            var calls = 0;
            using var service = Service(path, _ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new("http://catalogue.example.test/unsafe");
                return response;
            });
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            return (await service.DownloadAsync([source.Id])).Items.Single().Status == CatalogDownloadItemStatus.Failed && calls == 1;
        });
        await Test("streamed bytes count toward the batch budget and partial files are removed", async path =>
        {
            using var service = Service(path, _ => new(HttpStatusCode.OK)
            { Content = new StreamContent(new NonSeekStream(fixture)) }, limits: options with { MaximumBatchBytes = 16 });
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id]);
            return report.Items.Single().Status == CatalogDownloadItemStatus.Failed && report.BytesReceived > 16 &&
                !Directory.EnumerateFiles(Path.Combine(path, "local", "jobs"), "*", SearchOption.AllDirectories).Any();
        });
        await Test("truncated bodies are rejected", async path =>
        {
            using var service = Service(path, _ => { var response = Ok(fixture); response.Content.Headers.ContentLength = fixture.Length + 1; return response; });
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            return (await service.DownloadAsync([source.Id])).Items.Single().Status == CatalogDownloadItemStatus.Failed;
        });
        await Test("network deadlines are reported as time limits rather than user cancellation", async path =>
        {
            using var service = Service(path, _ => new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(fixture)) },
                limits: options with { RequestTimeout = TimeSpan.FromMilliseconds(30) });
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id]);
            return !report.Cancelled && report.Items.Single().Status == CatalogDownloadItemStatus.Failed &&
                report.Items.Single().Detail.Contains("time limit", StringComparison.OrdinalIgnoreCase);
        });
        await Test("archive traversal, XML DTD and wrong manufacturers do not reach the library", async path =>
        {
            foreach (var bad in new[] { Zip(("../escape", "data")), Zip(("knxprod.xml", "<!DOCTYPE KNX [<!ENTITY x SYSTEM 'file:///never-read'>]>" + Xml("X"))),
                Zip(("knxprod.xml", Xml("Wrong").Replace("M-00FA", "M-0001"))) })
            {
                using var service = Service(Path.Combine(path, Guid.NewGuid().ToString("N")), _ => Ok(bad));
                await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
                if ((await service.DownloadAsync([source.Id])).CatalogsAdded != 0) return false;
            }
            return !Directory.EnumerateFiles(path, "*.knxprod", SearchOption.AllDirectories).Any();
        });
        await Test("foreign application manufacturers and deeply nested XML are rejected", async path =>
        {
            var foreign = "<KNX xmlns='http://knx.org/xml/project/23'><ManufacturerData><Manufacturer RefId='M-0001'><ApplicationPrograms/></Manufacturer></ManufacturerData></KNX>";
            var deep = Xml("Deep").Replace("</Manufacturer>", string.Concat(Enumerable.Repeat("<Nested>", 130)) +
                string.Concat(Enumerable.Repeat("</Nested>", 130)) + "</Manufacturer>");
            foreach (var bad in new[] { Zip(("knxprod.xml", Xml("Valid")), ("M-0001/program.xml", foreign)), Zip(("knxprod.xml", deep)),
                Zip(("knxprod.xml", Xml("Valid")), ("M-00FA\\program.xml", "<data/>")),
                Zip(("knxprod.xml", Xml("Valid")), ("M-00FA/alias./program.xml", "<data/>")) })
            {
                using var service = Service(Path.Combine(path, Guid.NewGuid().ToString("N")), _ => Ok(bad));
                await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
                var report = await service.DownloadAsync([source.Id]);
                if (report.CatalogsAdded != 0 || !report.Items.Any(i => i.Status == CatalogDownloadItemStatus.Failed)) return false;
            }
            return true;
        });
        await Test("manufacturer placement and namespaces match the import parser boundary", async path =>
        {
            var extra = "<Extra><Manufacturer RefId='M-0001'><Hardware/></Manufacturer></Extra>";
            var wrongNamespace = "<Extra><x:Manufacturer xmlns:x='urn:foreign' RefId='M-0001'><x:Hardware/></x:Manufacturer></Extra>";
            var misplacedExpected = "<Extra><Manufacturer RefId='M-00FA'/></Extra>";
            var mixed = Xml("Valid").Replace("</ManufacturerData>", "<Manufacturer RefId='M-0001'><Hardware/></Manufacturer></ManufacturerData>");
            foreach (var bad in new[]
            {
                Zip(("knxprod.xml", Xml("Valid").Replace("<ManufacturerData>", extra + "<ManufacturerData>"))),
                Zip(("knxprod.xml", Xml("Valid").Replace("<ManufacturerData>", wrongNamespace + "<ManufacturerData>"))),
                Zip(("knxprod.xml", Xml("Valid").Replace("<ManufacturerData>", misplacedExpected + "<ManufacturerData>"))),
                Zip(("knxprod.xml", mixed)),
                Zip(("knxprod.xml", Xml("Valid")), ("knx_master.xml", "<KNX xmlns='http://knx.org/xml/project/23'>" + extra + "</KNX>")),
            })
            {
                using var service = Service(Path.Combine(path, Guid.NewGuid().ToString("N")), _ => Ok(bad));
                await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
                var report = await service.DownloadAsync([source.Id]);
                if (report.CatalogsAdded != 0 || !report.Items.Any(i => i.Status == CatalogDownloadItemStatus.Failed)) return false;
            }
            return true;
        });
        await Test("real master-code metadata and non-application baggage remain distinct", async path =>
        {
            var master = "<KNX xmlns='http://knx.org/xml/project/23'><MasterData><Manufacturers><Manufacturer Id='M-0001' KnxManufacturerId='1' Name='Other code'/><Manufacturer Id='M-00FA' KnxManufacturerId='250' Name='Example'/></Manufacturers></MasterData></KNX>";
            var payload = Zip(("knxprod.xml", Xml("Valid")), ("knx_master.xml", master),
                ("M-00FA/Baggages/data.xml", "<Data><Manufacturer Text='Decorative baggage metadata'/></Data>"));
            using var service = Service(path, _ => Ok(payload));
            await service.RecordConsentAsync(source.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            return (await service.DownloadAsync([source.Id])).CatalogsAdded == 1;
        });
        await Test("cancellation returns a persistent partial report and retains completed imports", async path =>
        {
            using var cancellation = new CancellationTokenSource();
            var second = source with { Id = "second", DownloadUri = new("https://catalogue.example.test/slow") };
            using var service = Service(path, request => request.RequestUri!.AbsolutePath == "/slow" ?
                new(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(fixture)) } : Ok(fixture), [source, second]);
            foreach (var item in service.Sources)
                await service.RecordConsentAsync(item.Id, CatalogConsentKind.UserDeclaredAuthorization, "My specific written source authorization is held locally.");
            var report = await service.DownloadAsync([source.Id, second.Id], new InlineProgress(p =>
            { if (p.SourceId == source.Id && p.Phase == CatalogDownloadPhase.Completed) cancellation.Cancel(); }), cancellation.Token);
            return report.Cancelled && report.CatalogsAdded == 1 && report.Items.Any(i => i.Status == CatalogDownloadItemStatus.Cancelled) && service.LoadReports().Count == 1;
        });
        await Test("registry refresh is explicit and preserves official website metadata without contact people", async path =>
        {
            var calls = 0;
            using var service = Service(path, request =>
            {
                calls++;
                return request.RequestUri!.AbsolutePath.EndsWith("knx_master.xml", StringComparison.Ordinal) ?
                    Ok(Encoding.UTF8.GetBytes("<KNX xmlns='http://knx.org/xml/project/23'><MasterData><Manufacturers><Manufacturer Id='M-00FA' KnxManufacturerId='250' Name='Example Devices' MemberStatus='Active'/></Manufacturers></MasterData></KNX>")) :
                    Ok(Encoding.UTF8.GetBytes("1 Results <a href='?page=0'>0</a><details><p class=\"sub-text\">Germany</p><h4>Example Devices GmbH</h4>Website: <a href=\"https://devices.example.test\">website</a><p>Person private@example.test</p></details>"));
            });
            var refused = await Throws<InvalidOperationException>(() => service.Registry.RefreshAsync(false));
            var snapshot = await service.Registry.RefreshAsync(true);
            var text = File.ReadAllText(Path.Combine(path, "local", "registry.json"));
            return refused && calls == 2 && snapshot.CodeCount == 1 && snapshot.DirectoryCount == 1 && snapshot.Entries.Count == 2 &&
                snapshot.Entries.Single(e => e.IdentityKind == "DirectoryEntry").Website == "https://devices.example.test/" && !text.Contains("private@example.test", StringComparison.Ordinal);
        });
        await Test("deep registry master XML is refused before cache replacement", async path =>
        {
            var local = Path.Combine(path, "local");
            Directory.CreateDirectory(local);
            var sentinel = "{\"previous\":\"cache bytes\"}";
            File.WriteAllText(Path.Combine(local, "registry.json"), sentinel);
            var deep = "<KNX xmlns='http://knx.org/xml/project/23'>" + string.Concat(Enumerable.Repeat("<Nested>", 130)) +
                string.Concat(Enumerable.Repeat("</Nested>", 130)) + "</KNX>";
            using var service = Service(path, _ => Ok(Encoding.UTF8.GetBytes(deep)));
            return await Throws<InvalidDataException>(() => service.Registry.RefreshAsync(true)) &&
                File.ReadAllText(Path.Combine(local, "registry.json")) == sentinel;
        });
        // Only this generated, verified temporary subtree is removed.
        if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
    }

    private static CatalogDownloadSource Source(string id) => new(id, "Example product", "M-00FA",
        new("https://catalogue.example.test/downloads"), new("https://catalogue.example.test/file"), ["catalogue.example.test"],
        CatalogSourceAvailability.Direct, CatalogSourceRights.Unknown, new("https://catalogue.example.test/terms"), "fixture-1", "Synthetic source", "knxprod", DateTimeOffset.UnixEpoch, "Example Devices");
    private static string Xml(string name, string product = "P-1") => $"<KNX xmlns='http://knx.org/xml/prod/20'><ManufacturerData><Manufacturer RefId='M-00FA'><Catalog><CatalogItem Id='CI-{product}' Name='{name}' ProductRefId='M-00FA_H-1_{product}'/></Catalog><Hardware><Hardware Id='M-00FA_H-1' Name='Fixture'><Products><Product Id='M-00FA_H-1_{product}' Text='{name}' OrderNumber='X'/></Products></Hardware></Hardware></Manufacturer></ManufacturerData></KNX>";
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static byte[] Zip(params (string Name, string Data)[] items) => ZipBytes(items.Select(i => (i.Name, Encoding.UTF8.GetBytes(i.Data))).ToArray());
    private static byte[] ZipBytes(params (string Name, byte[] Data)[] items)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var item in items) { using var stream = zip.CreateEntry(item.Name).Open(); stream.Write(item.Data); }
        return output.ToArray();
    }
    private static async Task<bool> Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; } catch (T) { return true; }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
    }
    private sealed class InlineProgress(Action<CatalogDownloadProgress> report) : IProgress<CatalogDownloadProgress>
    { public void Report(CatalogDownloadProgress value) => report(value); }
    private class NonSeekStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class SlowStream(byte[] bytes) : NonSeekStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(300, token); return await base.ReadAsync(buffer, token); }
    }
}
