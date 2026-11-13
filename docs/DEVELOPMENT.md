# Development Guide

## Prerequisites

- .NET 10 SDK (`dotnet --list-sdks` should show 10.x)
- Optional: Docker or Podman

## Build, run, test

```bash
dotnet build Sleipnir.sln
dotnet run --project src/Sleipnir.App/Sleipnir.App.csproj
dotnet test Sleipnir.sln
```

The suite runs in well under a second, apart from the loopback HTTP end-to-end test.

### No .NET 10 SDK installed?

Build and test in a container instead:

```bash
podman run --rm --security-opt label=disable -v "$PWD:/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test Sleipnir.sln
```

To run the TUI the same way (`-it` is required):

```bash
podman run -it --rm --security-opt label=disable \
  -v "$PWD:/src" -v ~/Downloads:/root/Downloads -v dnm-state:/root/.local/share/dnm -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project src/Sleipnir.App/Sleipnir.App.csproj
```

(`docker` works the same way.)

## Production build (Linux)

```bash
dotnet publish src/Sleipnir.App/Sleipnir.App.csproj -p:PublishProfile=linux-x64
# or, without a local .NET 10 SDK:
podman run --rm --security-opt label=disable -v "$PWD:/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet publish src/Sleipnir.App/Sleipnir.App.csproj -p:PublishProfile=linux-x64
```

This produces **`artifacts/linux-x64/dnm`**: one self-contained executable (about 43 MB) that runs
on 64-bit Linux with glibc and **no .NET installed**. The settings live in
`Properties/PublishProfiles/linux-x64.pubxml`:

| Setting | Why |
|---------|-----|
| `SelfContained` + `PublishSingleFile` | One file, no runtime to install |
| `EnableCompressionInSingleFile` | Smaller download |
| `PublishReadyToRun` | Precompiled code for faster start-up |
| `InvariantGlobalization` | No dependency on `libicu` |
| `AssemblyName=dnm` (in the app `.csproj`) | A short command name |

Not trimmed: `System.Text.Json` (the state store) and Spectre.Console use reflection, and trimming
would risk breaking them.

For a release, package it with a checksum:

```bash
cd artifacts && mkdir dnm-linux-x64 && cp linux-x64/dnm ../LICENSE ../README.md ../CHANGELOG.md dnm-linux-x64/
tar -czf dnm-linux-x64.tar.gz dnm-linux-x64 && sha256sum dnm-linux-x64.tar.gz > dnm-linux-x64.tar.gz.sha256
```

`artifacts/` is gitignored and excluded from the Docker build context.

## Project layout

```
.
├── Directory.Build.props      shared: net10.0, nullable, implicit usings
├── Sleipnir.sln
├── src/
│   ├── Sleipnir.Core/         models and interfaces; no dependencies beyond the framework
│   │   ├── Interfaces/        IDownloadService, INetworkClient, IFileStorage, IDownloadStateStore,
│   │   │                      RangeNotSupportedException, ServerBusyException
│   │   └── Models/            DownloadTask, DownloadSegment, DownloadStatus, RemoteFileInfo, DownloadRecord
│   ├── Sleipnir.Server/       the local REST API (ASP.NET Core): DaemonHost, auth, instance lock, app paths
│   ├── Sleipnir.Engine/       the download engine; no console and no web framework
│   │   ├── Services/          DownloadService (queue, runs, retry, persistence), SegmentDownloader,
│   │   │                      FileNameResolver, DownloadOptions
│   │   └── Infrastructure/    HttpNetworkClient + HappyEyeballs, LocalFileStorage, JsonDownloadStateStore
│   └── Sleipnir.App/          the `dnm` executable
│       ├── Program.cs         command dispatch, composition root, key/render loop, signal handling
│       ├── DaemonCommand.cs   `dnm daemon`
│       ├── Service/           `dnm service …` (systemd user unit)
│       └── UI/                TuiController, TuiRenderer, LineEditor, SlashCommands, SpeedTracker, Format
├── tests/Sleipnir.Tests/
└── docs/
```

Layering is enforced by `ArchitectureTests`: Core references only the framework, and Engine never
references Spectre.Console or ASP.NET, so it can be reused by a future Android app.

For how the pieces interact, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Tests

