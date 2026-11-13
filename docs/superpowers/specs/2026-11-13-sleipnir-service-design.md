# Sleipnir as a service: backend design

*Formerly "DotNet Download Manager" (DNM). Renamed to **Sleipnir**, Odin's eight-legged horse (eight connections), on 2026-11-13. The `dnm` command and data folder names are pending a decision.*

*Date: 2026-11-13 · Status: draft for review · Roadmap: Phase 2 (backend), Phase 3 (TUI as a client)*

Decisions are marked **[owner]** when the project owner made them in the design conversation, and
**[my call]** when the designer chose a default the owner hasn't reviewed in detail. The owner asked
for recommended defaults to be used "for now" and revisited when the time comes.

## 1. Intent

Sleipnir is becoming a **download server that other devices control**, not just an app on one machine.
A home PC runs Sleipnir in the background as a *home server* doing the heavy downloading. Everything a
person sees is a **client** of that service: today's TUI first, and later a web UI, browser
extensions and an Android app. A phone both downloads on its own and remote-controls the home
server. Family members share the server; friends run their own.

The owner's explicit priority: **build the backend robust enough now that all future clients, on
Windows, macOS, Linux and Android, and for Chrome, Firefox, Edge and Safari, can be written later
without changing it.** Order of work [owner]: backend → TUI as a client → only then everything else.

## 2. Goals, non-goals, anti-goals

**Goals (this design)**
- A background service with a stable, versioned, documented API.
- Users and roles: each user sees and controls only their own downloads; the admin sees and controls all [owner].
- Device pairing with admin approval; revocable devices [owner].
- Safe on the home network: encrypted, impostor-proof, resistant to websites and other LAN devices [owner].
- Discovery: phones find the server on the Wi-Fi without typing addresses [owner].
- Runs per-user on Linux now; designed for Windows, macOS and Android [owner].
- The existing download engine (resume, retries, Happy Eyeballs, crash-safe state) is reused unchanged in behaviour.
- The TUI becomes the first client (Phase 3), proving the API.

**Designed for, built later [owner]**
- LAN file sharing between devices (PC → phone: phone downloads a temporary link; phone → PC: resumable upload).
- Scheduling, an optional "needs approval" state, external sign-in (OIDC, e.g. Heimdall), remote access from outside the home network.
- Integrations reacting to events (Jellyfin rescan, scripts, notifications).
- Relaying: a browser on a laptop reaches the home server through the laptop's own Sleipnir.

