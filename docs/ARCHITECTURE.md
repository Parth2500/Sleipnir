# Architecture

## Goals

- **Correctness first**: a file marked complete is byte-identical to the source, whatever
  happens in between (pauses, retries, crashes, restarts, misbehaving servers).
- **Use the bandwidth**: parallel ranged requests where the server allows it.
- **Survive real networks**: dropped and stalled connections, rate limiting, broken IPv6.
- **Testable core**: the download engine depends on interfaces, not on HTTP or the file system,
  so it can be tested with in-memory fakes.

## Layers

```mermaid
flowchart TD
    Program["Program.cs<br/>(input loop, signals)"] --> UI["UI/DashboardRenderer<br/>UI/SpeedTracker"]
    Program --> IDS["IDownloadService"]
    UI --> IDS
    IDS -.implemented by.-> DS["Services/DownloadService<br/>(queue, runs, retry policy, persistence)"]
    DS --> SD["Services/SegmentDownloader"]
    DS --> FNR["Services/FileNameResolver"]
    DS --> INC["INetworkClient"]
    DS --> IFS["IFileStorage"]
    DS --> ISS["IDownloadStateStore"]
    SD --> INC
    SD --> IFS
    INC -.implemented by.-> HNC["Infrastructure/Network/HttpNetworkClient<br/>+ HappyEyeballs"]
    IFS -.implemented by.-> LFS["Infrastructure/Storage/LocalFileStorage"]
    ISS -.implemented by.-> JSS["Infrastructure/Persistence/JsonDownloadStateStore"]
```

| Folder | Contents | Depends on |
|--------|----------|------------|
| `Core/Models` | `DownloadTask`, `DownloadSegment`, `DownloadStatus`, `RemoteFileInfo`, `DownloadRecord` | nothing |
| `Core/Interfaces` | `IDownloadService`, `INetworkClient`, `IFileStorage`, `IDownloadStateStore`, `RangeNotSupportedException`, `ServerBusyException` | Models |
| `Services` | Download orchestration, segment downloading, file naming, options | Core |
| `Infrastructure` | `HttpClient`, file system, and JSON implementations of the interfaces | Core |
| `UI` | Pure functions that turn download state into Spectre.Console renderables; speed measurement | Core |
| `Program.cs` | Composition root, the keyboard/render loop, signal handling | everything |

## Download lifecycle

```mermaid
sequenceDiagram
    participant UI as Program / UI
    participant DS as DownloadService
    participant NC as INetworkClient
    participant SD as SegmentDownloader ×N
    participant FS as IFileStorage
    participant ST as IDownloadStateStore

    UI->>DS: AddDownloadAsync(url, dir)
    DS->>DS: sanitize name, pick unique path, enqueue
    DS->>ST: save (Queued)
    DS-->>UI: id
    Note over DS: when a slot is free (max 3 running)
    DS->>NC: GetFileInfoAsync(url)  (with retries)
    NC-->>DS: size?, supportsRanges
    DS->>FS: CreateFileAsync(name.dnm, size)  (pre-allocated)
    par one per segment
        DS->>SD: DownloadSegmentAsync(segment)
        SD->>NC: GetStreamAsync(url, start, end)
        loop until segment complete
            SD->>SD: read into buffer (stall timer per read)
            SD->>FS: WriteAsync at offset (every 512 KB) → PersistedBytes += n
        end
    and every 2 s
        DS->>ST: save (persisted bytes only)
    end
    DS->>FS: Move(name.dnm → name)  (never overwrites)
    DS->>ST: save (Completed)
    UI->>DS: GetDownload(id)  (polled every 100 ms)
```

### 1. Probing the server: `HttpNetworkClient.GetFileInfoAsync`

1. Send `HEAD`. If it succeeds with `Content-Length` and `Accept-Ranges: bytes`, we're done.
2. Otherwise (HEAD rejected, no size, or ranges not advertised), send `GET` with `Range: bytes=0-0`:
   - `206` with `Content-Range: bytes 0-0/TOTAL` means size = TOTAL and ranges are supported.
   - `200` means size = `Content-Length` (possibly unknown) and ranges are **not** supported.

The probe goes through the same retry policy as segments (see §4).

### 2. Segmenting: `DownloadService.CreateSegments`

`count = clamp(size / MinSegmentSize, 1, SegmentCount)` (defaults: 1 MB, 8). Segments are equal
except the last, which absorbs the remainder. Without range support there is exactly one segment.

**Unknown size:** a single *open-ended* segment (`IsOpenEnded`) that reads until the server closes
the stream, then sets its real `EndOffset`. Retries and resumes restart it from byte 0, truncating
the temp file first.

The segment list is created once and **assigned as a whole** (`IReadOnlyList`), never mutated, so the
UI thread can enumerate it while downloads run.

### 3. Downloading a segment: `SegmentDownloader`

Each segment requests `Range: bytes=(start + persisted)-end`, reads in 80 KB chunks into a memory
buffer, and writes the buffer at the right offset of the pre-allocated `.dnm` file every 512 KB.

