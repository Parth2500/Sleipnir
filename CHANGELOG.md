# Changelog

## Unreleased

### Changed: new name

- **Renamed to Sleipnir**, Odin's eight-legged horse: eight legs, eight connections. The GitHub
  repository is now `Parth2500/Sleipnir`; old links redirect. The UI and docs use the new name. The
  `dnm` command, the `.dnm` temp files and the data folders keep their names for now, and code
  namespaces move to `Sleipnir.*` during the Phase 2 restructure.

### Added: new interface

- **Full-screen, keyboard-first TUI** in the style of chat-like terminal apps. A list of downloads
  (status, name, progress bar, percentage, speed and ETA in one row) above an **input box that is
  always there**: paste a URL and press Enter. Pasting several URLs adds them all.
- **`/` commands with autocomplete**: `/add`, `/pause`, `/resume`, `/cancel`, `/clear`, `/help`,
  `/quit`. ↑↓ choose, Tab completes, unambiguous prefixes work (`/pa`).
- **Keys that don't fight with typing**: ↑↓ Space Enter Del ? act on the selection only while the
  input is empty. Del and Ctrl+C ask for a second press.
- **Expandable details** (Enter): URL, destination, progress, and every segment. Segments between
  retries show "waiting".
- Help screen, fading confirmation/error messages, context-sensitive hint line, scrolling list,
  narrow-terminal support, alternate screen (the shell is restored on exit).
- **Production Linux build**: `-p:PublishProfile=linux-x64` creates a single self-contained `dnm`
  executable that needs no .NET install.
- `IDownloadService.RemoveDownloadAsync`, used by `/clear` to forget finished downloads (files kept).

### Fixed

- Percentages could show one less than the truth (`580/1000` displayed as 57%) because of
  floating-point truncation.

### Removed

- The old two-panel dashboard (`DashboardRenderer`) and its N/P/R/C/Q single-letter keys.

## Phase 1, "daily driver" (committed 257337a)

### Added

- **Resume after restart.** Download state is saved to `~/.local/share/dnm/downloads/` (one atomic
  JSON file per download). On start-up, downloads that were running or queued resume automatically;
  paused, failed and finished ones (history) come back as they were.
- **Crash-safe progress.** Segments track `PersistedBytes` (confirmed on disk) separately from live
  progress, and only that is saved, so a crash can never make a resume skip data.
- **Graceful shutdown** on `Q`, Ctrl+C, SIGTERM (`kill`, `podman stop`) and SIGHUP. Buffers are
  flushed and progress saved. A second Ctrl+C exits immediately.
- **Download queue.** At most 3 downloads (`MaxConcurrentDownloads`) transfer at once; the rest wait as
  Queued and start in order.
- **Speed and ETA** for each download, and total speed in the footer (5 s sliding window).
- **Stall detection.** A connection that sends nothing for 30 s (`StallTimeout`) is retried.
- **Unknown-size downloads.** Servers that send no `Content-Length` are downloaded as one open-ended
  stream, with "size unknown" shown in the UI.
- **Rate-limit handling.** HTTP 429/503 honour `Retry-After` (or back off exponentially up to 60 s),
  with a separate budget of 10 retries.
- **Happy Eyeballs** (RFC 8305) connection racing between IPv6 and IPv4.

### Fixed

- **Downloads failed on networks with broken IPv6.** .NET tried the unreachable IPv6 address until
  the 30 s connect timeout and never tried IPv4 (seen with `proof.ovh.net`).
- **Permanent HTTP errors (404, 403, 410…) were retried three times for nothing.** They now fail at
  once.
- **The initial server probe had no retries,** so a single hiccup or 429 failed the whole download.
- **Timeouts were reported as `The operation was canceled.`** They are now `TimeoutException`s with a
  readable message, and are retried.
- **Queued downloads had no label** in the history panel.

### Changed

- `IDownloadService` gained `RestoreAsync()` and `ShutdownAsync()`. `DownloadService` takes an
  optional `IDownloadStateStore`.
- `HttpNetworkClient()` takes an optional `headersTimeout`, which bounds only the wait for response
  headers.
- Container instructions add a `dnm-state` volume, so state survives between runs.

## 0.1.0 (2026-11-12)

### Fixed

- **Pausing corrupted files.** Up to 512 KB per segment that had been received but not yet written
  was thrown away on pause, but still counted, so resuming left zero-filled holes. Buffered bytes are
  now flushed before a segment stops, and the counter always matches what is on disk.
- **Servers without Range support produced corrupt files.** Every segment received the whole file
  and wrote it at its own offset. Range support is now detected (HEAD + `bytes=0-0` probe), and a `200`
  reply to a ranged request is rejected. Such servers are downloaded over a single stream.
- **Pause could stop working after a quick pause → resume.** The old run's cleanup removed the new
  run's cancellation token, and two runs could write the same file at once. Runs are now tracked
  individually, and resume waits for the previous run to exit.
- **Cancel did not cancel.** Transfers kept running until the next chunk arrived (forever on a
  stalled connection) and the `.dnm` file was left behind. Cancel now aborts the transfers and
  deletes the partial file.
- **One failed segment left the rest running**, and nothing was retried. Segments now retry 3 times
  with backoff. A permanent failure stops the other segments and marks the download `Failed`
  (`R` retries it).
- **A connection closing early left a download stuck at "Downloading" forever.** It is now an
  error and is retried.
- **Path traversal through `?filename=`.** `?filename=../../.bashrc` wrote outside `~/Downloads`.
  Names are now reduced to a sanitized file name.
- **Existing files were overwritten**, and two downloads of the same URL shared one temp file.
  Names are now made unique (`file (1).zip`), and the final move never overwrites.
- **UI crashed on file names or errors containing `[` `]`** (e.g. `movie [1080p].mkv`, `[404]`).
  External text is now escaped.
- **UI crashed right after adding a download** (`Sequence contains no elements`). The progress bar
  had no items while the file size was still unknown. A 200 ms sleep after adding a download used to
  hide this, and that sleep has been removed.
- **Download failed when `~/Downloads` did not exist.** The folder is now created.
- **HTTP responses were never disposed**, and requests had no `User-Agent` (some servers reject
  those).
- The download list could be enumerated by the UI while being modified (possible
  `InvalidOperationException`).

### Added

- ↑/↓ to select any download in the history, so P/R/C work on more than the latest one.
- Footer shows the selected download's real status and the number of active downloads.
- `DownloadOptions` (segment count, minimum segment size, retries, backoff, flush size).
- Test project `tests/DotNetDownloadManager.Tests` (27 tests, including real loopback HTTP).
- Documentation in `docs/`, plus `.gitattributes` to keep LF line endings.
- MIT `LICENSE`.

### Changed

- Target framework upgraded from .NET 9 to **.NET 10** (LTS). The Docker images are now `sdk:10.0` / `runtime:10.0`.
- `INetworkClient.GetFileSizeAsync` was replaced by `GetFileInfoAsync`, which returns size and range
  support. All network methods accept a `CancellationToken`.
- `IFileStorage.WriteAsync` accepts a `CancellationToken`. A `Move` method was added.
- `DownloadTask.BytesDownloaded` is now computed from its segments. `Segments` is an
  `IReadOnlyList` that is replaced, never mutated.
- Rendering moved from `Program.cs` to `UI/DashboardRenderer.cs`.
- `RootNamespace` changed to `DotNetDownloadManager`, to match the code.
