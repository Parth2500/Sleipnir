# Sleipnir as a service: backend design

*Formerly "DotNet Download Manager" (DNM). Renamed to **Sleipnir**, Odin's eight-legged horse (eight connections), on 2026-11-13. The command and the data folder are renamed from `dnm` to `sleipnir`, with no transition period [owner, 2026-11-14]. This doc still writes `dnm` where it describes what is built today.*

*Revised 2026-11-14 after the suite decisions S1 to S14 (`Asgard/docs/decisions.md`). What changed:*

- *Sleipnir has **two modes** (S6). **Alone:** one machine, one person, loopback only, no users and no pairing. **Under Bifröst:** an extension that Bifröst starts and routes to.*
- *Users, roles, devices, tokens, pairing, the LAN listener, the home key, discovery and the audit log **move to Bifröst** (S4). Sections 6 and 7 below are rewritten. The identity code built on 2026-11-13 (`IdentityService`, the SQLite store) is the seed of Bifröst and is extracted, not thrown away.*
- *Bifröst decides **where a download goes** and who may see it (S11).*
- *There is no OIDC sign-in, now or later (S2). Other devices use the one Bifröst client app (S8).*

*Decisions marked **[owner: S…]** refer to that log. Bifröst's design is `Asgard/Bifrost/docs/design.md`.*

*Date: 2026-11-13 · Status: draft for review · Roadmap: Phase 2 (backend), Phase 3 (TUI as a client)*

Decisions are marked **[owner]** when the project owner made them in the design conversation, and
**[my call]** when the designer chose a default the owner hasn't reviewed in detail. The owner asked
for recommended defaults to be used "for now" and revisited when the time comes.

## 1. Intent

Sleipnir is a download engine behind a stable API, with two ways to run it [owner: S6]:

- **Alone.** A background service on one machine, for the person at that machine. The TUI is its
  first client. No users, no pairing, loopback only.
- **Under Bifröst.** The home's download manager. Bifröst gives it people and devices, and Sleipnir
  gives Bifröst downloading. Family members share it from the Bifröst client app. Friends run their
  own home.

Bifröst extends Sleipnir with other devices. Sleipnir extends Bifröst with downloads.

The owner's explicit priority: **build the backend robust enough now that all future clients, on
Windows, macOS, Linux and Android, and for Chrome, Firefox, Edge and Safari, can be written later
without changing it.** Order of work [owner]: backend → TUI as a client → only then everything else.

## 2. Goals, non-goals, anti-goals

**Goals (this design)**
- A background service with a stable, versioned, documented API.
- Ownership: under Bifröst, each person sees and controls only their own downloads, and the admin sees and controls all [owner]. Bifröst says who is calling.
- A pending state: a download can wait for an answer from the admin before it starts [owner: S12].
- Safe on one machine: resistant to websites and to other accounts on that machine [owner].
- Runs alone per-user on Linux now; designed for Windows and macOS [owner].
- Not Sleipnir's any more: users, roles, devices, pairing, encryption on the home network, and discovery. Bifröst owns them [owner: S4].
- The existing download engine (resume, retries, Happy Eyeballs, crash-safe state) is reused unchanged in behaviour.
- The TUI becomes the first client (Phase 3), proving the API.

**Designed for, built later [owner]**
- LAN file sharing between devices. It may become its own Bifröst extension, "Files", because it depends on pairing and not on downloading [owner: S5].
- Scheduling.
- Events other extensions react to. Saga scans a folder on "download finished" [owner: S11].
- The engine on the phone, inside the Bifröst client app, so a phone can pre-download on home Wi-Fi [owner: S13].

**Non-goals (now)**
- Building the web UI, browser extensions, Android app or CLI mode.
- Access from outside the home network. The design must not rule it out.
- Streaming (Saga's job, Jellyfin's until then).
- Sign-in of any kind, OIDC included [owner: S2].
- Reaching another machine's Sleipnir from a browser. A browser is used on the host only [owner: S8].
- Syncing download lists between separate Sleipnir servers.

**Anti-goals (failure even if it "works")**
- Any dependency on the internet, a cloud account or telemetry for Sleipnir to function.
- Sleipnir opens a port on the home network. Alone it listens on loopback only. Under Bifröst it listens on Bifröst's private socket only.
- Sleipnir writes to a path that did not come from its own setting (alone) or from Bifröst (extension).
- Sleipnir alone stops working, or needs Bifröst [owner: S6].
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
- Alone: no root/administrator rights needed to install or run. Under Bifröst, Bifröst installs and starts it under Bifröst's service account [owner: S12].
- Existing users' state (`~/.local/share/dnm/downloads/*.json`) is imported, not lost.
- Must keep working with no network at all (loopback only).