**Non-goals (now)**
- Building the web UI, browser extensions, Android app or CLI mode.
- Access from outside the home network. The design must not rule it out.
- Streaming (Jellyfin's job). Suite integration with other projects (parked: `Asgard/docs/suite-vision.md`).
- Syncing download lists between separate Sleipnir servers.

**Anti-goals (failure even if it "works")**
- Any dependency on the internet, a cloud account or telemetry for Sleipnir to function.
- Anything on the home network can control Sleipnir without pairing; Sleipnir exposed on the LAN by default.
- A website the user visits can drive the local Sleipnir.
- Regressions in download correctness: the engine's byte-identical, crash-safe guarantees must hold.
- Single-letter or surprising behaviour changes in the TUI: Phase 3 keeps today's screens and keys.

## 3. Constraints

- .NET 10. One self-contained binary per platform (`dnm`); the existing `linux-x64` publish profile stays.
- ASP.NET Core is accepted for the server, even though it grows the binary by roughly 20–30 MB [my call].
- **Consistency with the owner's other .NET projects** [owner, after a review of heimdall-idp, CoreOps, Huginn, JsGames, Roundelay; Heimdall is the reference]:
  - Pinned SDK (`global.json`), `TreatWarningsAsErrors`, `AnalysisLevel latest-recommended`, and NuGet audit failing the build (as Heimdall's `Directory.Build.props`).
  - **Minimal APIs**, built-in **OpenAPI** (`Microsoft.AspNetCore.OpenApi`) and **Scalar** for browsable API docs.
  - **EF Core with SQLite**, snake_case naming (`EFCore.NamingConventions`). PostgreSQL stays possible later as an optional provider [owner].
  - Heimdall's secret rules adopted by number: **D3** (256-bit random secrets, stored as SHA-256), **D4** (constant-time comparison), **D5** (one error per failure kind; the real reason goes to the audit log only), **D7** (never log secrets; log a correlation ID), **D23** (CORS echoes one exact allowed origin, never `*`, never credentials).
  - Documentation like Heimdall's: `docs/threat-model.md` with numbered decisions, and ADRs in `docs/decisions/`.
  - Tests: xUnit, `WebApplicationFactory`, **`TimeProvider`/`FakeTimeProvider`** for new time-dependent code, and **CsCheck** property tests (pairing codes, path checks).
  - `ILogger` everywhere, with a local file sink. OpenTelemetry is optional and off by default (anti-goal: no phoning home).
  - The future web UI uses **Angular**, as Heimdall's ADR 001 does.
  - **Deliberate differences:** plain WebSocket (not Socket.IO/SignalR), so any client can connect without a library; the Android app in .NET (not Capacitor/Flutter), because it must run the engine.
- No root/administrator rights needed to install or run.
- Existing users' state (`~/.local/share/dnm/downloads/*.json`) is imported, not lost.
- Must keep working with no network at all (loopback only).

## 4. Architecture

```
          ┌──────────────────────── dnm daemon (one per OS user) ─────────────────────────┐
          │  Sleipnir.Server (ASP.NET Core)                                                     │
          │   ├─ REST  /api/v1/…      ├─ WebSocket /api/v1/events   ├─ auth, roles, audit │
          │   ├─ pairing (QR, code)   ├─ mDNS announcer             └─ (shares: later)    │
          │  Sleipnir.Engine (download engine, unchanged behaviour)   SQLite store             │
          └───────▲───────────────────────────────────▲───────────────────────────────────┘
       loopback HTTP 127.0.0.1                LAN HTTPS (opt-in), fingerprint-pinned
                  │                                     │
   TUI (Sleipnir.Client) on this machine       phones, laptops, later browsers via their local Sleipnir
```

### 4.1 Projects [owner, structure; names my call]

| Project | Contains | Depends on | Runs on |
|---|---|---|---|
| `Sleipnir.Core` | Models, interfaces, API contracts (DTOs, event envelopes) | — | everywhere |
| `Sleipnir.Engine` | Today's `Services/` and `Infrastructure/` (download service, segments, HTTP client, Happy Eyeballs, storage, state store). No ASP.NET, no console | Core | everywhere, incl. Android |
| `Sleipnir.Server` | API endpoints, WebSocket hub, auth, users/devices, pairing, discovery, security middleware, SQLite persistence | Core, Engine | desktop/server |
| `Sleipnir.Client` | Typed .NET client for REST + WebSocket, with reconnect | Core | TUI, later Android |
| `dnm` (app) | TUI, command dispatch (`dnm`, `dnm daemon`, `dnm service …`) | all | desktop |

The Android app (later) is .NET (MAUI or .NET for Android), reusing `Sleipnir.Engine` and `Sleipnir.Client`.
**The phone never runs `Sleipnir.Server`** [owner]: ASP.NET Core isn't supported on Android, and a
server fights the OS's battery rules.

### 4.2 Commands [owner]

- `dnm`: start the TUI; start the daemon first if it isn't running (detached), then connect.
- `dnm daemon`: run the service in the foreground (what systemd/launchd/containers call).
- `dnm service install | uninstall | status`: register automatic start for the current OS user.
- Later: `dnm add <url>` etc. (CLI mode, roadmap "Later").

## 5. Running as a service [owner]

| OS | Mechanism | Built now? |
|---|---|---|
| Linux | systemd **user** unit; docs explain `loginctl enable-linger` so it starts at boot without login | **yes** |
| Windows | Task Scheduler task "at logon", hidden | designed for, later [my call] |
| macOS | launchd LaunchAgent | designed for, later [my call] |
| Android | foreground service inside the app | later |

- **Single instance per user:** an exclusive lock on `daemon.lock` in the **data** directory (next to the state it protects; two processes that disagree about `XDG_RUNTIME_DIR`, e.g. a systemd service and a cron job, still exclude each other [review fix 2026-11-13]). A second
  `dnm daemon` exits with a clear message.
- **Runtime file** `daemon.json` (port, PID, version) tells local clients where to connect.
- **Graceful shutdown** on SIGTERM/SIGINT/SIGHUP and `POST /api/v1/server/shutdown` (admin): the
  existing `ShutdownAsync` semantics (flush, save, resume next start).

**Directories [my call]** (resolved per OS; Linux shown):

| Purpose | Linux | Windows | macOS |
|---|---|---|---|
| Settings | `$XDG_CONFIG_HOME/dnm/settings.json` | `%APPDATA%\dnm\` | `~/Library/Application Support/dnm/` |
| Data (DB, identity key) | `$XDG_DATA_HOME/dnm/` | `%LOCALAPPDATA%\dnm\` | `~/Library/Application Support/dnm/` |
| Runtime (`daemon.json`, local admin token; the lock is in Data) | `$XDG_RUNTIME_DIR/dnm/`, falling back to `$XDG_STATE_HOME/dnm/` | `%LOCALAPPDATA%\dnm\run\` | `~/Library/Application Support/dnm/run/` |

Secret files (identity private key, local admin token) are created with permissions `0600` (Unix)
or a user-only ACL (Windows), **atomically, before any content is written**. On client devices
(Android later; desktops with a keyring), keys and device tokens go in the **OS key store**
(Android Keystore, macOS Keychain, Windows DPAPI, Secret Service). Roundelay lists its plain-file
key storage as a known weakness. The `0600` file stays the fallback for headless Linux servers.

## 6. Networking

### 6.1 Listeners [owner, model; ports my call]

| Listener | Address | Protocol | Default |
|---|---|---|---|
| Loopback | `127.0.0.1` and `[::1]` | HTTP | always on, port **7766** (next free port if taken, recorded in `daemon.json`) |
| Home network | all LAN interfaces | **HTTPS only** | **off**; the admin enables it. Port **7767** (fixed, so pinned clients can reconnect; configurable) |

### 6.2 Server identity and pinning [owner]

- On first start the server generates an **ECDSA P-256 key pair and a self-signed certificate**
  (long-lived) [my call: algorithm]. The **fingerprint** is SHA-256 over the certificate's public
  key (SPKI), so certificate renewal with the same key keeps the fingerprint.
- Native clients **pin the fingerprint**, not the address or hostname; a changed IP is fine.
- `dnm identity rotate` (admin) creates a new key; every device must re-pair. Used if the key may
  have leaked.

### 6.3 Discovery [owner]

- mDNS / DNS-SD service type `_dnm._tcp`, announced **only when the LAN listener is on**.
- TXT record: display name, API version, LAN port, **short fingerprint prefix**. Nothing about users or downloads.
- Discovery is a convenience; QR codes and manual `host:port` entry always work (some networks block multicast).
- Must coexist with an OS responder already using UDP 5353 (Avahi on Linux, Bonjour on macOS,
  Windows' built-in mDNS). Prefer publishing via the OS responder where available (e.g. Avahi
  over D-Bus); otherwise a shared-socket responder [left to builder: library choice].
- Announced information is unauthenticated by nature. The fingerprint prefix only helps pick
  the right server; **trust comes from pairing** (§7).

### 6.4 Browsers [owner]

- Browser clients (web UI, extensions) talk to **the Sleipnir on their own machine over loopback**.
  Reaching another server goes through that local Sleipnir (relay: designed for, built later).
- Optional for later: the admin exports the server certificate so a browser on another machine
  can trust it once.

## 7. Users, devices, pairing [owner]

### 7.1 Model

- **User:** id, display name, role (`admin` | `member`), created. Later: an optional external
  identity (`issuer` + `subject`) for OIDC/Heimdall sign-in [owner: designed for].
- **Device:** id, owning user, name, platform, token hash, created, last seen, revoked.
- **Download:** today's model **plus an owning user**. Imported downloads belong to the admin.
- The person who first runs the server is its **admin**. More admins are allowed [my call];
  the last admin can't be demoted or deleted.

### 7.2 Credentials [my call]

- Each device gets a **random 256-bit token** (base64url), sent as `Authorization: Bearer …`.
  The server stores **only its SHA-256 hash**. Revocation is immediate, so no JWTs are needed.
- **Local admin:** the daemon writes a token for a built-in "This computer" admin device into the
  runtime directory (`0600`). The local TUI reads it. The OS user's file permissions are the proof [owner].
- Tokens never appear in URLs, logs or WebSocket query strings (see §8.3 for WebSocket auth).

### 7.3 Pairing path A: QR code (or link)

1. The admin creates an **invitation** for a user (TUI or an admin device). It is single-use and
   expires after 10 minutes [my call].
2. Payload: LAN address(es), port, full fingerprint, invitation secret, server name. It is shown
   as a QR code and as a copyable link (`dnm://pair?...`) [my call: scheme].
3. The new device connects over HTTPS, **checks the fingerprint against the payload**, and redeems
   the secret with its name and platform. It receives its device token.
4. Creating the invitation *is* the admin's approval, so no second step.

### 7.4 Pairing path B: discover, then confirm a code (headless servers)

1. The device finds the server via mDNS (or a manual address) and opens HTTPS, **trusting on
   first use for this request only**.
2. It sends a **join request**: device name, platform, and a random device nonce.
3. Both sides compute a **6-digit short authentication string** bound to the fingerprint the
   device actually saw, the device nonce, and a server nonce. An attacker in the middle presents
   a different certificate, so the codes differ. (Exact construction is left to the builder;
   requirement: an attacker must not be able to make the codes match except by luck, about one in
   a million.)
4. Admin clients get a live event; the admin compares the codes, **picks the user** and approves
   (or rejects).
5. The device, waiting on its request, receives its token and **pins the fingerprint**.

**Limits [my call]:** at most 5 pending requests; each expires after 5 minutes; rate-limited per
source address; rejections and expiries are audited.

### 7.5 Where files go, fairness, and deleting users [my call]

- **Destinations:** the admin's default folder is `~/Downloads`. Each member defaults to
  `~/Downloads/<member name>/`. The admin can change a user's allowed root. All files are owned by
  the OS user running the daemon; members get their files through future clients (sharing, Jellyfin).
- **Ownership** lives on the engine's `DownloadTask` (an `OwnerId` in `Sleipnir.Core`); the server
  filters by it, and the engine's queue stays global.
- **Fairness:** one global FIFO queue (`MaxConcurrentDownloads`, as today). Per-user fairness is a
  later setting.
- **Deleting a user** cancels their running downloads, removes their records and devices, and keeps
  their files on disk. It requires confirmation and is audited.

### 7.6 Authorization rules

| Action | member | admin |
|---|---|---|
| List/see/act on downloads | own only | all (filter by user) |
| Add a download | yes, to own folder | yes, any allowed folder |
| Download from private/loopback/link-local addresses | **no** (SSRF guard, §9) | yes |
| Manage users, invitations, pairing requests, LAN mode, identity rotation | no | yes |
| See/revoke devices | own | all |
| Remove own finished downloads | yes | all |

## 8. API (v1)

### 8.1 Conventions [owner, style; details my call]

- Base path `/api/v1`. JSON, camelCase, ISO-8601 UTC timestamps, string enums.
- Errors use **RFC 9457** `application/problem+json`, with a stable `code` field (e.g. `download_not_found`).
- **Compatibility:** within v1, changes are **additive only**. Clients must ignore unknown fields
  and event types. Breaking changes mean `/api/v2`, with v1 kept for a deprecation period.
- `GET /api/v1/server` (unauthenticated): name, version, API versions, fingerprint. Nothing else.
- **OpenAPI** document served at `/api/v1/openapi.json` and checked into the repo. Builds fail if they drift.
- `POST /downloads` accepts an `Idempotency-Key` header so a phone retrying after a dropped
  connection doesn't add the same download twice.
- Lists are paginated (cursor-based).

### 8.2 Resources (outline; exact shapes left to the builder within these rules)

| Endpoint | Purpose |
|---|---|
| `GET /server` | public info (above) |
| `POST /server/shutdown` | admin: graceful stop |
| `GET /me` | current user and device |
| `GET/POST /downloads` | list (own; admin: all or `?user=`), add one or several URLs with an optional destination |
| `GET /downloads/{id}` | one download, including segments |
| `POST /downloads/{id}/pause` · `/resume` · `/cancel` | control |
| `DELETE /downloads/{id}` | remove a finished download from the list (file kept) |
| `GET/POST/PATCH/DELETE /users[/{id}]` | admin: manage users |
| `GET /devices` · `DELETE /devices/{id}` | list, revoke |
| `POST /pairing/invitations` | admin: create a QR invitation |
| `POST /pairing/redeem` | device: redeem an invitation |
| `POST /pairing/requests` · `GET /pairing/requests/{id}` | device: join request, wait for a decision |
| `GET /pairing/requests` · `POST /pairing/requests/{id}/approve` · `/reject` | admin |
| `GET /audit` | admin: security events |
| `PUT /settings/lan` | admin: enable/disable the LAN listener and discovery |
| `POST /events/ticket` | get a single-use ticket for the WebSocket (below) |
| `GET /events` | WebSocket |

### 8.3 Live events (WebSocket) [owner]

- **Authentication:** browsers can't set headers on a WebSocket. So the client first calls
  `POST /events/ticket` with its token and receives a **single-use, 30-second ticket**, then
  connects to `/events?ticket=…` [my call].
- **Envelope:** `{ "v": 1, "seq": 123, "type": "download.progress", "at": "…", "data": { … } }`.
- **Types (initial):** `download.added`, `download.changed` (status, error), `download.progress`
  (bytes, speed; **throttled** to at most a few per second per client and batched), `download.removed`,
  `pairing.requested` and `pairing.resolved` (admins), `device.revoked` (to that device:
  disconnect), `server.shuttingDown`.
- Each client only receives events for downloads it may see (§7.6).
- **Resync:** after reconnecting, a client fetches a snapshot (`GET /downloads`), then applies
  events with higher `seq`. No event replay is promised.
- Heartbeat ping/pong; idle connections closed; per-user connection limit.

## 9. Security [owner: "keep the edge cases in mind"]

Threat-model checklist. Each item needs a test.

| Threat | Mitigation |
|---|---|
| A website drives the loopback API (CSRF) | Bearer token in a header only, never cookies; strict CORS allowlist (none by default; extension and web-UI origins added when those clients exist) |
| DNS rebinding against loopback | Reject requests whose `Host` isn't `localhost`, `127.0.0.1`, `[::1]` or the server's own LAN names/addresses |
| Another LAN device uses the API | LAN listener off by default; every endpoint except `GET /server` and the pairing endpoints requires a device token |
| Guessing invitations or flooding pairing | 256-bit single-use secrets; pending-request cap, expiry, per-source rate limits; audit |
| Impostor server (MITM) | Fingerprint pinning; the QR carries the fingerprint; the 6-digit code binds the fingerprint for discovered servers |
| Stolen/lost device, leaked token | Revocation (immediate; hash lookup per request); `last seen` shown to the admin |
| Server key leak | `dnm identity rotate` → re-pair |
| SSRF: a member makes the server fetch internal hosts (router admin page, other services) | For non-admins, reject destinations whose **resolved** address is loopback, private (RFC 1918 / ULA), link-local, multicast or unspecified. Enforced **at connect time** in the existing `ConnectCallback`, so it covers redirects and DNS rebinding too |
| Destination folder escape (`../`, symlinks) | Per-user allowed roots set by the admin; canonicalize and resolve symlinks; the final path must stay inside the root; file names already sanitized by `FileNameResolver` |
| Other OS users on the same machine | Per-user daemon; secrets `0600`/user-only ACL; loopback port alone grants nothing without a token |
| Brute force against tokens | 256-bit tokens; global and per-source rate limiting on auth failures |
| Resource exhaustion | Request size limits, WebSocket connection limits, existing download queue limits |
| Secrets in logs | Tokens, invitation secrets and tickets are never logged; logs live in the state directory |

An **audit log** (SQLite table, admin-readable) records pairing, approvals, rejections, revocations,
role changes, LAN-mode changes, identity rotation and repeated auth failures [my call].

## 10. Persistence [my call]

- **SQLite** (`dnm.db` in the data directory) via **EF Core** (SQLite provider, snake_case naming)
  with migrations [owner: SQLite by default, PostgreSQL possible later]. It holds users, devices, invitations, pairing requests, audit, and download
  records (a SQLite implementation of the engine's `IDownloadStateStore`, keeping the
  *PersistedBytes-only* crash-safety rule).
- On first start, existing JSON download records are imported (owner = admin), and the JSON folder
  is left untouched as a backup.
- SQLite also runs on Android, so the phone app can reuse the same store.

## 11. Phase 3: the TUI as a client

- `RemoteDownloadService : IDownloadService` in `Sleipnir.Client` (REST for commands, WebSocket for
  state), so `TuiController` and `TuiRenderer` keep working with minimal change.
- `dnm` starts the daemon if needed, waits for `daemon.json`, connects with the local admin token.
- Closing the TUI leaves downloads running. The TUI shows "reconnecting…" if the daemon restarts.
- Admin additions: an owner column/filter for downloads; screens to create invitations (QR rendered
  in the terminal), approve requests (showing the 6-digit code), manage users and devices, and
  toggle LAN mode. Same look as today.
- There is no in-process mode any more: one code path [my call].

## 11a. Keeping the app working during Phase 2 [my call]

Phase 2 is delivered in steps that each leave `dnm` usable. During Phase 2 the existing TUI keeps
running the engine in-process as today, while `dnm daemon` is added alongside it. Phase 3 switches
the TUI to the daemon and removes the in-process path. The library split (§4.1) comes first, as a
pure refactor with all existing tests green.

## 12. Testing strategy

- Engine: the existing 113 tests keep passing unchanged (moved projects only).
- Server: in-memory API tests (`WebApplicationFactory`) for every endpoint and authorization rule.
- Security: a test per row in §9 (Host header, CORS, token, revoked device, SSRF incl. redirect
  and rebinding, path escape, pairing limits, SAS mismatch under MITM).
- WebSocket: tickets, filtering by user, throttling, resync.
- Contract: OpenAPI document generated and diffed in tests.
- End to end: a real `dnm daemon` process with the TUI's client against loopback HTTP, plus a LAN
  HTTPS pinning test.

## 13. Left to the builder

- Exact JSON shapes, endpoint names within §8's outline, SQL schema, migration tooling.
- The SAS construction (meeting §7.4's requirement), the mDNS library or OS integration.
- Logging library and format; port fallback details; QR rendering in the terminal.
- Order of work within Phase 2, as long as each step keeps the app working and the tests green.

## 14. Calls made by the designer (for the owner to check)

ASP.NET Core accepted (+20–30 MB) · consistency rules from Heimdall (above) · ports 7766 (loopback) / 7767 (LAN) · ECDSA P-256 identity,
SPKI fingerprint · opaque random tokens, stored hashed · WebSocket single-use tickets ·
invitations expire after 10 minutes, join requests after 5 minutes, at most 5 pending · `dnm://` link scheme · multiple
admins allowed · member folders `~/Downloads/<name>/` · ownership on the engine's task, one global queue ·
deleting a user keeps files · in-process TUI kept until Phase 3 · SQLite for all server state · JSON state imported, old files kept · Windows/macOS
service installers later · no in-process mode after Phase 3 · audit log · per-user allowed
destination roots · non-admin SSRF guard.