| File | Covers |
|------|--------|
| `SegmentDownloaderTests` | No data loss on pause/resume; early connection close is an error |
| `DownloadServiceTests` | Happy path, no-range servers, range-ignoring servers, pause→resume→pause, cancel, retry, permanent failure, 429 backoff, 4xx fail-fast, probe retries, path traversal, no overwrite, unique names, missing folder |
| `PersistenceTests` | Store round-trip, corrupt files skipped, paused download survives restart, shutdown → auto-resume (only missing ranges re-requested), saved progress never includes unflushed bytes, history survives restart |
| `QueueStallAndUnknownSizeTests` | Concurrency limit and FIFO start, pausing queued downloads, stalled connections retried, unknown-size downloads (incl. mid-stream failure) |
| `HttpNetworkClientTests` | 200-vs-206 validation, HEAD/probe fallback, unknown size, 429/503 + `Retry-After`, timeouts reported as timeouts, User-Agent (stub `HttpMessageHandler`) |
| `HappyEyeballsTests` | Address interleaving, a hanging address doesn't block, fast failure moves on, losers cancelled, caller cancellation |
| `SpeedTrackerTests` | Sliding-window speed, ETA, unknown size, restarts, non-running downloads |
| `Tui/LineEditorAndCommandTests` | Editing keys, cursor movement, word/line deletion; command suggestions and prefix parsing |
| `Tui/TuiControllerTests` | Paste-to-add (incl. several URLs), invalid input, selection and default selection, Space pause/resume, details, Del confirmation, Ctrl+C clear/quit, suggestions + Tab, commands, help, toasts |
| `Tui/TuiRendererTests` | Empty state, rows, speed/ETA, header, escaping, details, unknown size, waiting segments, suggestions, help, toasts, narrow terminals, scrolling, fits the terminal height |
| `ArchitectureTests` | Core has only framework references; Engine has no console/web references; state folder unchanged |
| `Server/AppPathsTests`, `SecretTests`, `InstanceTests` | Per-OS folders, `0600` secret files and the admin token, single-instance lock, `daemon.json` |
| `Server/DaemonHostTests` | Token and Host checks, no CORS, throttling, port fallback (real Kestrel on loopback) |
| `Server/DownloadEndpointTests` | Paging, validation, body limit, idempotency keys, control verbs, auth on every route |
| `Server/DaemonProcessTests` | Real `dnm daemon` processes: start, second instance, TUI/daemon lock, shutdown, SIGTERM, crash recovery |
| `ServiceCommandTests` | systemd unit text and install/uninstall/status with a fake `systemctl` |
| `EndToEndTests` | Real HTTP over loopback (`HttpListener`), with and without range support, slow body vs header timeout |

### Test helpers

- **`FakeNetworkClient`** serves a byte array. Its knobs: `SupportsRanges` (what the probe reports),
  `HonorRanges` (whether ranged requests get partial bodies), `ReportSize` (unknown-size servers),
  an `OnRequest` hook to throw or substitute a stream per request, and `OnProbe` to fail probes. `Stalling(from, n)` builds a stream that sends `n` bytes and then
  blocks until cancelled, which is how pause and cancel are simulated. It also tracks `OpenStreams`
  and `RequestCount`.
- **`Tui/StubDownloadService`** is an in-memory `IDownloadService` that records calls, used to test
  the UI without any network. **`Tui/Keys`** builds `ConsoleKeyInfo` values the way a terminal sends
  them.
- **`TempDir`** is a throwaway directory that is deleted on dispose.
- **`TestHelpers.WaitUntil`** polls a condition, because downloads run in the background.

Tests use the real `LocalFileStorage` in a temp directory. Byte-for-byte file comparison is the
strongest check that a download is correct.

### Writing a bug fix

1. Reproduce the bug as a failing test (fake the network behaviour that triggers it).
2. Run it and confirm it fails *for the right reason*.
3. Fix the bug, then run the full suite.

## Conventions

- **Layering**: `Core` references nothing else. `Services` use only `Core` interfaces. Only
  `Program.cs` creates the `Infrastructure` types.
- **Async**: every I/O path takes a `CancellationToken`. Fire-and-forget work goes through
  `DownloadService.StartRun`, so it is tracked by a `DownloadRun`.
- **Thread safety**: the UI reads state that download threads write. Replace collections instead
  of mutating them, and use `Interlocked` for counters.
- **UI text**: wrap any external string in `Markup.Escape(...)` before putting it in markup.
- **Line endings**: LF, enforced by `.gitattributes`. If `git status` shows every file as modified
  with no visible diff, your editor converted the files to CRLF.

## Docker

The `Dockerfile` is the Visual Studio multi-stage template: build on `sdk:10.0`, run on `runtime:10.0`
as the non-root `app` user. Because it is an interactive TUI, always run it with `-it`, and
override the user so it can write to a mounted Downloads folder:

```bash
podman build -t dnm .

# Podman (rootless): container root = you, so files in ~/Downloads are owned by you
podman run -it --rm --security-opt label=disable --user root -e HOME=/root \
  -v ~/Downloads:/root/Downloads -v dnm-state:/root/.local/share/dnm dnm

# Docker: run as your own UID so the container can write to your ~/Downloads
docker run -it --rm --user "$(id -u):$(id -g)" -e HOME=/home/dnm \
  -v ~/Downloads:/home/dnm/Downloads -v dnm-state:/home/dnm/.local/share/dnm dnm
```

See the [User Guide](USER_GUIDE.md#podman--docker) for why each flag is needed.
