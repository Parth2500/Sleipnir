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
  -v ~/Downloads:/root/Downloads dnm

# Docker: run as your own UID so the container can write to your ~/Downloads
docker run -it --rm --user "$(id -u):$(id -g)" -e HOME=/home/dnm \
  -v ~/Downloads:/home/dnm/Downloads dnm
```

- `-it` is required: DNM reads keys directly from the terminal.
- The volume mount makes downloads land on your machine instead of inside the container. DNM saves to
  `$HOME/Downloads`, so the mount target must match the `HOME` you set.
- The `--user` override is needed because the image's default `app` user (UID 1654) has no
  permission to write to your `~/Downloads`.
- `--security-opt label=disable` (SELinux systems such as Fedora) lets the container use the mount
  without relabelling your real Downloads folder, which `:Z` would do.

## The dashboard

| Area | Shows |
|------|-------|
| Header | App title and key bindings |
| Current Download (left) | The selected download: file name, status, progress, and each segment's progress |
| Download History (right) | The 10 most recent downloads, newest first |
| Footer | The selected download's status and how many downloads are active |

### Keys

| Key | Action |
|-----|--------|
| `N` | Start a new download. The dashboard is replaced by a prompt; paste the URL and press Enter. |
| `P` | Pause the selected download. Bytes already received are kept. |
| `R` | Resume the selected download. Also restarts a **failed** download from where it stopped. |
| `C` | Cancel the selected download. Transfers stop immediately and the partial file is deleted. |
| `↑` / `↓` | Select another download (same order as the history panel). |
| `Q` | Quit. |

### Statuses

| Status | Meaning |
|--------|---------|
| ○ Queued | Created, about to contact the server |
| ⬇ Downloading | Transferring data |
| ⏸ Paused | Stopped by you; press `R` to continue |
| ✓ Complete | The file is in its final location |
| ✗ Failed | A segment failed after all retries; the error is shown. Press `R` to try again |
| ✗ Cancelled | Stopped by you; the partial file has been removed |

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
"the rest" of it.

## Troubleshooting

| Problem | Cause / fix |
|---------|-------------|
| `Failed: The server did not report the file size.` | The server sends neither `Content-Length` nor `Content-Range`. Streaming downloads of unknown size are not supported yet. |
| `Failed: Response status code does not indicate success: 403/404…` | The link has expired or needs cookies/login (e.g. links copied from a logged-in browser session). |
| Download restarts from 0% after resuming | The server does not support ranges (see above). |
| Leftover `*.dnm` files in `~/Downloads` | DNM was closed while downloads were running or paused. Resuming after a restart isn't supported yet; delete the files. |
| `Invalid operation … console input` / crash on start in Docker | Run the container with `-it`. |
| Garbled boxes or icons | Use a Unicode-capable terminal font, or widen the window. |

## Limitations

- Download state lives in memory: quitting DNM forgets all downloads and paused progress.
- All downloads start immediately; there is no queue or concurrency limit yet.
- Only HTTP and HTTPS URLs that need no authentication are supported.
