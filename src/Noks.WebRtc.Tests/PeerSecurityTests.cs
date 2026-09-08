using System.Threading.Channels;
using System.Text.Json;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.X509;

namespace Noks.WebRtc.Tests;

public sealed class PeerSecurityTests
{
    [Fact]
    public async Task Tampered_fingerprint_fails_without_audio_delivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var alice = new ManagedPeerConnection(true);
        await using var bob = new ManagedPeerConnection(false);
        var toAlice = Channel.CreateUnbounded<WebRtcSignal>(); var toBob = Channel.CreateUnbounded<WebRtcSignal>();
        var audio = 0;
        bob.AudioReceived += _ => Interlocked.Increment(ref audio);
        alice.Signal += s => toBob.Writer.TryWrite(s.Kind == WebRtcSignalKind.Offer ? s with { Json = s.Json.Replace(alice.LocalFingerprint, string.Join(':', Enumerable.Repeat("00", 32)), StringComparison.Ordinal) } : s);
        bob.Signal += s => toAlice.Writer.TryWrite(s);
        await bob.StartAsync(timeout.Token); await alice.StartAsync(timeout.Token);
        Task Relay(ChannelReader<WebRtcSignal> r, ManagedPeerConnection p) => Task.Run(async () => { try { await foreach (var s in r.ReadAllAsync(timeout.Token)) await p.ApplySignalAsync(s, timeout.Token); } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { } });
        var ra = Relay(toAlice.Reader, alice); var rb = Relay(toBob.Reader, bob);
        await Assert.ThrowsAnyAsync<Exception>(() => Task.WhenAll(alice.WaitForConnectedAsync(timeout.Token), bob.WaitForConnectedAsync(timeout.Token)));
        Assert.Equal(0, Volatile.Read(ref audio));
        timeout.Cancel(); await Task.WhenAll(ra, rb);
    }

    [Fact]
    public async Task Browser_style_same_transport_reoffer_keeps_the_existing_association()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var alice = new ManagedPeerConnection(true); await using var bob = new ManagedPeerConnection(false);
        var toAlice = Channel.CreateUnbounded<WebRtcSignal>(); var toBob = Channel.CreateUnbounded<WebRtcSignal>();
        WebRtcSignal? bobAnswer = null, reofferAnswer = null; var dropAlice = false;
        alice.Signal += s => { if (dropAlice && s.Kind == WebRtcSignalKind.Answer) reofferAnswer = s; else if (!dropAlice) toBob.Writer.TryWrite(s); };
        bob.Signal += s => { if (s.Kind == WebRtcSignalKind.Answer) bobAnswer ??= s; toAlice.Writer.TryWrite(s); };
        await bob.StartAsync(timeout.Token); await alice.StartAsync(timeout.Token);
        Task Relay(ChannelReader<WebRtcSignal> r, ManagedPeerConnection p) => Task.Run(async () => { try { await foreach (var s in r.ReadAllAsync(timeout.Token)) await p.ApplySignalAsync(s, timeout.Token); } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { } });
        var ra = Relay(toAlice.Reader, alice); var rb = Relay(toBob.Reader, bob);
        await Task.WhenAll(alice.WaitForConnectedAsync(timeout.Token), bob.WaitForConnectedAsync(timeout.Token));
        Assert.NotNull(bobAnswer); dropAlice = true;
        using var doc = JsonDocument.Parse(bobAnswer!.Json);
        var reofferSdp = doc.RootElement.GetProperty("sdp").GetString()!
            .Replace("a=setup:passive", "a=setup:actpass", StringComparison.Ordinal)
            .Replace("a=setup:active", "a=setup:actpass", StringComparison.Ordinal)
            .Replace("a=sendonly", "a=sendrecv", StringComparison.Ordinal)
            .Replace("a=recvonly", "a=sendrecv", StringComparison.Ordinal);
        await alice.ApplySignalAsync(new WebRtcSignal(WebRtcSignalKind.Offer, JsonSerializer.Serialize(new { type = "offer", sdp = reofferSdp })), timeout.Token);
        Assert.NotNull(reofferAnswer);
        using (var answer = JsonDocument.Parse(reofferAnswer!.Json))
            Assert.DoesNotContain("a=setup:actpass", answer.RootElement.GetProperty("sdp").GetString()!, StringComparison.Ordinal);
        Assert.Equal(WebRtcState.Connected, alice.State); Assert.Equal(WebRtcState.Connected, bob.State);
        await alice.SendAudioAsync(ManagedPeerConnectionTests.Tone(0, 440), timeout.Token);
        await bob.SendAudioAsync(ManagedPeerConnectionTests.Tone(0, 660), timeout.Token);
        await Task.Delay(100, timeout.Token);
        Assert.True(alice.Statistics.ReceivedPackets > 0); Assert.True(bob.Statistics.ReceivedPackets > 0);
        timeout.Cancel(); await Task.WhenAll(ra, rb);
    }

    [Fact]
    public void Dtls_certificate_uses_named_prime256v1_parameters()
    {
        using var identity = new DtlsIdentity();
        var certificate = new X509CertificateParser().ReadCertificate(identity.CertificateDer);
        var parameters = certificate.CertificateStructure.SubjectPublicKeyInfo.Algorithm.Parameters.ToAsn1Object();
        Assert.Equal(X9ObjectIdentifiers.Prime256v1, DerObjectIdentifier.GetInstance(parameters));
    }
}
