# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`LAN.Lib` is a zero-dependency (beyond MS DI/Hosting abstractions) .NET library for LAN peer
discovery: a symmetric UDP announce beacon plus a self-expiring peer table, over a single shared
broadcast domain, filtered by service name. AOT/trim-friendly, `TimeProvider`-driven so cadence and
expiry are deterministic in tests. See `README.md` for the full pitch and usage example.

Discovery is deliberately UDP-only: its whole job is to tell an app *which address:port* to open a
session/control channel (TCP, WebSocket, HTTP, …) to. That channel is the consuming app's concern,
outside this library. So is how an invite travels: **`LanInvites<T>` and `LanGrants` (2.1) carry
nothing over the network.** They hold the RULES of asking a peer's person for something (one invite
at a time, alive only while its asker is, an answer that names what it answers) and of the lasting
capability an acceptance leaves (a hashed bearer token, verified in constant time, revoked), lifted
out of chess's lobby so that TianWen's node, whose request arrives over HTTP and is answered by a
different client of the same machine, keeps chess's rules instead of a second copy of them.

## Commands

The solution file is `LAN.Lib.slnx` (the new XML solution format) at the repo root.

```bash
dotnet restore
dotnet build
dotnet test                                                    # run everything
dotnet test src/LAN.Lib.Tests                                   # test project directly
dotnet test --filter "FullyQualifiedName~LanDiscoveryTests.Bye_RemovesPeer"   # single test
```

`LAN.Lib.csproj` has `GeneratePackageOnBuild=true`, so a plain `dotnet build` also produces a
`.nupkg` under `src/LAN.Lib/bin/<Config>/`.

Package versions are centrally managed in `Directory.Packages.props`
(`ManagePackageVersionsCentrally=true`) — add a new dependency's version there, not as a version
attribute on the `PackageReference` in the `.csproj`. `nuget.config` restricts restore to
`nuget.org` only via package source mapping.

CI (`.github/workflows/dotnet.yml`) runs on push/PR to `main`: restore → build (Release) → test →
upload the `.nupkg` artifact; a second job pushes to nuget.org, gated on **push to `main`** (so a PR
run shows it as `skipping`, which is correct, not a failure). Note what that gating means in practice:
**merging to `main` publishes.** There is no separate release trigger to forget or to fire on purpose.

The version's Major.Minor lives in **exactly one place — `VersionMajorMinor` in the repo-root
`Directory.Build.props`** — and CI reads it back with
`dotnet msbuild Directory.Build.props -getProperty:VersionMajorMinor`, so it cannot drift from what the
packages declare. Bump that one line: minor for a feature addition, major for a breaking change. Do not
restate a version in `LAN.Lib.csproj`; the file holds `VersionMajorMinor`, a conditional
`VersionPrefix` defaulting to `$(VersionMajorMinor).0` for local packs, and an **unconditional**
`AssemblyVersion` of `$(VersionMajorMinor).0.0`. `AssemblyVersion` is unconditional on purpose, because
it is the one value CI does *not* stamp (it passes `-p:Version` and `-p:FileVersion` only), so a csproj
literal would quietly win for assembly identity in the build that actually ships.

The published version is `X.Y.<run_number><run_attempt>+<sha>`, and the run attempt is **concatenated,
not a separate component** — run 12, attempt 1 of `2.0` publishes as `2.0.121`, not `2.0.12.1`. Every
push to `main` therefore republishes the current X.Y under a new build counter, which downstream
`X.Y.*` floating pins pick up on their next restore with no props change anywhere.

**The release notes live in that workflow's `env:` comment block, newest entry last** — in the yml
because entries contain `--`, which XML forbids inside a comment, so `Directory.Build.props` cannot
hold them. Write the entry in the SAME commit as the `VersionMajorMinor` bump. Remembering it
afterwards means either an extra commit or amending a published one, and either way burns a second
build-counter publish of the same X.Y; 2.0 shipped that way, its newest note still describing 1.2.
Say what changed, what is NOT included, and flag a behaviour or wire change explicitly — a consumer
on a floating pin has no other warning.

## Architecture

Everything lives in `src/LAN.Lib` (the library) and `src/LAN.Lib.Tests` (xunit.v3 + Shouldly).
Reading these files in order builds the full picture:

- **`LanProtocol`** — the wire format: plain space-separated ASCII (no JSON/reflection, keeps the
  package AOT/trim-clean), one line per datagram, prefixed with a magic word (`SALAN`) + version so
  a foreign/malformed datagram on the shared port is silently ignored rather than misparsed.
  `ANNOUNCE` carries peer id/service/port/name plus an open `key=value` property bag (URL-encoded
  values) for forward-extensibility without a wire bump; `BYE` carries just the peer id.
  `DiscoveryPort` is **38821**, and the value is load-bearing: it must stay BELOW 49152, because
  49152-65535 is the Windows dynamic range out of which Hyper-V, WSL and Docker carve port exclusions
  (`netsh int ipv4 show excludedportrange protocol=udp`). The original 52821 landed inside one on an
  ordinary box and every bind failed with WSAEACCES. Moving it is wire-incompatible — old and new
  nodes simply never hear each other — which is what made it LAN.Lib 2.0 rather than a minor.

