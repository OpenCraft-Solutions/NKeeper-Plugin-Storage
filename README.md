# NKeeper Storage plugin

The official `storage` plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0: a Linux host's shared storage (iSCSI, multipath and DRBD) over its SSH service. It lives in a repository of its own and is released on its own, so releasing it isn't a release of NKeeper (NKeeper's `docs/architecture.md` §9). Its design is §7.27 there.

## What it does

It contributes no service type. Its one tool opens on the ssh plugin's `ssh` services, with the service's credential:
- Core opens the connection, checks the server's key against its trust store, asks the user about a new one and signs in. The plugin never sees a password or a key.
- The tool is a stream session, so core counts it toward the user's sessions and ends it when their access changes.
- It needs SDK 1.3 (`>=1.3.0 <2.0.0`).

**Storage** (`storage.open`), ported from 1.x:
- It shows the iSCSI initiator, its sessions and known targets, the multipath maps with their paths and the iSCSI targets behind them, and the DRBD resources with their volumes and peers.
- Its actions:
  - iSCSI: discovery, login, logout and rescan;
  - multipath: reload, flush and rename (through the map's alias in the host's multipath configuration, put back if the map doesn't take it);
  - DRBD: up, down, primary, secondary, connect, disconnect and adjust.
- Every value in a command is one the host itself reported, or a strictly checked portal address.
- An action counts as done only when the host's state afterwards shows it.
- Actions that take storage away ask first.
- It needs root, or `sudo` without a password.
- It checks `uname -s` first and says so on a host that isn't Linux.
- Actions are audited as `storage.action`.

The plugin's ID is `storage`, and it is built in: it is signed with NKeeper's official key and installed with NKeeper.

## Layout

```text
backend/        the plugin: StoragePlugin.cs, the tool (StorageSession.cs, StorageHost.cs), nkeeper-plugin.json (the manifest), the project
frontend/       the Storage view: a Vite library build to dist/index.js
tests/          the backend tests, which start NKeeper's test server with this plugin loaded; tests/Storage, the fake tools
.github/        CI and the release workflow
```

It builds against NKeeper's published SDK, never core's source:

| Package | Registry | Used for |
| --- | --- | --- |
| `NKeeper.Abstractions` | GitHub Packages (NuGet) | the backend SDK |
| `NKeeper.Plugin.Build` | GitHub Packages (NuGet) | builds, packs and signs the plugin |
| `NKeeper.Testing` | GitHub Packages (NuGet) | the test server and PostgreSQL fixture |
| `@opencraft-solutions/nkeeper-sdk` | GitHub Packages (npm) | the frontend SDK, imported as `@nkeeper/sdk` |

## Building

You need the .NET 10 SDK (see `global.json`), Node.js 24 (see `.nvmrc`) and Docker for the tests.

GitHub Packages needs a token even for public packages. Make a GitHub personal access token that can read packages (`read:packages`) and give it to the build in the environment. It is never written to the repository:

```bash
export NUGET_USERNAME=<your GitHub user name>
export NUGET_AUTH_TOKEN=<token>
export NODE_AUTH_TOKEN=<token>
```

Then, from the repository root:

```bash
(cd frontend && npm ci && npm run lint && npm run typecheck)
dotnet restore NKeeper.Plugins.Storage.slnx
dotnet build NKeeper.Plugins.Storage.slnx --configuration Release
```

Building the plugin builds its frontend, packs `storage-<version>.nkp` into `artifacts/plugins/` and signs it with a development key, which the build creates under `.dev/keys/` on first use. Both are ignored by git. To load the package in a development NKeeper, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

Pinned versions: NuGet packages are locked in `packages.lock.json` (CI restores with `--locked-mode`), and npm packages in `frontend/package-lock.json`. Run `dotnet restore --force-evaluate` after changing a version.

## Testing

```bash
dotnet test NKeeper.Plugins.Storage.slnx --configuration Release
```

The tests need Docker and the internet:
- They start PostgreSQL and an OpenSSH server in containers.
- They start NKeeper's test server (from `NKeeper.Testing`) with this plugin's built package and the released ssh plugin loaded. The ssh plugin is downloaded from its GitHub release and checked against the SHA-256 that NKeeper's `deploy/plugins.lock` pins.
- The OpenSSH server is dressed as a storage host with fake `iscsiadm`, `multipath`, `multipathd`, `drbdadm` and `drbdsetup` (`tests/Storage/`, ported from 1.x). They answer in the real tools' formats and need root, as the real ones do; the test user gets it with `sudo`. A fake `uname` can say the host is Windows.

They cover the tool end to end:
- the status;
- iSCSI discovery, login and logout, a SAN that redirects logins, a login that doesn't take effect, and unsafe portals;
- multipath flush and rename, with its refusals and roll-backs;
- the DRBD actions;
- no `sudo`, unknown actions, a host that isn't Linux, and a user without access.

The parsers have their own tests, ported from 1.x.

## Continuous integration

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: it restores from GitHub Packages with the workflow's token, lints and type-checks the frontend, checks formatting, builds, runs the tests and packs the plugin signed with a development key, which it keeps as a build artifact.

## Releasing

1. Raise `version` in `backend/nkeeper-plugin.json` (and keep `frontend/package.json` in step), and merge it to `main`.
2. Tag the merge commit `v<version>`, for example `v0.2.0`, and push the tag.
3. `.github/workflows/release.yml` runs. It checks that the tag matches the manifest's version and is on `main`, builds and tests, and then, in the protected `release` environment, which needs an owner's approval, packs `storage-<version>.nkp` signed with the official key and publishes a GitHub release with the package and its SHA-256.

The official key's private half, `NKEEPER_RELEASE_KEY` (a PEM), is a secret of the `release` environment. It is available only to the signing job, goes to memory-backed storage for the packing step alone, and is removed when that step ends. It is never in the repository or in a log.

NKeeper's installer picks the release up when its `deploy/plugins.lock` is updated with the new URL and SHA-256.

## License

MIT, as NKeeper. See [LICENSE](LICENSE).
