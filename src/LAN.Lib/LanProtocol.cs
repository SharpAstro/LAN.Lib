using System;
using System.Collections.Generic;
using System.Text;

namespace LAN.Lib;

/// <summary>The kind of a decoded discovery message. <see cref="Unknown"/> is a foreign/garbled
/// datagram that must be ignored — the discovery port is deliberately shared across every SharpAstro
/// app, so a datagram that isn't one of ours (or is malformed) is simply skipped.</summary>
public enum LanMessageKind
{
    Unknown,
    Announce,   // "I'm here: this is my service, name, port and properties"
    Bye,        // "I'm leaving" (prompt removal; expiry is the fallback)
}

/// <summary>
/// A decoded discovery message. <see cref="Kind"/> says which fields are meaningful:
/// Announce carries PeerId/Service/Port/Name + a <see cref="Properties"/> bag; Bye carries PeerId only.
/// </summary>
public readonly record struct LanMessage(
    LanMessageKind Kind,
    string PeerId = "",
    string Service = "",
    int Port = 0,
    string Name = "",
    IReadOnlyDictionary<string, string>? Properties = null);

/// <summary>
/// The discovery wire format — deliberately plain, space-separated ASCII text (no reflection-JSON, so
/// the library stays AOT/trim-clean). Every datagram is one line prefixed with a magic word + version
/// so a foreign datagram on the shared discovery port is ignored rather than misparsed. Free text (the
/// display name and property values) is URL-encoded so it can never contain a token-splitting space.
///
/// <para><b>Shared broadcast domain.</b> All SharpAstro apps broadcast on the one
/// <see cref="DiscoveryPort"/> with the one <see cref="Magic"/>; a consumer filters the peers it cares
/// about by <see cref="LanMessage.Service"/> (see <see cref="IPeerTable.PeersOf"/>). This keeps a single
/// socket per host (<c>ReuseAddress</c> lets several apps share it) instead of a port per app.</para>
///
/// <para><b>Extensibility.</b> The ANNOUNCE verb carries an open <c>key=value</c> property bag after its
/// fixed fields, so an app can advertise extra facts (a stable node id, a version, a capability flag)
/// without a wire-format bump. Well-known keys are defined here (<see cref="PropNodeId"/> etc.); anything
/// else rides along in <see cref="LanMessage.Properties"/> untouched.</para>
/// </summary>
public static class LanProtocol
{
    /// <summary>Magic prefix identifying our datagrams on the shared port.</summary>
    public const string Magic = "SALAN";

    /// <summary>Protocol version — bumped only on an incompatible wire change.</summary>
    public const int Version = 1;

    /// <summary>Fixed UDP port every SharpAstro app broadcasts/listens on for discovery.</summary>
    public const int DiscoveryPort = 52821;

    /// <summary>Well-known property key: a stable, persisted node identity (survives restarts).</summary>
    public const string PropNodeId = "node";

    /// <summary>Well-known property key: the announcer's machine name (for disambiguating look-alikes).</summary>
    public const string PropMachineName = "mach";

    /// <summary>Well-known property key: the announcer's OS process id.</summary>
    public const string PropPid = "pid";

    private static readonly IReadOnlyDictionary<string, string> EmptyProps =
        new Dictionary<string, string>(0);

    /// <summary>Encode an ANNOUNCE datagram. <paramref name="properties"/> keys must be simple
    /// identifiers (letters/digits/underscore, no spaces or <c>=</c>); values are URL-encoded and may
    /// contain anything.</summary>
    public static string EncodeAnnounce(
        string peerId, string service, int port, string name,
        IReadOnlyDictionary<string, string>? properties = null)
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append(' ').Append(Version).Append(" ANNOUNCE ")
          .Append(peerId).Append(' ').Append(Encode(service)).Append(' ')
          .Append(port).Append(' ').Append(Encode(name));

        if (properties is not null)
        {
            foreach (var kv in properties)
            {
                sb.Append(' ').Append(kv.Key).Append('=').Append(Encode(kv.Value));
            }
        }

        return sb.ToString();
    }

    public static string EncodeBye(string peerId) => $"{Magic} {Version} BYE {peerId}";

    /// <summary>Parse one datagram. Returns <see cref="LanMessageKind.Unknown"/> for anything that
    /// isn't a well-formed message of ours (never throws).</summary>
    public static LanMessage Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return default;

        var t = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Need at least: magic, version, verb.
        if (t.Length < 3 || t[0] != Magic)
            return default;

        // t[1] is the version; an unknown future version still parses best-effort by verb.
        return t[2] switch
        {
            // magic, version, verb, peerId, service, port, name (= 7 tokens), then optional k=v props.
            "ANNOUNCE" when t.Length >= 7 =>
                new LanMessage(LanMessageKind.Announce, PeerId: t[3], Service: Decode(t[4]),
                    Port: ParseInt(t[5]), Name: Decode(t[6]), Properties: ParseProps(t, 7)),
            "BYE" when t.Length >= 4 =>
                new LanMessage(LanMessageKind.Bye, PeerId: t[3]),
            _ => default,
        };
    }

    private static IReadOnlyDictionary<string, string> ParseProps(string[] tokens, int start)
    {
        if (start >= tokens.Length)
            return EmptyProps;

        Dictionary<string, string>? props = null;
        for (var i = start; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            // A URL-encoded value can never contain a raw '=', so the first '=' always splits key/value.
            var eq = tok.IndexOf('=');
            if (eq <= 0)
                continue; // malformed token — skip, never throw
            props ??= new Dictionary<string, string>();
            props[tok[..eq]] = Decode(tok[(eq + 1)..]);
        }

        return props ?? EmptyProps;
    }

    // Empty strings would produce a zero-length token that RemoveEmptyEntries drops, shifting every
    // field after it — so an empty string is encoded as a "-" sentinel (and decoded back to empty).
    private static string Encode(string s) => string.IsNullOrEmpty(s) ? "-" : Uri.EscapeDataString(s);
    private static string Decode(string s) => s == "-" ? "" : Uri.UnescapeDataString(s);

    private static int ParseInt(string s) => int.TryParse(s, out var v) ? v : 0;
}