- **`ILanTransport`** — the one abstraction point: `BroadcastAsync(text)` + a `DatagramReceived`
  event, plus `Degradation` — null when healthy, otherwise one sentence saying what this transport
  cannot do. It is a DEFAULT interface member so a fake or bespoke transport need not know the
  concept. `UdpLanTransport` is the real backend (one UDP socket, `ReuseAddress` + `EnableBroadcast`,
  a background receive loop). Its constructor **must not throw on a failed bind**: it runs at DI
  resolution, so throwing took a whole consuming GUI down before its first frame over an optional
  feature. It catches the `SocketException` and degrades to announce-only instead — sending from an
  unbound socket still works, so the node is heard but deaf — which `LanDiscoveryHostedService` logs
  once and `LanDiscovery` re-exposes for a host driving discovery directly. Nothing else acts on it:
  discovery is best-effort by nature and the app must stay usable without it. Tests never touch a real
  socket: `FakeLanBus`/`FakeLanTransport` in
  the test project simulate the shared broadcast domain in-memory, including the self-echo real UDP
  produces (a broadcast reaches the sender's own listener too) — this is what exercises
  `LanDiscovery`'s own-peer-id filter.

- **`LanDiscovery`** — the core: implements `IPeerTable` and owns the beacon timer (via injected
  `TimeProvider`, so tests drive cadence/expiry deterministically with `FakeTimeProvider.Advance`
  instead of real delays), the live peer table (`ConcurrentDictionary<string, LanPeer>` — reads
  from `Peers`/`PeersOf` must be safe against the single datagram-handling writer, but writes
  themselves are already serialized so no extra locking is needed), self-expiry (`Prune`, called
  every beacon tick), and the `Changed` event (fires on add/bye/expire, not on a refresh of a
  known peer).

- **`LanIdentity`** — two distinct ids, easy to conflate: `PeerId` is minted fresh per process and
  never persisted (its only job is the self-echo filter — persisting it was what made two instances
  on one machine share an id and silently ignore each other). `NodeId` is the opposite: minted once
  and persisted to `StableNodeIdPath` so a consumer can recognise a node across restarts as its
  address changes; empty for a pure listen-only consumer.

- **`LanPeer`** — the peer record; `ResolveLabels` progressively disambiguates look-alike display
  names across a peer list (name → + machine name → + ascending-PID suffix), only adding as much
  detail as needed to keep a unique name clean.

- **`LanInvites<T>`**: the answering side of an invite, transport-free: a single slot (a second
  offer is refused while one waits), an invite withdrawn by the app (`Withdraw`, a socket closing) or
  by a presence lapse (`Seen` on each poll), an answer by id so a stale answer accepts nothing, and
  the outcome readable by the asker for `OutcomeRetention`. The whole state is ONE immutable record
  replaced by compare-and-swap, never a lock, because a UI reads `Pending` every frame. **An update
  reads the state BEFORE the clock**: `LanInvitesTests` races two offers by running the second from
  inside the clock read, which is the only way to put it between the first's read and its write
  deterministically (a many-threads test never overlapped them and passed with the CAS deleted).

- **`LanGrants`**: the lasting capability an acceptance turns into: a 256-bit bearer token minted
  once and kept only as its SHA-256 (through the app's `ILanGrantStore`), `TryVerify` in constant
  time over a lock-free array, and grant and revoke serialized by one `SemaphoreSlim` and saved BEFORE
  they take effect in memory, so a refused save changes nothing and two racing writes lose nothing.

- **`LanHostNames`**: who an address is, by name, for a peer that cannot ask: the reverse lookup's name
  counted only when it resolves back to the same address (forward-confirmed; an IPv4 address seen
  through an IPv6 socket is the IPv4 address). Cached per address, a miss included, and bounded by its
  own budget, never the caller's token, so a caller that gives up leaves the answer for the next and a
  resolver that ignores its token still cannot hold a request. `DnsHostNameResolver` is the machine's
  resolver; tests answer through `IHostNameResolver`.

- **`LanClockOffset`** (2.2): how far a peer's clock is ahead of this one, transport-free (the caller stamps its
  clock as a request leaves and as the answer arrives, the peer stamps its own into the answer). NTP's midpoint,
  taken from the sample with the SHORTEST round trip of the last `Window` (a slow answer says as much about the
  network as about the clock; the newest wins a tie, so a stepped clock is followed within a window). **A
  `MinValue` or `MaxValue` is a peer's "never" and "no end", not a time, and `ToLocal`/`ToPeer` return it as it is**:
  shifting `MinValue` by a negative span threw on the render thread of every window attaching to a running rig.
  Any other time is clamped to the calendar. Lock-free (one immutable state, compare-and-swap), since a UI reads it
  every frame.

- **`LanDiscoveryOptions`** / **`ServiceCollectionExtensions.AddLanDiscovery`** — DI wiring.
  `ILanTransport` is registered with `TryAddSingleton` specifically so a test (or an app with a
  bespoke transport) can register its own beforehand and have it left in place.
  **`LanDiscoveryHostedService`** just bookends `LanDiscovery`'s lifetime for the generic host
  (`StartAsync` on startup, a best-effort `SendByeAsync` on shutdown) — the beacon timer itself
  drives the actual cadence, not this service's loop.

## Testing conventions

- `LanDiscovery` needs no host and no real sockets — construct it directly with a
  `FakeLanTransport` and a `FakeTimeProvider` (see `LanDiscoveryTests.NewDiscovery`).
- Async test methods that call `StartAsync`/`SendByeAsync` should pass
  `TestContext.Current.CancellationToken` (xunit.v3 convention; the analyzer flags a bare call).