Each segment keeps two counters:

| Counter | Meaning | Used by |
|---------|---------|---------|
| `BytesDownloaded` | bytes received, including those still in the memory buffer | the UI (live progress) |
| `PersistedBytes` | bytes confirmed written to disk | resume and the state store |

**Key invariants:**
- `PersistedBytes` only advances after a write completes, so the saved state never claims bytes
  that aren't on disk. This holds even if the process is killed.
- Whenever `DownloadSegmentAsync` exits, for whatever reason, the buffer has been flushed (with
  `CancellationToken.None`, so the flush itself can't be interrupted) and the two counters are equal.
  If that flush fails, they roll back to the last successful write.

Other failure modes:
- `GetStreamAsync` rejects a ranged request that comes back as `200` by throwing
  `RangeNotSupportedException`. Writing a full-file body at a segment's offset would corrupt the file.
- A stream that ends before the segment is complete raises `IOException`.
- **Stall detection:** before every read, a linked token's timer is reset to `StallTimeout` (30 s). A
  connection that stays open but sends nothing trips it, and the segment raises `TimeoutException`.

### 4. Retry policy: `DownloadService.NextRetryDelay`

The probe and every segment share one policy, with two separate budgets:

| Error | Decision |
|-------|----------|
| `OperationCanceledException` from our own token | not an error: pause/cancel/shutdown |
| `RangeNotSupportedException` | no retry; `RunAsync` switches to single-stream mode and restarts |
| `ServerBusyException` (HTTP 429/503) | wait `Retry-After` if sent, else `RetryDelay × 2ⁿ` with jitter, capped at `MaxBusyDelay` (60 s); up to `MaxBusyRetries` (10) |
| `HttpRequestException` with 4xx (except 408/425/429) | permanent: fail at once |
| anything else (network errors, timeouts, early close) | up to `MaxRetries` (3), waiting `RetryDelay × n` (1 s, 2 s, 3 s) |

A retry resumes from `PersistedBytes`. When a segment gives up, it cancels a **linked token**
shared by all segments of the download, so the other connections stop immediately and the download
becomes `Failed`.

`HttpNetworkClient` turns HttpClient's own timeouts (reported as `TaskCanceledException`) into
`TimeoutException`. This keeps them from being mistaken for a user's pause, and gives them a readable
message.

### 5. Connecting: Happy Eyeballs

`SocketsHttpHandler.ConnectCallback` uses `HappyEyeballs.ConnectSocketAsync` (a simplified RFC 8305):
1. Resolve the host, then interleave address families (v6, v4, v6, v4…), keeping the resolver's
   preferred family first.
2. Start the first attempt. Each further attempt starts when the previous one **fails**, or after a
   **250 ms head start** if it is still pending.
3. The first successful connection wins. The others are cancelled, and disposed if they still succeed.

Without this, .NET tries addresses one at a time. On networks where IPv6 is advertised but
black-holed (common), every connection then hangs until the 30 s connect timeout, without ever trying
IPv4. Browsers and curl avoid this the same way.

### 6. Finishing

Once every segment is `Completed`, `name.dnm` is renamed to `name`. If `name` appeared on disk in the
meantime, a fresh unique name is chosen. `File.Move(..., overwrite: false)` guarantees nothing
existing is replaced. For unknown-size downloads, `TotalBytes` is set to the bytes received.

## Concurrency model: queue and runs

```
_queue:  [ id, id, id ]  (FIFO, waiting)   ──PumpQueue──▶   _runs: { id → DownloadRun }  (≤ MaxConcurrentDownloads)
```

A *run* is one execution of a download, from start/resume until it completes, pauses, fails, is
cancelled, or is stopped by shutdown:

```csharp
sealed class DownloadRun { CancellationTokenSource Cts; Task Completion; StopReason Reason; }
```

- **Add / Resume** put the id at the back of `_queue` and call `PumpQueue`. That starts runs while
  fewer than `MaxConcurrentDownloads` (3) are active.
- **Pause**: a queued download is simply removed from the queue. A running one has its token
  cancelled and `run.Completion` awaited.
- **Cancel**: same as pause with `Reason = Cancel`, then the `.dnm` file is deleted.
- **Resume** waits for any previous run to finish first, so two runs never write the same file.
- When a run ends, it saves its final state, removes **only its own** entry from `_runs`
  (`TryRemove(KeyValuePair)`), and calls `PumpQueue` so the next queued download starts.
- The final status is decided by the run itself, from `Reason`: `Pause → Paused`,
  `Cancel → Cancelled`, `Shutdown → Queued` (so it resumes next session).

Queue and run-table changes happen under a single lock (`_gate`). Segment counters use `Interlocked`,
because download threads write them and the UI thread reads them.

## Persistence: surviving restarts

`IDownloadStateStore` holds one `DownloadRecord` per download. `JsonDownloadStateStore` keeps them at
`$XDG_DATA_HOME/dnm/downloads/<id>.json` (usually `~/.local/share/dnm/downloads/`).

| When | What is saved |
|------|---------------|
| Add, pause (queued), resume, cancel | the new status |
| A run starts | status `Downloading`, size, range support |
| Every `StateSaveInterval` (2 s) while running | segment progress (`PersistedBytes` only) |
| A run ends | the final status, saved **before** it is published to `Status`, so anyone who sees the new status can rely on the saved state |
| `ShutdownAsync` | every download |

- **Atomic writes:** each record is written to `<id>.json.tmp`, then renamed over the old file, so a
  crash mid-write leaves the previous version intact. Unreadable files are skipped at start-up
  rather than crashing it.
- **Restore** (`RestoreAsync`, on start-up): records become tasks again. `Downloading` and `Queued`
  are re-queued and start automatically. `Paused`, `Failed` and finished downloads keep their status.
  Only the 200 most recent finished downloads are kept.
- **If the `.dnm` file is gone**, the run notices, recreates it and starts that download over.
- **Shutdown** (`ShutdownAsync`, from `Q`, Ctrl+C, SIGTERM, SIGHUP): stop accepting new runs, stop
  every run with `Reason = Shutdown` (each one flushes its buffers), then save everything.

**Crash safety:** because only `PersistedBytes` is saved, the state on disk never runs ahead of the
data in the `.dnm` file. After a hard crash you lose at most the last couple of seconds of progress,
never correctness. (One caveat: after an OS crash or power loss, data the OS hadn't yet flushed from
its page cache may be missing. DNM doesn't `fsync` on every write.)

## Status state machine

```mermaid
stateDiagram-v2
    [*] --> Queued: Add / Resume / Restore
    Queued --> Downloading: a slot frees up
    Queued --> Paused: Pause
    Queued --> Cancelled: Cancel
    Downloading --> Completed: all segments done
    Downloading --> Paused: Pause
    Downloading --> Failed: gave up after retries
    Downloading --> Cancelled: Cancel
    Downloading --> Queued: Shutdown (resumes next start)
    Paused --> Queued: Resume
    Failed --> Queued: Resume
    Paused --> Cancelled: Cancel
    Failed --> Cancelled: Cancel
```

## The UI

`Program.cs` restores the previous session, then runs a Spectre.Console `Live` display. Every 100 ms
it rebuilds the layout from `IDownloadService` state, feeds `SpeedTracker`, and polls
`Console.KeyAvailable`. To prompt for a URL, it leaves the live display, calls `AnsiConsole.Ask`, then
re-enters it.

**Exit paths:** `Q`, `SIGINT` (Ctrl+C), `SIGTERM` (`kill`, `podman stop`) and `SIGHUP` (terminal
closed) are registered with `PosixSignalRegistration`. They set a flag so the loop exits and calls
`ShutdownAsync`. A second Ctrl+C exits immediately.

`DashboardRenderer` is a set of pure functions (state in, renderable out), which keeps them unit
testable. Any text that comes from a URL or server (file names, error messages) **must** go through
`Markup.Escape`.

`SpeedTracker` keeps `(time, bytes)` samples for each running download over a 5 s sliding window.
Speed = bytes gained ÷ time elapsed across the window. ETA = remaining ÷ speed, and is unknown
without a size or while nothing arrives. If progress goes backwards (a single-stream restart), the
window is reset.

## Configuration

`DownloadOptions` (passed to the `DownloadService` constructor):

| Option | Default | Purpose |
|--------|---------|---------|
| `SegmentCount` | 8 | Max parallel connections per download |
| `MinSegmentSize` | 1 MB | Smaller files get fewer segments |
| `MaxConcurrentDownloads` | 3 | Downloads transferring at once; the rest are queued |
| `MaxRetries` | 3 | Retries for ordinary errors, per segment and for the probe |
| `RetryDelay` | 1 s | Base backoff; attempt *n* waits *n* × this |
| `MaxBusyRetries` | 10 | Retries for HTTP 429/503 (separate budget) |
| `MaxBusyDelay` | 60 s | Cap on a single "server busy" wait |
| `StallTimeout` | 30 s | No data for this long counts as a failed connection |
| `FlushThreshold` | 512 KB | Per-segment memory buffer before writing to disk |
| `StateSaveInterval` | 2 s | How often running downloads save their progress |

`HttpNetworkClient(headersTimeout)` defaults to 60 s, and bounds only the wait for response headers.

## Known limitations and next steps

See the [roadmap](https://github.com/Parth2500/DotNet-Download-Manager/issues/1) and
[FEATURE_COMPARISON.md](FEATURE_COMPARISON.md).

| Limitation | Direction |
|------------|-----------|
| Options are hard-coded in `Program.cs` | Settings file (`~/.config/dnm/settings.json`), Phase 2 |
| No bandwidth limit | Token-bucket limiter around segment reads, Phase 2 |
| A download whose size is unknown can't resume mid-file | Inherent without ranges; nothing to do |
| One output folder (`~/Downloads`) | Part of the settings file / CLI mode |