## 4. Architecture

```
 ALONE                                          UNDER BIFRÖST
 ┌──── sleipnir daemon (one per OS user) ───┐   ┌──────────── Bifröst (system service) ───────────┐
 │ Sleipnir.Server: REST + WebSocket       │   │ people · devices · pairing · folders · events   │
 │ local token · Host allowlist            │   │        │ private socket, identity in headers    │
 │ Sleipnir.Engine                         │   │ ┌──────▼─────────── sleipnir ─────────────────┐ │
 └────────▲────────────────────────────────┘   │ │ Sleipnir.Server: same REST + events         │ │
   loopback HTTP 127.0.0.1                     │ │ Sleipnir.Engine                             │ │
          │                                    │ └─────────────────────────────────────────────┘ │
   TUI (Sleipnir.Client) on this machine       └────────▲──────────────────────────▲─────────────┘
                                                 Bifröst client app            browser on the host
```

One engine and one API. Only the front door differs.

### 4.1 Projects [owner, structure; names my call]

| Project | Contains | Depends on | Runs on |
|---|---|---|---|
| `Sleipnir.Core` | Models, interfaces, API contracts (DTOs, event envelopes) | — | everywhere |
| `Sleipnir.Engine` | Today's `Services/` and `Infrastructure/` (download service, segments, HTTP client, Happy Eyeballs, storage, state store). No ASP.NET, no console | Core | everywhere, incl. Android |
| `Sleipnir.Server` | API endpoints, events, the two front doors (loopback with a local token; Bifröst's socket with identity headers), security middleware, persistence | Core, Engine | desktop/server |
| `Sleipnir.Client` | Typed .NET client for REST + WebSocket, with reconnect | Core | TUI, later Android |
| `dnm` (app) | TUI, command dispatch (`dnm`, `dnm daemon`, `dnm service …`) | all | desktop |

There is no Sleipnir Android app. The one Bifröst client app is .NET for Android [owner: S13] and can later reuse `Sleipnir.Engine` and `Sleipnir.Client`.
**The phone never runs `Sleipnir.Server`** [owner]: ASP.NET Core isn't supported on Android, and a
server fights the OS's battery rules.

### 4.2 Commands [owner]

Written with the new command name. Today's build still installs `dnm`.

These are the commands of Sleipnir alone. Under Bifröst, Bifröst starts Sleipnir and nobody types them.

- `sleipnir`: start the TUI; start the daemon first if it isn't running (detached), then connect.
- `sleipnir daemon`: run the service in the foreground (what systemd/launchd/containers call).
- `sleipnir service install | uninstall | status`: register automatic start for the current OS user.
- Later: `sleipnir add <url>` etc. (CLI mode, roadmap "Later").

## 5. Running as a service [owner]

This section is Sleipnir alone. Under Bifröst there is no Sleipnir service: Bifröst is the system service and Sleipnir is its child process (Bifröst design §8.4).

| OS | Mechanism | Built now? |
|---|---|---|
| Linux | systemd **user** unit; docs explain `loginctl enable-linger` so it starts at boot without login | **yes** |
| Windows | Task Scheduler task "at logon", hidden | designed for, later [my call] |
| macOS | launchd LaunchAgent | designed for, later [my call] |

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

### 6.1 Alone

| Listener | Address | Protocol | Default |
|---|---|---|---|
| Loopback | `127.0.0.1` and `[::1]` | HTTP | always on, port **7766** (next free port if taken, recorded in `daemon.json`) |

That is the only listener. There is no LAN listener, no certificate and no discovery in Sleipnir.

### 6.2 Under Bifröst

Sleipnir opens no network port. It listens on the private socket Bifröst creates for it. Bifröst
owns the LAN listener, the home key and its fingerprint, pinning, and mDNS discovery
(Bifröst design §6). The service type `_dnm._tcp` and the port 7767 are gone.

### 6.3 How Sleipnir knows its mode

It decides at start-up, from how it was started [left to the builder: an argument or an environment
variable set by Bifröst]. It never runs both front doors at once.

### 6.4 Browsers

A browser is used on the same machine as Sleipnir, over loopback (alone) or through Bifröst on the
host. A browser on another device is not a Sleipnir client [owner: S8]. Browser extensions talk to
loopback on their own machine. How a laptop's browser extension reaches the home is an open question
for Bifröst (a desktop client), not for Sleipnir.

## 7. Who is calling, and what they may do

### 7.1 Model

| | Alone | Under Bifröst |
|---|---|---|
| People | None. Whoever holds the local token is "the person at this machine" | Bifröst's people |
| Devices | None | Bifröst's devices |
| Pairing | None | Bifröst's (design §7). The `dnm://` link is gone |
| Proof of identity | A random 256-bit local token, in the runtime folder, `0600` | Headers Bifröst adds: person, role, device, device kind. Trusted because only Bifröst can reach the socket |
| Owner of a download | None | A Bifröst person id |

- Sleipnir keeps **no** users, roles, devices, invitations or audit log of its own [owner: S6].
- `DownloadTask.OwnerId` stays in `Sleipnir.Core`. Alone it is empty. Under Bifröst it holds the
  person id. Downloads made alone belong to the admin if the same data is later run under Bifröst.
- The local token rules stay: SHA-256 at rest is not needed for a file the owner can read, but the
  token is never logged, never in a URL, and wrong guesses are throttled.

### 7.2 What moved to Bifröst

Built on 2026-11-13 in `Sleipnir.Server` and now Bifröst's: `IdentityService`, the users and devices
tables, per-device tokens, `GET /me`, the user and device endpoints, and "keep at least one admin".
They are extracted as step 1 of Bifröst's order of work. Sleipnir alone must keep working at every
step of that extraction.

Designed here on 2026-11-13 and now designed in Bifröst: invitations, QR pairing, the 6-digit code,
pending join requests, the home key and its rotation, the audit log.

### 7.3 Where files go [owner: S11]

- **Alone:** one folder, `~/Downloads` by default, from the settings file.
- **Under Bifröst:** Bifröst decides. A client says "private" or "public". Sleipnir asks Bifröst to
  resolve that for the calling person and writes **only** to the path it gets back:
  `<home root>/<person>/private/` or `<home root>/<person>/public/`. Sleipnir never takes a path
  from a client. With no choice given, it is private.
- The path-safety rule is Bifröst's (design §8.9). `DestinationPolicy` moves there with the folders.
- Partial `.dnm` files stay beside their final place, on the same disk, so finishing is a rename.
- Under an NTFS home root, file names follow Windows' rules. `FileNameResolver` already removes
  path separators; it must also refuse `< > : " | ? *` and a trailing dot or space there.
- If Bifröst reports the home root as unavailable (the disk is not mounted), downloads wait.
- **Fairness:** one global FIFO queue (`MaxConcurrentDownloads`, as today). Per-person fairness is a later setting.
- When Bifröst removes a person, Sleipnir cancels their running downloads and removes their records. Files stay on disk.

### 7.4 Pending downloads [owner: S12]

A download can be created in a `Pending` state that does not start until it is approved.

- **Alone:** not used.
- **Under Bifröst, without Norns:** Sleipnir asks Bifröst's "ask the admin" facility for a plain
  approve or reject (design §5.1). The first answer wins.
- **Under Bifröst, with Norns:** Sleipnir gives the request to Norns, which applies its rules and
  history and reaches the admin the same way.
- Which downloads need approval is a setting the admin changes on the host [left to the builder:
  by person, by size, or always].
- An expired question leaves the download pending and tells the requester.

### 7.5 Authorization rules

Under Bifröst. Alone, the one caller may do everything.

| Action | member | admin |
|---|---|---|
| List/see/act on downloads | own only | all (filter by person) |
| Add a download | yes, to own private or public folder | yes, to any folder Bifröst resolves |
| Download from private/loopback/link-local addresses | **no** (SSRF guard, §9) | yes |
| Approve or reject a pending download | no | yes, through Bifröst |
| Remove own finished downloads | yes | all |
| A device of kind `browser-fallback` | view own list only | view only |

A download that is not the caller's is reported as missing (404), so ids cannot be probed.

## 8. API (v1)

### 8.1 Conventions [owner, style; details my call]

- Base path `/api/v1`. JSON, camelCase, ISO-8601 UTC timestamps, string enums.
- Errors use **RFC 9457** `application/problem+json`, with a stable `code` field (e.g. `download_not_found`).
- **Compatibility:** within v1, changes are **additive only**. Clients must ignore unknown fields
  and event types. Breaking changes mean `/api/v2`, with v1 kept for a deprecation period.
- `GET /api/v1/server` (unauthenticated): name, version, API versions, mode. Nothing else.
- Under Bifröst, clients reach the same API at Bifröst's `/x/sleipnir/api/v1/…`.
- **OpenAPI** document served at `/api/v1/openapi.json` and checked into the repo. Builds fail if they drift.
- `POST /downloads` accepts an `Idempotency-Key` header so a phone retrying after a dropped
  connection doesn't add the same download twice.
- Lists are paginated (cursor-based).

### 8.2 Resources (outline; exact shapes left to the builder within these rules)

| Endpoint | Purpose |
|---|---|
| `GET /server` | public info (above) |
| `POST /server/shutdown` | admin: graceful stop |
| `GET/POST /downloads` | list (own; admin: all or `?person=`), add one or several URLs, with `private` or `public` under Bifröst |
| `GET /downloads/{id}` | one download, including segments |
| `POST /downloads/{id}/pause` · `/resume` · `/cancel` | control |
| `DELETE /downloads/{id}` | remove a finished download from the list (file kept) |
| `POST /events/ticket` | alone: get a single-use ticket for the WebSocket (below) |
| `GET /events` | alone: WebSocket |

Users, devices, pairing, the audit log and the LAN setting are Bifröst's endpoints now.

### 8.3 Live events [owner]

**Alone,** Sleipnir serves its own WebSocket, as below. **Under Bifröst,** Sleipnir publishes the same events to Bifröst, which filters them by person and carries them to client apps and to other extensions (design §8.7). Types gain the prefix `sleipnir.`, for example `sleipnir.download.finished`, which carries the folder id, the subfolder and the file's path inside it.

- **Authentication:** browsers can't set headers on a WebSocket. So the client first calls
  `POST /events/ticket` with its token and receives a **single-use, 30-second ticket**, then
  connects to `/events?ticket=…` [my call].
- **Envelope:** `{ "v": 1, "seq": 123, "type": "download.progress", "at": "…", "data": { … } }`.
- **Types (initial):** `download.added`, `download.changed` (status, error), `download.progress`
  (bytes, speed; **throttled** to at most a few per second per client and batched), `download.removed`,
  `download.finished`, `server.shuttingDown`. Pairing and device events are Bifröst's.
- Each client only receives events for downloads it may see (§7.6).
- **Resync:** after reconnecting, a client fetches a snapshot (`GET /downloads`), then applies
  events with higher `seq`. No event replay is promised.
- Heartbeat ping/pong; idle connections closed; per-user connection limit.

## 9. Security [owner: "keep the edge cases in mind"]

Threat-model checklist. Each item needs a test.

| Threat | Mitigation |
|---|---|
| A website drives the loopback API (CSRF) | Bearer token in a header only, never cookies; strict CORS allowlist (none by default; extension and web-UI origins added when those clients exist) |
| DNS rebinding against loopback | Alone: reject requests whose `Host` isn't `localhost`, `127.0.0.1` or `[::1]`. Under Bifröst there is no TCP listener to rebind to |
| Another LAN device uses the API | Sleipnir has no LAN listener in either mode. Under Bifröst, Bifröst authenticates every device (its design §11) |
| Leaked local token (alone) | The token is refreshed at each start. It lives in a `0600` file |
| A client forges identity headers (under Bifröst) | Sleipnir reads them only from Bifröst's private socket. Bifröst strips and rewrites them |
| SSRF: a member makes the server fetch internal hosts (router admin page, other services) | For non-admins, reject destinations whose **resolved** address is loopback, private (RFC 1918 / ULA), link-local, multicast or unspecified. Enforced **at connect time** in the existing `ConnectCallback`, so it covers redirects and DNS rebinding too |
| Destination folder escape (`../`, symlinks) | Under Bifröst, Sleipnir writes only to a path Bifröst resolved, and never takes one from a client. Alone, one folder from the settings file. File names sanitized by `FileNameResolver` |
| Other OS users on the same machine | Per-user daemon; secrets `0600`/user-only ACL; loopback port alone grants nothing without a token |
| Brute force against tokens | 256-bit tokens; global and per-source rate limiting on auth failures |
| Resource exhaustion | Request size limits, WebSocket connection limits, existing download queue limits |
| Secrets in logs | Tokens, invitation secrets and tickets are never logged; logs live in the state directory |

The audit log is Bifröst's. Sleipnir logs repeated failures of the local token to its own log file.

## 10. Persistence [my call]

- **SQLite** in the data directory, via **EF Core** (SQLite provider, snake_case naming) with
  migrations [owner], for download records (a SQLite implementation of the engine's
  `IDownloadStateStore`, keeping the *PersistedBytes-only* crash-safety rule). Users, devices,
  invitations and the audit log are not Sleipnir's data any more.
- **Status (step 3a, 2026-11-13):** only users and devices are in SQLite so far, and those tables leave with the extraction to Bifröst; download records stay in the JSON
  store, which now also saves each download's `OwnerId`. Moving them into SQLite is a later step.
- On first start, existing JSON download records are imported (owner = admin), and the JSON folder
  is left untouched as a backup.
- SQLite also runs on Android, so the Bifröst client app can reuse the same store when it runs the engine.
- Under Bifröst, Sleipnir's data folder is the one Bifröst gives it, on the system disk. Only people's files go under the home root.

## 11. Phase 3: the TUI as a client

- `RemoteDownloadService : IDownloadService` in `Sleipnir.Client` (REST for commands, WebSocket for
  state), so `TuiController` and `TuiRenderer` keep working with minimal change.
- `dnm` starts the daemon if needed, waits for `daemon.json`, connects with the local admin token.
- Closing the TUI leaves downloads running. The TUI shows "reconnecting…" if the daemon restarts.
- The TUI is the client of Sleipnir alone. It has no screens for people, devices or pairing: those
  are Bifröst's, on the host and in its client app. Same look as today.
- There is no in-process mode any more: one code path [my call].

## 11a. Keeping the app working during Phase 2 [my call]

Phase 2 is delivered in steps that each leave `dnm` usable. During Phase 2 the existing TUI keeps
running the engine in-process as today, while `dnm daemon` is added alongside it. Phase 3 switches
the TUI to the daemon and removes the in-process path. The library split (§4.1) comes first, as a
pure refactor with all existing tests green.

## 12. Testing strategy

- Engine: the existing 113 tests keep passing unchanged (moved projects only).
- Server: in-memory API tests (`WebApplicationFactory`) for every endpoint and authorization rule.
- Security: a test per row in §9 (Host header, CORS, local token, forged identity headers, SSRF incl.
  redirect and rebinding, path escape). Pairing and pinning tests are Bifröst's.
- Both front doors: every endpoint and authorization rule is tested alone and under a fake Bifröst.
- WebSocket: tickets, filtering by user, throttling, resync.
- Contract: OpenAPI document generated and diffed in tests.
- End to end: a real daemon process with the TUI's client against loopback HTTP.

## 13. Left to the builder

- Exact JSON shapes, endpoint names within §8's outline, SQL schema, migration tooling.
- How Sleipnir detects its mode, and how the pending rule is configured.
- Logging library and format; port fallback details; QR rendering in the terminal.
- Order of work within Phase 2, as long as each step keeps the app working and the tests green.

## 14. Calls made by the designer (for the owner to check)

ASP.NET Core accepted (+20–30 MB) · consistency rules from Heimdall (above) · port 7766 (loopback) ·
WebSocket single-use tickets · ownership on the engine's task, one global queue · removing a person keeps
files · in-process TUI kept until Phase 3 · JSON state imported, old files kept · Windows/macOS service
installers later · no in-process mode after Phase 3 · non-admin SSRF guard · private when no choice is given.

Moved to Bifröst's design with the same values: ECDSA P-256 identity and SPKI fingerprint · opaque
random tokens, stored hashed · invitations expire after 10 minutes, join requests after 5 minutes, at
most 5 pending · the audit log.

## 15. What this revision does to the plans

- `plans/2026-11-13-library-split.md` and `plans/2026-11-13-daemon-skeleton.md`: unchanged. Both are Sleipnir alone.
- `plans/2026-11-13-identity-and-access.md` (step 3a, built): its users, devices and tokens become
  Bifröst's. Do not build further on them inside Sleipnir. Ownership (`OwnerId`) and the folder check stay useful.
- **Next for Sleipnir:** the second front door (Bifröst's socket and identity headers), the pending
  state, `sleipnir.download.finished` with the folder, and the rename of the command and data folder.
- **Not next for Sleipnir:** the LAN listener, the home key, pairing and discovery. Those are steps 1 to 3 of Bifröst's order of work.
