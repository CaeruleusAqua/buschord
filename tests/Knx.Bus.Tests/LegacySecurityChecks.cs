using System.Collections.Concurrent;
using System.Net;
using Knx.Bus.Security;

#pragma warning disable CS0618 // These checks deliberately exercise fail-closed compatibility APIs.
internal static class LegacySecurityChecks
{
    internal static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS" : "FAIL") + " LegacySecurity " + name); if (!ok) failures++; }
        static bool Refused(Action action) { try { action(); return false; } catch (NotSupportedException) { return true; } }
        var key = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(); var endpoint = new IPEndPoint(IPAddress.Loopback, 3671);
        var packet = new SecurePacket { SerialNumber = [1, 2, 3, 4, 5, 6], Payload = [1, 2, 3] };
        var telegram = new SecureTelegram { SequenceNumber = 1, PlainData = [0, 128, 1] }; var header = SecureFrameHeader.Group(0x1110, 0x0101);
        Check("legacy key derivation cannot produce purported IP keys", Refused(() => SecureKeys.DeriveKeys("synthetic", key)));
        Check("legacy wrapper cannot produce wire bytes", Refused(() => SecureWrapper.Wrap(packet, key)));
        var oldWrapper = Convert.FromHexString("00010000000000010102030405060000C86BC93E11E7E61E8719D2A598937D8F476B0638DE20CD9CFC569A30502FC03A");
        Check("previously accepted legacy wrapper is no longer authenticated", !SecureWrapper.TryUnwrap(oldWrapper, key, out var unwrapped, out var wrapperError) && unwrapped is null && wrapperError is not null);
        Check("legacy identity helper cannot claim an authenticated sender", !SecureWrapper.IsFrom(packet, packet.SerialNumber));
        Check("legacy authentication cannot serialize a user key", Refused(() => SecureSession.Authenticate(endpoint, key)));
        var oldAuthentication = new byte[24]; oldAuthentication[0] = 8; oldAuthentication[1] = 1; key.CopyTo(oldAuthentication, 8);
        Check("legacy authentication parser never returns a credential", !SecureSession.TryParseAuthenticate(oldAuthentication, out var parsedEndpoint, out var parsedKey, out var authError)
            && parsedEndpoint is null && parsedKey is null && authError is not null);
        Check("legacy response cannot produce a false authentication frame", Refused(() => SecureSession.Response(1, packet.SerialNumber, key)));
        Check("legacy response parser cannot claim authentication", !SecureSession.TryParseResponse(new byte[24], key, out var sid, out var serial, out var responseError)
            && sid == 0 && serial is null && responseError is not null);
        Check("legacy Data Secure cannot expose plaintext as ciphertext", Refused(() => SecureTelegram.Encode(header, telegram, key)) && Refused(() => SecureTelegram.Encrypt(header.Source, header.Destination, telegram, key)));
        var oldData = Convert.FromHexString("00000000000000010080011BB63969");
        Check("previously accepted legacy Data Secure is no longer authenticated", !SecureTelegram.TryDecrypt(header, oldData, key, out var plain, out var dataError)
            && plain is null && dataError is not null && !SecureTelegram.TryDecrypt(header, oldData, key, 4, out _, out _));
        Check("legacy cEMI builders cannot produce false secure frames", Refused(() => SecureApdu.BuildLDataIndication(header, oldData)) && Refused(() => SecureApdu.BuildLDataIndication(header.Source, header.Destination, telegram, key)));
        Check("legacy cEMI parsing cannot release claimed secure data", !SecureApdu.TryParseLDataIndication(new byte[32], out _, out var raw) && raw is null
            && !SecureApdu.TryDecryptLDataIndication(new byte[32], key, out var parsed, out _) && parsed is null);
        var sequence = new SecureKey(); var values = new ConcurrentBag<ulong>(); Parallel.For(0, 512, _ => values.Add(sequence.NextSequenceNumber()));
        Check("counter allocation is atomic for concurrent callers", values.Count == 512 && values.Distinct().Count() == 512 && values.Min() == 1 && values.Max() == 512 && sequence.SequenceNumber == 512);
        var exhausted = new SecureKey { SequenceNumber = SecureKey.MaxSequenceNumber - 1 }; var accepted = 0; var denied = 0;
        Parallel.For(0, 64, _ => { try { if (exhausted.NextSequenceNumber() == SecureKey.MaxSequenceNumber) Interlocked.Increment(ref accepted); } catch (InvalidOperationException) { Interlocked.Increment(ref denied); } });
        Check("48-bit counter exhaustion never wraps or reuses a nonce", accepted == 1 && denied == 63 && exhausted.SequenceNumber == SecureKey.MaxSequenceNumber);
        var oversized = false; try { sequence.SequenceNumber = 1UL << 48; } catch (ArgumentOutOfRangeException) { oversized = true; }
        Check("counter setters refuse values beyond 48 bits", oversized && sequence.SequenceNumber == 512);
        var ring = new Keyring { Keys = [new SecureKey { Id = "synthetic", Key = key, SequenceNumber = 17 }] };
        Check("keyring round-trip retains valid counters and key bytes", Keyring.FromJson(ring.ToJson()).Keys is [{ SequenceNumber: 17 } loaded] && loaded.Key.SequenceEqual(key));
        return failures;
    }
}
#pragma warning restore CS0618
