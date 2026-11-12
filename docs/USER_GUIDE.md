# User Guide

## Requirements

- .NET 10 SDK (to build) or .NET 10 runtime (to run a published build)
  - Fedora: `sudo dnf install dotnet-sdk-10.0`
  - Ubuntu 24.04+: `sudo apt install dotnet-sdk-10.0`
  - Others: <https://dotnet.microsoft.com/download/dotnet/10.0>
- A terminal at least ~100 columns wide that supports Unicode (most modern terminals do)

## Running

### From source

```bash
dotnet run --project "DotNet Download Manager.csproj"
```

### Published build

```bash
dotnet publish "DotNet Download Manager.csproj" -c Release -o out
dotnet "out/DotNet Download Manager.dll"
```

### Podman / Docker

```bash
podman build -t dnm .

# Podman (rootless): container root = you, so files in ~/Downloads are owned by you
podman run -it --rm --security-opt label=disable --user root -e HOME=/root \
  -v ~/Downloads:/root/Downloads -v dnm-state:/root/.local/share/dnm dnm

# Docker: run as your own UID so the container can write to your ~/Downloads
docker run -it --rm --user "$(id -u):$(id -g)" -e HOME=/home/dnm \
  -v ~/Downloads:/home/dnm/Downloads -v dnm-state:/home/dnm/.local/share/dnm dnm
```

- `-it` is required: DNM reads keys directly from the terminal.
- The volume mount makes downloads land on your machine instead of inside the container. DNM saves to
  `$HOME/Downloads`, so the mount target must match the `HOME` you set.
- The `--user` override is needed because the image's default `app` user (UID 1654) has no
  permission to write to your `~/Downloads`.
- `--security-opt label=disable` (SELinux systems such as Fedora) lets the container use the mount
  without relabelling your real Downloads folder, which `:Z` would do.
- `-v dnm-state:…` keeps DNM's saved download list in a named volume, so downloads resume and
  history survives between container runs. Without it, every run starts empty.
- `podman stop` / `docker stop` shut DNM down gracefully and save progress, just like pressing `Q`.

## The dashboard

| Area | Shows |
|------|-------|
| Header | App title and key bindings |
| Current Download (left) | The selected download: file name, status, progress, speed, time remaining (ETA), and each segment's progress |
| Download History (right) | The 10 most recent downloads, newest first (kept across restarts) |
| Footer | The selected download's status, how many downloads are active, and the total speed |

### Keys

| Key | Action |
|-----|--------|
| `N` | Start a new download. The dashboard is replaced by a prompt; paste the URL and press Enter. |
| `P` | Pause the selected download. Bytes already received are kept. |
| `R` | Resume the selected download. Also restarts a **failed** download from where it stopped. |
| `C` | Cancel the selected download. Transfers stop immediately and the partial file is deleted. |
| `↑` / `↓` | Select another download (same order as the history panel). |
| `Q` | Quit. Running downloads are stopped cleanly and **resume automatically** the next time you start DNM. `Ctrl+C` does the same; press it twice to exit immediately. |

### Statuses

| Status | Meaning |
|--------|---------|
| ○ Queued | Waiting for a free slot (at most 3 downloads run at once), or about to contact the server |
| ⬇ Downloading | Transferring data |
| ⏸ Paused | Stopped by you; press `R` to continue |
| ✓ Complete | The file is in its final location |
| ✗ Failed | A segment failed after all retries; the error is shown. Press `R` to try again |
| ✗ Cancelled | Stopped by you; the partial file has been removed |

## Quitting and coming back later

DNM remembers your downloads. When you quit (`Q`, `Ctrl+C`, closing the terminal, or `kill`):

- **Running** downloads are stopped cleanly: data already received is written to disk, then they are
  marked to **resume automatically** on the next start.
- **Paused** downloads stay paused; press `R` to continue them.
- **Failed** downloads stay failed; press `R` to retry.
- **Finished** downloads stay in the history (the 200 most recent are kept).

Even after a crash or power cut, progress is safe: DNM only records bytes that have actually been
written to disk, so a resume can never skip data. You may lose up to a few seconds of progress.

The saved state lives in `~/.local/share/dnm/downloads/` (one small JSON file per download; it
follows `$XDG_DATA_HOME` if set). Deleting that folder makes DNM forget all downloads; the files in
`~/Downloads` are not touched.

## Where files go

Downloads are saved to `~/Downloads` (created if missing).

- **File name**: taken from the `filename` query parameter if present (plus `ext` if given),
  otherwise from the last part of the URL path — e.g. `https://host/a/b/report%20v2.pdf` →
  `report v2.pdf`. Directory parts and unsafe characters are removed, so a URL can never write
  outside the download folder.
- **No overwrites**: if `name.ext` already exists, the download is saved as `name (1).ext`,
  `name (2).ext`, and so on.
- **While downloading**, data is written to `name.ext.dnm`. It is renamed to `name.ext` only
  once every byte has arrived, so a file without `.dnm` is always complete.

## How fast is it?

When the server supports HTTP Range requests (most file hosts and CDNs do), DNM opens up to
8 parallel connections, one per segment of at least 1 MB. Small files use fewer segments.

If the server does not support ranges, DNM downloads over a single connection. In that case a
pause/resume or retry has to restart the file from the beginning, because the server cannot send
"the rest" of it. The same applies when the server doesn't report the file size at all: DNM then
downloads until the server ends the transfer, and shows bytes received instead of a percentage.

At most **3 downloads** transfer at the same time; others wait as *Queued* and start automatically
when a slot frees up.

### When things go wrong, DNM retries for you

| Situation | What DNM does |
|-----------|---------------|
| Connection dropped / network error | Retries the segment up to 3 times (after 1 s, 2 s, 3 s), continuing from the bytes it already has |
| Connection open but no data for 30 s | Treats it as dropped and retries |
| Server says "too many requests" / "busy" (HTTP 429 / 503) | Waits as long as the server asks (`Retry-After`), or backs off up to 60 s, up to 10 times |
| File not found, forbidden, gone (HTTP 404 / 403 / 410…) | Fails immediately: retrying won't help |
| IPv6 is broken on your network | Tries IPv6 and IPv4 in parallel (like browsers do) and uses whichever connects first |

## Troubleshooting

| Problem | Cause / fix |
|---------|-------------|
| `Failed: Response status code does not indicate success: 403/404…` | The link has expired or needs cookies/login (e.g. links copied from a logged-in browser session). |
| `Failed: Server is busy (HTTP 429)…` | The server kept rate-limiting after 10 polite retries. Wait a while and press `R`. |
| `Failed: No data received for 30 s…` / `Timed out connecting…` | The server or network stopped responding, and retries didn't help. Check your connection and press `R`. |
| Download restarts from 0% after resuming | The server does not support ranges (see above). |
| Leftover `*.dnm` files in `~/Downloads` | Normally these belong to paused/queued downloads and are resumed. If DNM's saved state was deleted, they are orphans and safe to delete. |
| Downloads don't come back after restarting the container | Add the `-v dnm-state:…` volume (see [Podman / Docker](#podman--docker)). |
| `Invalid operation … console input` / crash on start in Docker | Run the container with `-it`. |
| Garbled boxes or icons | Use a Unicode-capable terminal font, or widen the window. |

## Limitations

- Only HTTP and HTTPS URLs that need no authentication are supported.
- No speed limits, checksum verification, or settings file yet (see the roadmap).
- The download folder is always `~/Downloads`.
