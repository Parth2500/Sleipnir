# Feature Comparison: DNM vs aria2

A reference for deciding what to build next. [aria2](https://github.com/aria2/aria2) (GPL-2.0, C++)
is the most complete open-source command-line downloader, so it serves as the benchmark.

> **Goal:** DNM aims to be an excellent **HTTP download manager** with a good UI and browser
> integration, not a full aria2 replacement. Use this file to decide case by case whether an
> aria2 feature is worth adding.

*Last reviewed: 2026-11-12. Roadmap: [issue #1](https://github.com/Parth2500/DotNet-Download-Manager/issues/1).*

**Legend:** ✅ have it · 🗺️ on the roadmap · 💡 candidate, not planned yet · ❌ out of scope for now

## HTTP downloading (DNM's core)

| Feature | aria2 | DNM | Notes |
|---|:---:|:---:|---|
| Parallel segmented HTTP/HTTPS downloads | ✅ (up to 16) | ✅ (up to 8) | Segment count is set in `DownloadOptions` |
| Single-stream fallback without Range support | ✅ | ✅ | Also handles servers that advertise ranges but ignore them |
| Pause / resume in one session | ✅ | ✅ | |
| Resume after restart | ✅ | 🗺️ Phase 1 | Persist task and segment state as JSON next to the `.dnm` file |
| Per-segment retry with backoff | ✅ | ✅ | |
| Stall / idle timeout | ✅ | 🗺️ Phase 1 | |
| Unknown-size (chunked) downloads | ✅ | 🗺️ Phase 1 | |
| Download queue / max concurrent | ✅ | 🗺️ Phase 1 | |
| Speed and time remaining | ✅ | 🗺️ Phase 1 | |
| Speed limits (global and per download) | ✅ | 🗺️ Phase 2 | |
| Checksum verification | ✅ | 🗺️ Phase 2 | |
| Batch input (URL list file) | ✅ | 🗺️ Phase 2 | |
| Custom headers, cookies, basic auth | ✅ | 🗺️ Phase 2 | |
| HTTP / SOCKS proxies | ✅ | 🗺️ Phase 2 | |
| Safe file naming / no overwrite | ✅ | ✅ | |
| `.netrc` credentials | ✅ | 💡 | Small; pairs with the auth work |
| Import cookies from a browser cookie file | ✅ | 💡 | Small to medium |
| Conditional download (skip if unchanged) | ✅ | 💡 | Small |
| Async DNS, many low-level tuning options | ✅ | 💡 | Only if a real need appears |

## Beyond single-server HTTP

| Feature | aria2 | DNM | Effort | Notes |
|---|:---:|:---:|---|---|
| **Multi-source / mirror downloads** (one file from several URLs) | ✅ | 💡 | Medium | **Best candidate.** The segment design already fits: each segment just picks a different URL. Needs size/ETag agreement between mirrors and per-mirror failover. |
| **Metalink** (mirror lists + per-piece checksums) | ✅ | 💡 | Large | Builds on multi-source; mostly used by Linux distro mirrors |
| **FTP / SFTP** | ✅ | ❌ | Medium | Would need new `INetworkClient` implementations; FTP use is declining |
| **BitTorrent** (magnet links, DHT, peer exchange, seeding, encryption) | ✅ | ❌ | Huge | Effectively a separate project. Wrapping a library such as MonoTorrent is the only realistic route. |

## Control, integration and UI

| Feature | aria2 | DNM | Notes |
|---|:---:|:---:|---|
| Remote control API | ✅ JSON-RPC / XML-RPC / WebSocket | 🗺️ Phase 3 (own local API) | 💡 An aria2-compatible JSON-RPC layer would let existing front ends (AriaNg, Motrix) and extensions work with DNM |
| Non-interactive CLI mode | ✅ (its main mode) | 🗺️ Phase 3 | `dnm <url> [-o dir]` |
| Built-in interactive UI | ❌ (third-party front ends only) | ✅ live TUI dashboard | **DNM advantage** |
| Web dashboard | ❌ (via AriaNg) | 💡 | Comes almost for free once the local API exists |
| First-party browser extension | ❌ (third-party) | 🗺️ Phase 3 | **Potential DNM advantage** |
| Desktop notifications | ❌ (hooks only) | 🗺️ Phase 3 | |
| Event hooks (run a command on completion) | ✅ | 💡 | Small; useful for scripts |

## Summary

- **After the roadmap**, DNM roughly matches aria2 for **single-server HTTP/HTTPS** downloading.
- **The remaining gaps** are multi-source/Metalink, FTP/SFTP, BitTorrent, RPC compatibility,
  and a long tail of edge cases aria2 has accumulated over about 20 years.
- **DNM's edge** is usability: a built-in live UI, a planned web dashboard, first-party browser
  integration, and a small tested .NET codebase.
- A fair description after the roadmap: *"a modern HTTP download manager for .NET with a live
  dashboard and browser integration, covering the core HTTP features of tools like aria2."*

## When revisiting

1. Re-check aria2's current feature list (`aria2c --help=#all` and its docs).
2. For each 💡 item, ask: will I actually use this? Does it fit the existing design?
3. If yes, move it to the roadmap issue and change its status here to 🗺️.

**Licensing:** aria2 is GPL-2.0-or-later and DNM is MIT. Reading aria2 to learn how it works is
fine. Copying its code into DNM would require relicensing DNM under the GPL.
