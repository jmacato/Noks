using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Noks.WebRtc;

public sealed class DtlsSession : IDisposable
{
    private const int SrtpProfile = SrtpProtectionProfile.SRTP_AES128_CM_HMAC_SHA1_80;
    private const int ExportedKeyingMaterialLength = 2 * (16 + 14);
    private readonly DtlsTransport _transport;
    private readonly CancellationTokenSource _shutdown;
    private readonly Task _postHandshake;
    private bool _disposed;

    private DtlsSession(DtlsTransport transport, DtlsKeyingMaterial keys)
    {
        _transport = transport;
        Keys = keys;
        _shutdown = new CancellationTokenSource();
        _postHandshake = Task.Run(ReceivePostHandshakeRecords);
    }

    public DtlsKeyingMaterial Keys { get; }
    /// <summary>Completes when the session is disposed, or faults when a post-handshake DTLS error occurs.</summary>
    public Task Completion => _postHandshake;

    public static async Task<DtlsSession> HandshakeAsync(
        DtlsIdentity identity,
        bool isClient,
        string remoteFingerprint,
        ChannelReader<byte[]> incoming,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteFingerprint);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(send);

        var transport = new ChannelDatagramTransport(incoming, send, token);
        try
        {
            var peer = isClient
                ? (IDtlsPeer)new WebRtcDtlsClient(identity, remoteFingerprint)
                : new WebRtcDtlsServer(identity, remoteFingerprint);
            DtlsTransport dtlsTransport = await Task.Run(() =>
            {
                if (peer is WebRtcDtlsClient client)
                    return new DtlsClientProtocol().Connect(client, transport);

                var protocol = new DtlsServerProtocol { VerifyRequests = false };
                return protocol.Accept((WebRtcDtlsServer)peer, transport);
            }, token).ConfigureAwait(false);

            if (!peer.SrtpWasNegotiated)
                throw new InvalidDataException("The peer did not negotiate SRTP_AES128_CM_HMAC_SHA1_80.");

            var exported = peer.ExportedKeyingMaterial ?? throw new InvalidDataException("DTLS peer did not export SRTP keying material.");
            var keys = DtlsKeyingMaterial.FromExporter(exported);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(exported);
            transport.DetachHandshakeCancellation();
            return new DtlsSession(dtlsTransport, keys);
        }
        catch
        {
            transport.Close();
            throw;
        }
    }

    private void ReceivePostHandshakeRecords()
    {
        var buffer = new byte[2048];
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                // DtlsTransport processes retransmitted Finished records and close alerts here.
                _transport.Receive(buffer, 0, buffer.Length, 250);
            }
        }
        catch (IOException) when (_shutdown.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shutdown.Cancel();
        try { _transport.Close(); } catch (IOException) { }
        try { _postHandshake.GetAwaiter().GetResult(); } catch (IOException) { }
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(Keys.ClientKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(Keys.ServerKey);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(Keys.ClientSalt);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(Keys.ServerSalt);
        _shutdown.Dispose();
    }

    public sealed class DtlsKeyingMaterial
    {
        private DtlsKeyingMaterial(byte[] clientKey, byte[] serverKey, byte[] clientSalt, byte[] serverSalt)
        {
            ClientKey = clientKey;
            ServerKey = serverKey;
            ClientSalt = clientSalt;
            ServerSalt = serverSalt;
        }

        public byte[] ClientKey { get; }
        public byte[] ServerKey { get; }
        public byte[] ClientSalt { get; }
        public byte[] ServerSalt { get; }

        internal static DtlsKeyingMaterial FromExporter(byte[] material)
        {
            if (material.Length != ExportedKeyingMaterialLength)
                throw new InvalidDataException("DTLS-SRTP exporter returned an invalid key block length.");
            // RFC 5764 section 4.2: client_write_key | server_write_key | client_write_salt | server_write_salt.
            return new DtlsKeyingMaterial(material[0..16], material[16..32], material[32..46], material[46..60]);
        }
    }

    private interface IDtlsPeer
    {
        TlsContext? Context { get; }
        bool SrtpWasNegotiated { get; }
        byte[]? ExportedKeyingMaterial { get; }
    }

    private static void CheckSrtp(UseSrtpData? extension)
    {
        ValidateSrtp(extension is not null, extension?.ProtectionProfiles, extension?.Mki);
    }

    internal static void ValidateSrtp(bool offered, int[]? profiles, byte[]? mki)
    {
        if (!offered || mki is null || profiles is null || mki.Length != 0 || profiles.Length != 1 || profiles[0] != SrtpProfile)
            throw new InvalidDataException("The peer did not negotiate SRTP_AES128_CM_HMAC_SHA1_80.");
    }

    private sealed class WebRtcDtlsClient : DefaultTlsClient, IDtlsPeer
    {
        public WebRtcDtlsClient(DtlsIdentity identity, string remoteFingerprint) : base(new BcTlsCrypto())
        {
            Identity = identity;
            RemoteFingerprint = Fingerprint.Parse(remoteFingerprint);
        }

        private DtlsIdentity Identity { get; }
        private byte[] RemoteFingerprint { get; }
        public bool SrtpWasNegotiated { get; private set; }
        public TlsContext? Context { get; private set; }
        public byte[]? ExportedKeyingMaterial { get; private set; }

        public override void Init(TlsClientContext context) { base.Init(context); Context = context; }
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.DTLSv12];
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256];
        public override IDictionary<int, byte[]> GetClientExtensions()
        {
            var extensions = base.GetClientExtensions();
            TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData([SrtpProfile], []));
            return extensions;
        }
        public override void ProcessServerExtensions(IDictionary<int, byte[]> extensions)
        {
            base.ProcessServerExtensions(extensions);
            try { CheckSrtp(TlsSrtpUtilities.GetUseSrtpExtension(extensions)); }
            catch (InvalidDataException exception) { throw new TlsFatalAlert(AlertDescription.illegal_parameter, exception); }
            SrtpWasNegotiated = true;
        }
        public override TlsAuthentication GetAuthentication() => new Authentication(Identity, RemoteFingerprint, Context!);
        public override void NotifyHandshakeComplete() => ExportedKeyingMaterial = Context!.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, ExportedKeyingMaterialLength);
    }

    private sealed class WebRtcDtlsServer : DefaultTlsServer, IDtlsPeer
    {
        public WebRtcDtlsServer(DtlsIdentity identity, string remoteFingerprint) : base(new BcTlsCrypto())
        {
            Identity = identity;
            RemoteFingerprint = Fingerprint.Parse(remoteFingerprint);
        }

        private DtlsIdentity Identity { get; }
        private byte[] RemoteFingerprint { get; }
        public bool SrtpWasNegotiated { get; private set; }
        public TlsContext? Context { get; private set; }
        public byte[]? ExportedKeyingMaterial { get; private set; }
        public override void Init(TlsServerContext context) { base.Init(context); Context = context; }
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.DTLSv12];
        protected override int[] GetSupportedCipherSuites() => [CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256];
        public override void ProcessClientExtensions(IDictionary<int, byte[]> extensions)
        {
            base.ProcessClientExtensions(extensions);
            try
            {
                UseSrtpData? offered = TlsSrtpUtilities.GetUseSrtpExtension(extensions);
                if (offered is null || offered.Mki.Length != 0 || !offered.ProtectionProfiles.Contains(SrtpProfile))
                    throw new InvalidDataException("The peer did not offer SRTP_AES128_CM_HMAC_SHA1_80.");
            }
            catch (InvalidDataException exception) { throw new TlsFatalAlert(AlertDescription.illegal_parameter, exception); }
        }
        public override IDictionary<int, byte[]> GetServerExtensions()
        {
            var extensions = base.GetServerExtensions();
            TlsSrtpUtilities.AddUseSrtpExtension(extensions, new UseSrtpData([SrtpProfile], []));
            SrtpWasNegotiated = true;
            return extensions;
        }
        public override TlsCredentials GetCredentials() => new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(Context!), (BcTlsCrypto)Crypto, Identity.PrivateKey, Identity.Certificate,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
        public override CertificateRequest GetCertificateRequest() => new(
            [ClientCertificateType.ecdsa_sign],
            [SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)], null);
        public override void NotifyClientCertificate(Certificate certificate)
        {
            if (certificate.IsEmpty || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(certificate.GetCertificateAt(0).GetEncoded()), RemoteFingerprint))
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
        public override void NotifyHandshakeComplete() => ExportedKeyingMaterial = Context!.ExportKeyingMaterial(ExporterLabel.dtls_srtp, null, ExportedKeyingMaterialLength);
    }

    private sealed class Authentication : TlsAuthentication
    {
        private readonly DtlsIdentity _identity;
        private readonly byte[] _fingerprint;
        private readonly TlsContext _context;
        public Authentication(DtlsIdentity identity, byte[] fingerprint, TlsContext context) { _identity = identity; _fingerprint = fingerprint; _context = context; }
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var certificate = serverCertificate.Certificate;
            if (certificate.IsEmpty || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Security.Cryptography.SHA256.HashData(certificate.GetCertificateAt(0).GetEncoded()), _fingerprint))
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
        }
        public TlsCredentials GetClientCredentials(CertificateRequest certificateRequest) => new Org.BouncyCastle.Tls.Crypto.Impl.BC.BcDefaultTlsCredentialedSigner(
            new TlsCryptoParameters(_context), (BcTlsCrypto)_context.Crypto, _identity.PrivateKey, _identity.Certificate,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa));
    }

    private sealed class ChannelDatagramTransport : DatagramTransport
    {
        private readonly ChannelReader<byte[]> _incoming;
        private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _send;
        private CancellationTokenSource _closed;
        public ChannelDatagramTransport(ChannelReader<byte[]> incoming, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> send, CancellationToken token)
        {
            _incoming = incoming; _send = send; _closed = CancellationTokenSource.CreateLinkedTokenSource(token);
        }
        public int GetReceiveLimit() => 65535;
        public int GetSendLimit() => 65535;
        public void DetachHandshakeCancellation()
        {
            var old = Interlocked.Exchange(ref _closed, new CancellationTokenSource());
            old.Dispose();
        }
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            var temporary = new byte[buffer.Length];
            var received = Receive(temporary, 0, temporary.Length, waitMillis);
            if (received > 0) temporary.AsSpan(0, received).CopyTo(buffer);
            return received;
        }
        public int Receive(byte[] buffer, int offset, int length, int waitMillis)
        {
            var closed = Volatile.Read(ref _closed);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            timeout.CancelAfter(waitMillis);
            try
            {
                while (_incoming.WaitToReadAsync(timeout.Token).AsTask().GetAwaiter().GetResult())
                {
                    if (!_incoming.TryRead(out var datagram)) continue;
                    if (datagram.Length > length) throw new IOException("DTLS datagram exceeds receive buffer.");
                    Buffer.BlockCopy(datagram, 0, buffer, offset, datagram.Length);
                    return datagram.Length;
                }
                throw new IOException("DTLS input completed.");
            }
            catch (OperationCanceledException) when (!closed.IsCancellationRequested) { return -1; }
            catch (OperationCanceledException) { throw new IOException("DTLS transport closed."); }
        }
        public void Send(byte[] buffer, int offset, int length) => _send(buffer.AsMemory(offset, length).ToArray(), Volatile.Read(ref _closed).Token).AsTask().GetAwaiter().GetResult();
        public void Send(ReadOnlySpan<byte> buffer) => _send(buffer.ToArray(), Volatile.Read(ref _closed).Token).AsTask().GetAwaiter().GetResult();
        public void Close() { var closed = Volatile.Read(ref _closed); if (!closed.IsCancellationRequested) closed.Cancel(); }
    }

    private static class Fingerprint
    {
        internal static byte[] Parse(string value)
        {
            var normalized = value.Trim();
            if (normalized.StartsWith("sha-256 ", StringComparison.OrdinalIgnoreCase)) normalized = normalized[8..].Trim();
            var hex = normalized.Replace(":", string.Empty, StringComparison.Ordinal);
            if (hex.Length != 64 || !hex.All(Uri.IsHexDigit)) throw new ArgumentException("Expected an SDP SHA-256 fingerprint.", nameof(value));
            return Convert.FromHexString(hex);
        }
    }
}
