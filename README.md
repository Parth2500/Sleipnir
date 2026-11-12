# DotNet Download Manager (DNM)

A multi-threaded **download manager for the terminal**, written in C# / .NET 10 with a
full-screen, keyboard-first interface built on [Spectre.Console](https://spectreconsole.net/).

DNM splits a file into byte ranges, downloads them in parallel over HTTP, and stitches them into
one file. It can pause, resume (even after a restart), cancel, queue, and retry segments that fail.

```
⬇ DotNet Download Manager  1 active · 10.6 MB/s

   ✓ 1Mb.dat                           done · 1 MB
 › ↓ 100Mb.dat                         ██████████████████████░░░░░░░░░░░░░░░  59%  10.6 MB/s · 0:03
       URL       https://proof.ovh.net/files/100Mb.dat
       Saved to  /root/Downloads/100Mb.dat
       Progress  59.32 MB of 100 MB (59.3%) · 8 connections
       Segments  Seg 1 ✓        Seg 2 ✓        Seg 3 29%      Seg 4 ✓
                 Seg 5 waiting  Seg 6 45%      Seg 7 waiting  Seg 8 ✓

╭──────────────────────────────────────────────────────────────────────────────────────────────╮
│ >  Paste a URL, or type / for commands                                                       │
╰──────────────────────────────────────────────────────────────────────────────────────────────╯
  ↑↓ select · Space pause/resume · Enter details · Del cancel · ? for help
```

## Quick start

**Prebuilt binary (64-bit Linux, no .NET needed):** build it with
`dotnet publish -p:PublishProfile=linux-x64` (see [Development Guide](docs/DEVELOPMENT.md#production-build-linux)), then:

```bash
install -m 755 artifacts/linux-x64/dnm ~/.local/bin/dnm   # make sure ~/.local/bin is on your PATH
dnm
```

**From source:**

```bash
# Requires the .NET 10 SDK (Fedora: sudo dnf install dotnet-sdk-10.0)
dotnet run --project "DotNet Download Manager.csproj"
```

**Paste a URL and press Enter.** That's it. Files are saved to `~/Downloads`.

With the input box empty, a few keys work on the selected download:

| Key | Action |
|-----|--------|
| `↑` `↓` | Select a download |
| `Space` | Pause / resume (also retries a failed one) |
| `Enter` | Show / hide details (segments, path, errors) |
| `Del` `Del` | Cancel and delete the partial file |
| `?` | Help |
| `Ctrl+C` `Ctrl+C` | Quit; running downloads resume next time |

Or type `/` for commands with autocomplete: `/add`, `/pause`, `/resume`, `/cancel`, `/clear`,
`/help`, `/quit`.

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
- Full-screen, keyboard-first interface: paste-to-download input box, `/` commands with
  autocomplete, expandable details, help screen, and short confirmations
- Persistent history; `/clear` tidies finished downloads away (files are kept)

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
