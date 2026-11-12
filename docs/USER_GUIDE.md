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
- `podman stop` / `docker stop` shut DNM down gracefully and save progress, just like quitting.

## The screen

DNM takes over the terminal (like `htop` or `less`) and gives it back when you quit.

| Area | Shows |
|------|-------|
| Header | How many downloads are active and the total speed |
| List | Every download, newest first: status icon, name, progress bar, percentage, speed and time remaining. The selected one is marked with `›` |
| Details | Press `Enter` on a download to expand it: URL, where it's saved, progress, and every segment (connection) |
| Messages | Short confirmations or errors ("Added movie.mkv", "That doesn't look like a URL") that fade after a few seconds |
| Input box | Paste a URL here, or type `/` for commands |
| Hints | The keys that work right now. They change as you type |

### Adding downloads

Paste a URL into the input box and press `Enter`. Paste several URLs at once (separated by
spaces or new lines) and DNM adds them all. `/add <url>` does the same.

### Keys

These work when the input box is **empty**. Once you start typing, every key goes into the box.

| Key | Action |
|-----|--------|
| `↑` / `↓` (`PgUp` / `PgDn`) | Select a download |
| `Space` | Pause the selected download, or resume it (also retries a **failed** one) |
| `Enter` | Show or hide its details |
| `Del` then `Del` | Cancel it. Transfers stop immediately and the partial file is deleted. The second press confirms |
| `?` | Help |
| `Esc` | Clear the input box, or close details |
| `Ctrl+C` | Clear the input box. On an empty box, press it **twice** to quit |

While typing, the usual editing keys work: `←` `→`, `Home` / `End` (or `Ctrl+A` / `Ctrl+E`),
`Backspace`, `Delete`, `Ctrl+W` (delete word), `Ctrl+U` / `Ctrl+K` (delete to start / end).

### Commands

Type `/` and a menu appears. Keep typing to filter it, `↑` `↓` to choose, `Tab` to complete,
`Enter` to run. Short forms work too (`/pa` is `/pause`).

| Command | Does |
|---------|------|
| `/add <url>` | Download a URL |
| `/pause` | Pause the selected download |
| `/resume` | Resume or retry the selected download |
| `/cancel` | Cancel the selected download and delete its partial file |
| `/clear` | Remove finished (completed and cancelled) downloads from the list. The files stay on disk |
| `/help` | Show keys and commands |
| `/quit` | Save progress and exit |

### Statuses

| Status | Meaning |
|--------|---------|
| ○ queued | Waiting for a free slot (at most 3 downloads run at once), or about to contact the server |
| ↓ (progress bar) | Transferring data |
| ‖ paused | Stopped by you; press `Space` to continue |
| ✓ done | The file is in its final location |
| ✗ failed | Gave up after all retries; the error is shown. Press `Space` to try again |
| ✗ cancelled | Stopped by you; the partial file has been removed |

In the details, a segment marked **waiting** is pausing between retries, for example because the
server asked DNM to slow down.

## Quitting and coming back later

DNM remembers your downloads. When you quit (`Ctrl+C` twice, `/quit`, closing the terminal, or `kill`):

- **Running** downloads are stopped cleanly: data already received is written to disk, then they are
  marked to **resume automatically** on the next start.
- **Paused** downloads stay paused; press `Space` to continue them.
- **Failed** downloads stay failed; press `Space` to retry.
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
| `Failed: Server is busy (HTTP 429)…` | The server kept rate-limiting after 10 polite retries. Wait a while and press `Space`. |
| `Failed: No data received for 30 s…` / `Timed out connecting…` | The server or network stopped responding, and retries didn't help. Check your connection and press `Space`. |
| Download restarts from 0% after resuming | The server does not support ranges (see above). |
| Leftover `*.dnm` files in `~/Downloads` | Normally these belong to paused/queued downloads and are resumed. If DNM's saved state was deleted, they are orphans and safe to delete. |
| Downloads don't come back after restarting the container | Add the `-v dnm-state:…` volume (see [Podman / Docker](#podman--docker)). |
| `Invalid operation … console input` / crash on start in Docker | Run the container with `-it`. |
| Garbled boxes or icons | Use a Unicode-capable terminal font, or widen the window. |

## Limitations

- Only HTTP and HTTPS URLs that need no authentication are supported.
- No speed limits, checksum verification, or settings file yet (see the roadmap).
- The download folder is always `~/Downloads`.
