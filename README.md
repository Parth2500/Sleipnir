# DotNet Download Manager (DNM)

A multi-threaded **download manager for the terminal**, written in C# / .NET 10 with a live
dashboard built on [Spectre.Console](https://spectreconsole.net/).

DNM splits a file into byte ranges, downloads them in parallel over HTTP, and stitches them into
one file. It can pause, resume (even after a restart), cancel, queue, and retry segments that fail.

```
╭─Current Download──────────────────────╮ ╭─Download History─────────────────────╮
│ File: ubuntu-24.04.iso                │ │ Filename         │ Status   │ Size   │
│ Status: ⬇ Downloading...              │ │ ubuntu-24.04.iso │ ⬇ 42%    │ 5.7 GB │
│ Progress: 42.10% (2.4 GB / 5.7 GB)    │ │ sdk.zip          │ ✓ Done   │ 210 MB │
│ Segments:                             │ │ tools.exe        │ ⏸ Paused │ 12 MB  │
│ ⬇ Seg 1: 44%   ⬇ Seg 2: 41%           │ ╰──────────────────────────────────────╯
╰───────────────────────────────────────╯
 N=New  P=Pause  R=Resume  C=Cancel  ↑/↓=Select  Q=Quit
```

## Quick start

```bash
# Requires the .NET 10 SDK (Fedora: sudo dnf install dotnet-sdk-10.0)
dotnet run --project "DotNet Download Manager.csproj"
```

Press **N**, paste a URL, and press Enter. Files are saved to `~/Downloads`.

| Key   | Action                                                   |
|-------|----------------------------------------------------------|
| `N`   | New download                                             |
| `P`   | Pause the selected download                              |
| `R`   | Resume the selected download (also retries failed ones)  |
| `C`   | Cancel the selected download and delete its partial file |
| `↑/↓` | Select another download from the history                 |
| `Q`   | Quit (running downloads resume on next start)            |

Running in a container (Podman shown; Docker instructions are in the [User Guide](docs/USER_GUIDE.md#podman--docker)):

```bash
podman build -t dnm .
podman run -it --rm --security-opt label=disable --user root -e HOME=/root \
  -v ~/Downloads:/root/Downloads -v dnm-state:/root/.local/share/dnm dnm
```

## Features

**Working today**

- Segmented parallel downloads using HTTP Range requests (up to 8 segments, ≥ 1 MB each)
- Automatic fallback to a single stream when the server does not support ranges — including servers
  that advertise ranges but ignore them
- **Resume after restart**: quit or crash mid-download and DNM picks up where it left off next time
  (progress is crash-safe: only bytes already on disk are recorded)
- Pause / resume / cancel without losing or corrupting data
- Download queue: at most 3 downloads run at once, the rest wait their turn
- Live speed and time remaining (ETA), per download and in total
- Robust retries: dropped connections, stalled connections (no data for 30 s), and rate limiting
  (HTTP 429/503, honouring `Retry-After`); permanent errors such as 404 fail immediately
- Works on networks with broken IPv6 (Happy Eyeballs: IPv6 and IPv4 are raced)
- Downloads of unknown size (servers that don't send `Content-Length`)
- Safe file naming: names from the URL are sanitized (no path traversal) and existing files are
  never overwritten (`file (1).zip`, `file (2).zip`, …)
- Live dashboard with per-segment progress and a persistent history

**Planned** — see the [roadmap](https://github.com/Parth2500/DotNet-Download-Manager/issues/1)

- Settings file, speed limits, checksum verification, batch input, authentication, proxies
- Non-interactive CLI mode, local API, browser extension

## Documentation

| Document | For |
|----------|-----|
| [User Guide](docs/USER_GUIDE.md) | Installing, running, using the TUI, troubleshooting |
| [Architecture](docs/ARCHITECTURE.md) | How downloads work internally, design decisions, limitations |
| [Development Guide](docs/DEVELOPMENT.md) | Building, testing, project layout, contributing |
| [Feature Comparison](docs/FEATURE_COMPARISON.md) | DNM vs aria2: what we have, what's planned, candidates to revisit |
| [Changelog](CHANGELOG.md) | What changed and which bugs were fixed |

## Project status

Early development. The core download engine is functional and covered by tests
(`tests/DotNetDownloadManager.Tests`); the features listed under *Planned* are not.

## License

[MIT](LICENSE) © 2026 Parth Makwana

## Disclaimer

For educational and personal use. Respect server policies, rate limits, and the law when
downloading content.
