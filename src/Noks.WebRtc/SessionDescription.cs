using System.Globalization;
using System.Text;

namespace Noks.WebRtc;

internal sealed record SessionDescription(string Ufrag, string Password, string Fingerprint,
    string Setup, string Mid, int PayloadType, IReadOnlyList<string> Candidates, string Direction = "sendrecv")
{
    public static SessionDescription Parse(string sdp)
    {
        if (sdp.Length is 0 or > 262144)
            throw new FormatException("Invalid SDP size.");
        string? ufrag = null, password = null, fingerprint = null, setup = null, mid = null;
        bool audio = false, mux = false;
        string direction = "sendrecv";
        HashSet<int> formats = [];
        int? payload = null;
        List<string> candidates = [];
        foreach (string raw in sdp.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                string[] parts = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (audio || parts.Length < 4 || parts[0] != "audio" || parts[1] == "0" || parts[2] != "UDP/TLS/RTP/SAVPF")
                    throw new NotSupportedException("A single active DTLS-SRTP audio track is required.");
                audio = true;
                foreach (string part in parts.Skip(3))
                    if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int pt) && pt is >= 0 and <= 127)
                        formats.Add(pt);
            }
            else if (line.StartsWith("a=ice-ufrag:", StringComparison.Ordinal)) ufrag = line[12..];
            else if (line.StartsWith("a=ice-pwd:", StringComparison.Ordinal)) password = line[10..];
            else if (line.StartsWith("a=fingerprint:", StringComparison.Ordinal))
            {
                if (!line.StartsWith("a=fingerprint:sha-256 ", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("A SHA-256 certificate fingerprint is required.");
                fingerprint = line[22..].Trim();
            }
            else if (line.StartsWith("a=setup:", StringComparison.Ordinal)) setup = line[8..];
            else if (line.StartsWith("a=mid:", StringComparison.Ordinal)) mid = line[6..];
            else if (line == "a=rtcp-mux") mux = true;
            else if (line.StartsWith("a=rtpmap:", StringComparison.Ordinal))
            {
                string[] parts = line[9..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && (parts[1].Equals("PCMU/8000", StringComparison.OrdinalIgnoreCase) ||
                    parts[1].Equals("PCMU/8000/1", StringComparison.OrdinalIgnoreCase)) &&
                    int.TryParse(parts[0], out int pt) && formats.Contains(pt)) payload = pt;
            }
            else if (line.StartsWith("a=candidate:", StringComparison.Ordinal) && candidates.Count < 128) candidates.Add(line[2..]);
            else if (line is "a=inactive" or "a=sendonly" or "a=recvonly" or "a=sendrecv")
                direction = line[2..];
        }
        payload ??= formats.Contains(0) ? 0 : null;
        if (!audio || !mux || payload is null || string.IsNullOrWhiteSpace(mid) ||
            ufrag is null || ufrag.Length is < 4 or > 256 || password is null || password.Length is < 22 or > 256 ||
            fingerprint is null || setup is not ("actpass" or "active" or "passive"))
            throw new FormatException("SDP lacks required PCMU, ICE, DTLS, or RTCP parameters.");
        if (mid.Length > 32 || mid.Any(char.IsWhiteSpace) || ufrag.Any(char.IsWhiteSpace) || password.Any(char.IsWhiteSpace))
            throw new FormatException("SDP contains invalid identifiers.");
        string[] fingerprintBytes = fingerprint.Split(':');
        if (fingerprintBytes.Length != 32 || fingerprintBytes.Any(value => value.Length != 2 || !value.All(Uri.IsHexDigit)))
            throw new FormatException("SDP requires a 32-byte SHA-256 fingerprint.");
        if (payload.Value is >= 64 and <= 95)
            throw new FormatException("The RTP payload type conflicts with RTCP multiplexing.");
        return new(ufrag, password, fingerprint, setup, mid, payload.Value, candidates, direction);
    }

    public static string Create(string ufrag, string password, string fingerprint, string mid, string setup,
        int payload, uint ssrc, IEnumerable<string> candidates, long version, string direction = "sendrecv")
    {
        StringBuilder text = new();
        foreach (string line in new[] { "v=0", $"o=- 1 {version} IN IP4 0.0.0.0", "s=Noks", "t=0 0", $"a=group:BUNDLE {mid}",
            "a=msid-semantic: WMS noks", $"m=audio 9 UDP/TLS/RTP/SAVPF {payload}", "c=IN IP4 0.0.0.0", $"a=mid:{mid}",
            $"a=ice-ufrag:{ufrag}", $"a=ice-pwd:{password}", "a=ice-options:trickle", $"a=fingerprint:sha-256 {fingerprint}",
            $"a=setup:{setup}", $"a={direction}", "a=rtcp-mux", $"a=rtpmap:{payload} PCMU/8000", "a=ptime:20", "a=maxptime:20",
            "a=msid:noks audio", $"a=ssrc:{ssrc} cname:noks", $"a=ssrc:{ssrc} msid:noks audio" })
            text.Append(line).Append("\r\n");
        foreach (string candidate in candidates) text.Append("a=").Append(candidate).Append("\r\n");
        return text.ToString();
    }
}
