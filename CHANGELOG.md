# Changelog

## Unreleased

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
