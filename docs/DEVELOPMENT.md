# Development Guide

## Prerequisites

- .NET 10 SDK (`dotnet --list-sdks` should show 10.x)
- Optional: Docker or Podman

## Build, run, test

```bash
dotnet build "DotNet Download Manager.sln"
dotnet run --project "DotNet Download Manager.csproj"
dotnet test "DotNet Download Manager.sln"
```

The suite runs in well under a second, apart from the loopback HTTP end-to-end test.

### No .NET 10 SDK installed?

Build and test in a container instead:

```bash
podman run --rm --security-opt label=disable -v "$PWD:/src" -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test "DotNet Download Manager.sln"
```

To run the TUI the same way (`-it` is required):

```bash
podman run -it --rm --security-opt label=disable \
  -v "$PWD:/src" -v ~/Downloads:/root/Downloads -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project "DotNet Download Manager.csproj"
```

(`docker` works the same way.)

## Project layout

```
.
├── Core/
│   ├── Interfaces/       IDownloadService, INetworkClient, IFileStorage, RangeNotSupportedException
│   └── Models/           DownloadTask, DownloadSegment, DownloadStatus, RemoteFileInfo
├── Infrastructure/
│   ├── Network/          HttpNetworkClient  (HEAD/probe, ranged GET, 206 validation)
│   └── Storage/          LocalFileStorage   (pre-allocate, offset writes, safe move)
├── Services/
│   ├── DownloadService.cs     orchestration, runs, retries, pause/resume/cancel
│   ├── SegmentDownloader.cs   one byte range → disk, with the "counted = persisted" invariant
│   ├── FileNameResolver.cs    URL → safe, unique file name
│   └── DownloadOptions.cs     tunables
├── UI/DashboardRenderer.cs    Spectre.Console renderables (pure functions)
├── Program.cs                 composition root + key/render loop
├── tests/DotNetDownloadManager.Tests/
└── docs/
```

`tests/` sits inside the app's project folder, so the app's `.csproj` excludes it with
`<Compile Remove="tests/**" />`. Keep that line if you move things around.

For how the pieces interact, see [ARCHITECTURE.md](ARCHITECTURE.md).

## Tests

| File | Covers |
|------|--------|
| `SegmentDownloaderTests` | No data loss on pause/resume; early connection close is an error |
| `DownloadServiceTests` | Happy path, no-range servers, range-ignoring servers, pause→resume→pause, cancel, retry, permanent failure, path traversal, no overwrite, unique names, missing folder |
| `HttpNetworkClientTests` | 200-vs-206 validation, HEAD/probe fallback, User-Agent (stub `HttpMessageHandler`) |
| `DashboardRendererTests` | Markup characters in file names and errors render literally |
| `EndToEndTests` | Real HTTP over loopback (`HttpListener`), with and without range support |

### Test helpers

- **`FakeNetworkClient`** serves a byte array. Its knobs: `SupportsRanges` (what the probe reports),
  `HonorRanges` (whether ranged requests get partial bodies), and an `OnRequest` hook to throw or
  substitute a stream per request. `Stalling(from, n)` builds a stream that sends `n` bytes and then
  blocks until cancelled, which is how pause and cancel are simulated. It also tracks `OpenStreams`
  and `RequestCount`.
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
  -v ~/Downloads:/root/Downloads dnm

# Docker: run as your own UID so the container can write to your ~/Downloads
docker run -it --rm --user "$(id -u):$(id -g)" -e HOME=/home/dnm \
  -v ~/Downloads:/home/dnm/Downloads dnm
```

See the [User Guide](USER_GUIDE.md#podman--docker) for why each flag is needed.
