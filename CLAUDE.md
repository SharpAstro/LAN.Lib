# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

`LAN.Lib` is a zero-dependency (beyond MS DI/Hosting abstractions) .NET library for LAN peer
discovery: a symmetric UDP announce beacon plus a self-expiring peer table, over a single shared
broadcast domain, filtered by service name. AOT/trim-friendly, `TimeProvider`-driven so cadence and
expiry are deterministic in tests. See `README.md` for the full pitch and usage example.

Discovery is deliberately UDP-only: its whole job is to tell an app *which address:port* to open a
session/control channel (TCP, WebSocket, HTTP, …) to. That channel is the consuming app's concern,
outside this library.

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
upload the `.nupkg` artifact; a second job pushes to nuget.org on push to `main`. The package
version is composed there as `1.0.<run_number>+<run_attempt>+<sha>` — bump `VersionPrefix` in
`LAN.Lib.csproj` by hand for a minor feature addition or breaking change; CI's `run_number` drives
the patch component.

## Architecture

Everything lives in `src/LAN.Lib` (the library) and `src/LAN.Lib.Tests` (xunit.v3 + Shouldly).
Reading these files in order builds the full picture:

- **`LanProtocol`** — the wire format: plain space-separated ASCII (no JSON/reflection, keeps the
  package AOT/trim-clean), one line per datagram, prefixed with a magic word (`SALAN`) + version so
  a foreign/malformed datagram on the shared port is silently ignored rather than misparsed.
  `ANNOUNCE` carries peer id/service/port/name plus an open `key=value` property bag (URL-encoded
  values) for forward-extensibility without a wire bump; `BYE` carries just the peer id.

- **`ILanTransport`** — the one abstraction point: `BroadcastAsync(text)` + a `DatagramReceived`
  event. `UdpLanTransport` is the real backend (one UDP socket, `ReuseAddress` + `EnableBroadcast`,
  a background receive loop). Tests never touch a real socket: `FakeLanBus`/`FakeLanTransport` in
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
