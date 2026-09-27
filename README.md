# LAN.Lib

Zero-dependency **LAN peer discovery** for .NET: a symmetric UDP announce beacon plus a
self-expiring peer table, over a single shared broadcast domain, filtered by service name.
`TimeProvider`-driven (so beacon cadence and expiry are deterministic in tests), AOT/trim
friendly, with a one-line DI extension for the .NET generic host. Beside it, the two halves of
**asking a peer's person for something**: the invite handshake's answering side, and the grants an
accepted invite leaves behind.

Extracted and generalised from the LAN play in the `chess` project.

## What it does

- **Symmetric discovery.** Every node broadcasts a small ASCII announce beacon (1 s) *and*
  listens for everyone else's, keeping a live table of peers that self-expires (5 s) when a
  node goes quiet. Both cadences run off an injected `TimeProvider`.
- **One shared broadcast domain.** All apps broadcast on **one** UDP port (`38821`) with one
  magic prefix; a consumer filters to the service it cares about (`IPeerTable.PeersOf("...")`).
  `ReuseAddress` lets several apps share the port on one host — no port-per-app.
- **Every interface, not just the routed one.** The announce goes out once *per* up, non-loopback
  interface, to that interface's directed broadcast address. A single send to `255.255.255.255`
  leaves a multi-homed host on whichever interface wins the route — commonly a Hyper-V, WSL,
  Docker or VPN adapter rather than the LAN — while receiving (bound to `Any`) still hears every
  interface. That asymmetry made such a host *see* its peers while staying invisible to them.
- **Stable node identity.** A node can mint a persisted node id once (`StableNodeIdPath`) and
  advertise it in every beacon, so a consumer can bind to a node and recognise it across
  restarts even as its address changes. Separate from the per-process *peer id*, which exists
  only to filter a node's own beacon echo.
- **Extensible.** The announce carries an open `key=value` property bag (URL-encoded values),
  so an app can advertise extra facts — a version, a capability flag — with no wire-format bump.
- **Foreign-datagram safe.** A magic + version prefix means a datagram that isn't ours (or is
  malformed) is ignored, never misparsed — the shared port coexists with anything else on it.

Discovery is **UDP-only** by design: its whole job is to tell an app *which address:port* to
open a session/control channel to (TCP, WebSocket, HTTP, …). That channel is the app's concern.

## Invites and grants

Neither carries anything over the network: an invite arrives however the app receives it (chess:
a line on a TCP connection; TianWen: an HTTP route, answered by a different client on the same
machine), and the rules around it are what is shared.

- **`LanInvites<T>`** is the answering side. **One invite waits at a time**, since a person answers
  one question at a time, so a second `TryOffer` is refused while one waits. **An invite lives only
  while its asker is there**: the app calls `Withdraw` when it learns the asker left (a socket
  closing), or `Seen` on each poll, with a presence lapse after which an unseen invite is withdrawn,
  so nothing is granted to someone who walked away. **An answer names the invite it answers**, so an
  answer meant for a withdrawn invite never accepts the one that replaced it. The asker reads the
  outcome back by id (`OutcomeOf`) for `OutcomeRetention` after the answer. Every read is lock-free
  (the state is one immutable value replaced by compare-and-swap), so a UI may read `Pending` every
  frame.
- **`LanGrants`** is what an acceptance that should last turns into: `GrantAsync` mints a 256-bit
  bearer token and hands it out once, the store (`ILanGrantStore`, the app's own storage) keeps only
  its SHA-256, `TryVerify` checks a presented token in constant time, and `RevokeAsync` ends it. A
  write the store refuses did not happen, and writes are one at a time so none is lost. Over plain
  HTTP a token can be read off the wire: this stops "anyone who can reach the port", not a hostile
  network, which is TLS's job.

```csharp
var invites = new LanInvites<Requester>(TimeProvider.System, presenceLapse: TimeSpan.FromSeconds(15));
using var grants = new LanGrants(new MyGrantFile(path), TimeProvider.System);
await grants.LoadAsync(ct);

// The asker's request arrives:
if (!invites.TryOffer("Laptop (Seb)", requester, out var invite)) { /* busy: answering another */ }

// The person answers, on the machine that owns the thing asked for:
if (invites.Answer(invite.Id, accept: true) is { } accepted)
{
    var issued = await grants.GrantAsync(accepted.Label, ct);   // hand issued.Token to the asker, once
}

// Every later request:
if (!grants.TryVerify(presentedToken, out var grant)) { /* refused */ }
```

## Usage

```csharp
using LAN.Lib;

// On a node that others discover and bind to (e.g. a headless server):
services.AddLanDiscovery(o =>
{
    o.ServiceName = "tianwen-server";                 // announced + filtered on
    o.ServicePort = 1888;                             // the port peers should reach us at
    o.NodeName = Environment.MachineName;             // human display name
    o.StableNodeIdPath = Path.Combine(appData, "node-id.txt"); // mint-once, persisted
});

// On a consumer that only wants to see who's out there (never appears itself):
services.AddLanDiscovery(o =>
{
    o.ServiceName = "tianwen-gui";
    o.Announce = false;                               // listen-only
});

// Anywhere, read the live peer list (a fresh snapshot each call; Changed hints when to re-read):
IPeerTable peers = provider.GetRequiredService<IPeerTable>();
foreach (var peer in peers.PeersOf("tianwen-server"))
    Console.WriteLine($"{peer.DisplayName} @ {peer.EndPoint}  (node {peer.NodeId})");
```

`AddLanDiscovery` registers `ILanTransport` (via `TryAdd`, so a bespoke/fake transport can be
registered first), the `LanDiscovery` beacon + peer table (as `IPeerTable`), and a hosted
service that runs them for the host's lifetime (sending a polite *bye* on shutdown).

## Testing

`LanDiscovery` needs no host and no real sockets: construct it directly with an in-memory
`ILanTransport` and a `FakeTimeProvider`, then `Advance` the clock to drive beacon cadence and
expiry deterministically. `LanInvites<T>` and `LanGrants` need neither: a `FakeTimeProvider` and,
for grants, an in-memory `ILanGrantStore`. See `src/LAN.Lib.Tests`.

## License

MIT — see [LICENSE](LICENSE).
