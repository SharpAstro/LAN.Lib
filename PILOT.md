# PILOT.md

What a plane can and cannot do in this repo. The node quotes this page whole into the planner, worker and reviewer
briefs, so it stays short. `CLAUDE.md` holds the architecture and conventions; this page holds only what a flight
needs to plan, build, check and land.

## What a plane can and cannot do

- A plane is a git worktree at `<repo>/.claude/worktrees/<code>` on `flight/<code>`. Work and commit only there. Do not
  push, open a PR or merge; the controller lands the flight. Leave nothing uncommitted, and put scratch output
  outside the plane.
- LAN.Lib has no sibling dependencies, so a plane builds everything it needs. Its consumers (`tianwen`, `pilot`,
  chess) cannot be built from a plane: a change's effect on a consumer is named in the leg's report, not checked, and a
  change a consumer must make is its own flight in that repo.
- No real network is used by a test. `FakeLanBus` / `FakeLanTransport` stand in for the broadcast domain, and anything
  read from the operating system (a socket, a routing or neighbour table, a resolver) goes through an interface the
  test answers. A test that needs the machine's own network is skipped unless an environment variable turns it on, and
  says which; it never runs in CI or in a plane.
- Never wait with a job that sleeps or polls: use `pilot leg wait --for-file <path>` and end the turn. Long builds
  and test runs go through `pilot run -- <command>`. The machine is shared, so a job may wait its turn first.

## Build and check

Always `-c Release`, as CI does. Run from the repo root.

```bash
dotnet build LAN.Lib.slnx -c Release
dotnet test src/LAN.Lib.Tests -c Release --no-build
```

- Tests are xunit.v3 with Shouldly on Microsoft.Testing.Platform, not VSTest: `--logger` and `--blame-*` fail the run.
  `--filter` keeps its VSTest syntax (`--filter "FullyQualifiedName~<Name>"`).
- Time goes through the injected `TimeProvider`; a test drives it with `FakeTimeProvider.Advance`, never a real delay.
- An async test passes `TestContext.Current.CancellationToken`.

## Compatibility and release

- **Merging to `main` publishes** the package (CI pushes to nuget.org on every push to main, as `X.Y.<run><attempt>`).
  There is no separate release step, so what lands on main ships.
- A flight that adds API bumps `VersionMajorMinor` in the root `Directory.Build.props` (additive is a minor, a break is
  a major) and, in the SAME commit, adds its release note to the `env:` comment block of `.github/workflows/dotnet.yml`
  (newest last): what changed, what is not included, and any behaviour or wire change said plainly.
- Consumers compile against the published package, so a binary break is a real break even when every caller still
  compiles. The wire format (`LanProtocol`) is a contract between nodes of different versions: a change to it is a
  major, unless it only adds to the open property bag.
- AOT and trimming must stay clean: no reflection, and native calls through `LibraryImport` (source-generated
  marshalling), never `DllImport`.

## How a flight merges

- main has no merge queue, but its ruleset requires **linear history**, so a PR lands by squash or rebase, never as a
  merge commit. Take main by a merge (`git fetch origin`, `git merge origin/main`), never a rebase, and never push with
  `--force`. A merge that resolved a conflict cannot be rebased onto main; say so in the leg's report, so the PR
  lands squashed.
- Keep a flight's commits few and each one whole.

## Writing

No em-dashes, and no `--` in their place: a full stop, a comma, a semicolon or parentheses. This holds for prose,
comments and commit messages.
