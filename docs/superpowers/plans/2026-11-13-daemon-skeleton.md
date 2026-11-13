# Daemon and Server Skeleton Implementation Plan (Phase 2, step 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `dnm daemon` runs the download engine as a single-instance background process with a token-protected loopback REST API (server info, shutdown, download control), and `dnm service install` registers it as a systemd user service.

**Architecture:** New library project `Sleipnir.Server` (ASP.NET Core minimal APIs on Kestrel, `FrameworkReference Microsoft.AspNetCore.App`) wraps an `IDownloadService`. The `dnm` app gains `daemon` and `service` commands. The TUI keeps running the engine in-process (spec §11a) but takes the same instance lock, so the daemon and the TUI can never drive the same state folder at once.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, xUnit (real Kestrel on loopback port 0, no extra test packages).

**Spec:** `docs/superpowers/specs/2026-11-13-sleipnir-service-design.md` (§4.2, §5, §6.1, §8.1–8.2, §9, §11a).

**Out of scope (later steps):** users, devices, pairing (step 3), LAN HTTPS, pinning, mDNS (step 4), WebSocket, OpenAPI, SQLite (step 5/3), per-user folders and the SSRF guard (need users).

## Global Constraints

- Run dotnet via the container script: `DN` = `/tmp/claude-1000/-home-silverwolf-Projects-Asgard-Sleipnir/9f621007-23c9-4d15-8998-69438baff021/scratchpad/dn` (wraps `podman run … sdk:10.0 dotnet "$@"`). Suite command: `DN test Sleipnir.sln`. Baseline: 116 passing.
- Namespaces `Sleipnir.*`. Server code in `Sleipnir.Server.*`; DTOs in `Sleipnir.Core.Api`. `Sleipnir.Core` and `Sleipnir.Engine` must not gain ASP.NET references (`ArchitectureTests` enforces it).
- Base path `/api/v1`; JSON camelCase with string enums; ISO-8601 UTC timestamps; errors are RFC 9457 `application/problem+json` with a stable `code` field (spec §8.1).
- Only `GET /api/v1/server` is unauthenticated. Everything else needs `Authorization: Bearer <token>`; tokens are 256-bit random, compared in constant time, never logged, never accepted from cookies or query strings (§9).
- Loopback only: `127.0.0.1` (and `[::1]` when available), default port **7766**, next free port if taken (up to 20 tries), actual port recorded in `daemon.json` (§6.1). Requests whose `Host` isn't `localhost`, `127.0.0.1` or `[::1]` (any port) are rejected. No CORS headers are ever sent (§9).
- Secret files are created `0600` atomically, parent dir `0700`, before content is written (§5).
- Directories per spec §5 table (`dnm` folder names unchanged). Daemon files in the runtime dir: `daemon.lock`, `daemon.json` (port, pid, version), `admin.token`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Stage by explicit path; never stage `docs/ideas/suite-vision.md`. Work on branch `phase2-daemon`, merge to `main` only when told.

## Review Focus

1. A website or other local process calling the loopback API: wrong Host (DNS rebinding), missing/wrong/short token, `Origin` header present: tests in Task 4.
2. Two daemons, or the TUI and a daemon, at once, including a stale lock after a crash (lock must be released by the OS, not by a leftover file): Tasks 3 and 6.
3. Huge or hostile request bodies, thousands of URLs, malformed URLs and `file:`/`ftp:` schemes on `POST /downloads`: Task 5.
4. A phone retrying `POST /downloads` after a dropped connection must not add the download twice: Task 5.
5. SIGTERM while downloads run must save progress and resume next start (existing `ShutdownAsync` semantics): Task 6.

---

### Task 1: `Sleipnir.Server` project and `AppPaths`

**Files:**
- Create: `src/Sleipnir.Server/Sleipnir.Server.csproj` (library, `FrameworkReference Include="Microsoft.AspNetCore.App"`, references Core and Engine, `RootNamespace Sleipnir`), `src/Sleipnir.Server/Hosting/AppPaths.cs`, `tests/Sleipnir.Tests/Server/AppPathsTests.cs`
- Modify: `Sleipnir.sln` (add under folder `src`), `tests/Sleipnir.Tests/Sleipnir.Tests.csproj` (reference Server), `Dockerfile` (copy the new csproj in the restore layer)

**Interfaces:**
- Produces: `namespace Sleipnir.Server.Hosting`; `enum PlatformKind { Linux, Windows, MacOS }`; `sealed record AppPaths(string Settings, string Data, string Runtime)` with `static AppPaths Resolve(PlatformKind platform, Func<string, string?> env, string home)` and `static AppPaths ForCurrentUser()`.

- [ ] **Step 1: Write failing tests** (`AppPathsTests`), each asserting full paths for `home = "/home/u"`:
  - Linux with empty env → Settings `/home/u/.config/dnm`, Data `/home/u/.local/share/dnm`, Runtime `/home/u/.local/state/dnm`.
  - Linux with `XDG_CONFIG_HOME=/c`, `XDG_DATA_HOME=/d`, `XDG_RUNTIME_DIR=/r` → `/c/dnm`, `/d/dnm`, `/r/dnm`.
  - Linux with `XDG_STATE_HOME=/s` and no runtime dir → Runtime `/s/dnm`.
  - Windows (`APPDATA=C:\A`, `LOCALAPPDATA=C:\L`) → `C:\A\dnm`, `C:\L\dnm`, `C:\L\dnm\run`.
  - macOS → Settings and Data `/home/u/Library/Application Support/dnm`, Runtime the same plus `/run`.
  - Linux Data joined with `downloads` equals `JsonDownloadStateStore.DefaultDirectory()` (so the daemon finds today's saved downloads).
- [ ] **Step 2: Run** `DN test Sleipnir.sln --filter AppPathsTests`. Expected: compile failure (type missing).
- [ ] **Step 3: Create the project, add to solution/tests/Dockerfile, implement `AppPaths`.** Treat empty env values as unset. Windows paths use `\` joins regardless of host OS (build strings, don't call `Path.Combine` for Windows).
- [ ] **Step 4: Run the suite.** Expected: all pass (116 + new).
- [ ] **Step 5: Commit** `feat(server): add Sleipnir.Server project and per-OS app paths`.

---

### Task 2: Secret files and the local admin token

**Files:**
- Create: `src/Sleipnir.Server/Security/SecretFile.cs`, `src/Sleipnir.Server/Security/LocalAdminToken.cs`, `tests/Sleipnir.Tests/Server/SecretTests.cs`

**Interfaces:**
- Produces: `static class SecretFile { static void CreateNew(string path, string content) }` (throws `IOException` if the file exists; creates parent directories `0700`; file mode `0600` set at creation via `FileStreamOptions.UnixCreateMode`; on Windows uses default user-only profile permissions).
- Produces: `static class LocalAdminToken { static string LoadOrCreate(string runtimeDir); static bool Matches(string expected, string? presented) }` (token file `admin.token`; 43-char base64url of 32 random bytes; `Matches` uses `CryptographicOperations.FixedTimeEquals`, false for null/empty/different length).

- [ ] **Step 1: Failing tests** (skip the mode assertions on Windows): `CreateNew` yields mode `UserRead|UserWrite` exactly and parent `UserRead|UserWrite|UserExecute`; a second `CreateNew` on the same path throws `IOException` and leaves content intact; `LoadOrCreate` twice returns the same 43-char token; tokens from two fresh dirs differ; `Matches` true for equal, false for `null`, `""`, a prefix, and a different token of equal length.
- [ ] **Step 2: Run** `--filter SecretTests`. Expected: compile failure.
- [ ] **Step 3: Implement** both classes. `LoadOrCreate` reads an existing file (trimmed); if absent, creates via `SecretFile.CreateNew`; if two processes race and `CreateNew` throws, re-read.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): secret files and local admin token`.

---

### Task 3: Single-instance lock and `daemon.json`

**Files:**
- Create: `src/Sleipnir.Server/Hosting/InstanceLock.cs`, `src/Sleipnir.Server/Hosting/DaemonInfo.cs`, `tests/Sleipnir.Tests/Server/InstanceTests.cs`

**Interfaces:**
- Produces: `sealed class InstanceLock : IDisposable { static InstanceLock? TryAcquire(string runtimeDir) }` — exclusive `FileStream` on `daemon.lock` (`FileShare.None`), so the OS frees it if the process dies; returns `null` when held.
- Produces: `sealed record DaemonInfo(int Port, int Pid, string Version)` with `static void Write(string runtimeDir, DaemonInfo info)` (write temp file then atomic rename, mode `0600`), `static DaemonInfo? TryRead(string runtimeDir)` (null if missing or corrupt), `static void Delete(string runtimeDir)`.

- [ ] **Step 1: Failing tests:** `TryAcquire` returns non-null, a second `TryAcquire` on the same dir returns `null`, after `Dispose` a third succeeds; a lock file left on disk by a "crashed" holder (create the file, don't hold it) does not block `TryAcquire`; `DaemonInfo` round-trips; `TryRead` returns `null` for missing file and for garbage content; `Write` overwrites an existing file; `Delete` on a missing file doesn't throw.
- [ ] **Step 2: Run** `--filter InstanceTests`. Expected: compile failure.
- [ ] **Step 3: Implement.** Create the runtime dir (`0700`) if missing.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): single-instance lock and daemon.json`.

---

### Task 4: `DaemonHost`: loopback listener, Host check, bearer auth, server info and shutdown

**Files:**
- Create: `src/Sleipnir.Server/Hosting/DaemonHost.cs`, `src/Sleipnir.Server/Hosting/DaemonOptions.cs`, `src/Sleipnir.Server/Api/Problem.cs` (helper producing `application/problem+json` with `code`), `src/Sleipnir.Server/Api/ServerEndpoints.cs`, `src/Sleipnir.Server/Security/AuthFailureLimiter.cs`, `src/Sleipnir.Core/Api/ServerInfoDto.cs`, `tests/Sleipnir.Tests/Server/DaemonHostTests.cs`, `tests/Sleipnir.Tests/Server/DaemonFixture.cs`

**Interfaces:**
- Consumes: `IDownloadService` (Core).
- Produces: `sealed record DaemonOptions(string AdminToken, int Port = 7766)` (`Port = 0` means ephemeral; no fallback then).
- Produces: `sealed class DaemonHost : IAsyncDisposable { static DaemonHost Create(DaemonOptions options, IDownloadService downloads); Task StartAsync(CancellationToken ct = default); int Port { get; } Task WaitForShutdownAsync(CancellationToken ct = default); Task StopAsync(); }`. `StartAsync` binds `127.0.0.1` and, best effort, `[::1]` on the same port, trying up to 20 following ports if the default is taken. `WaitForShutdownAsync` completes when `POST /api/v1/server/shutdown` is accepted or `StopAsync` is called.
- Produces: `record ServerInfoDto(string Name, string Version, string[] ApiVersions)` with `Name = "Sleipnir"`, `ApiVersions = ["v1"]` (fingerprint added with LAN HTTPS).
- Produces (test): `DaemonFixture` starting a `DaemonHost` on port 0 over a `StubDownloadService`, exposing `HttpClient Http` (base address `http://127.0.0.1:<port>`), `string Token`.
- Produces: `static class Problem { static IResult Create(int status, string code, string title) }`; error codes used here: `unauthorized` (401, with `WWW-Authenticate: Bearer`), `host_not_allowed` (400), `too_many_attempts` (429).

- [ ] **Step 1: Failing tests** (`DaemonHostTests`, all through real HTTP):
  - `GET /api/v1/server` without a token → 200 JSON with `name`=`Sleipnir`, `apiVersions`=`["v1"]`, and no other fields.
  - `POST /api/v1/server/shutdown` without a token → 401 `application/problem+json` with `code`=`unauthorized`; with a wrong token → 401; with a same-length wrong token → 401; with `Authorization: Basic …` → 401.
  - A token passed as `?access_token=` or in a cookie is not accepted (401).
  - Valid token on shutdown → 202, and `WaitForShutdownAsync` completes within 2 s.
  - `Host: evil.example` → 400 with `code`=`host_not_allowed`, even with a valid token and even for `GET /server`; `Host: localhost:1234`, `127.0.0.1`, `[::1]` are accepted.
  - A request with `Origin: http://evil.example` gets no `Access-Control-Allow-Origin` header; an `OPTIONS` preflight is not answered with CORS headers.
  - After 20 failed auth attempts from one source within a minute the 21st gets 429 `too_many_attempts` (limiter takes a clock `Func<DateTimeOffset>` so the test needn't wait); a valid token is still rejected while blocked.
  - Unknown path `/api/v1/nope` with valid token → 404 problem+json `code`=`not_found`.
  - Starting two hosts with `Port = 7766`-style fixed ports where the first holds it: use a pre-bound `TcpListener` on a free port P, start a host with `Port = P` → host binds a later port and `Port != P`.
- [ ] **Step 2: Run** `--filter DaemonHostTests`. Expected: compile failure.
- [ ] **Step 3: Implement.** Build a `WebApplication` with Kestrel (`Listen` explicitly, no config files, `Limits.MaxRequestBodySize = 1 MB`, server header off). Middleware order: Host check → auth-failure limiter → bearer auth (everything except `GET /api/v1/server`) → endpoints. JSON options: camelCase, `JsonStringEnumConverter`. Log lines never contain the token.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): token-protected loopback API with host check and shutdown`.

---

### Task 5: Download endpoints

**Files:**
- Create: `src/Sleipnir.Core/Api/DownloadDto.cs`, `src/Sleipnir.Server/Api/DownloadEndpoints.cs`, `src/Sleipnir.Server/Api/IdempotencyCache.cs`, `tests/Sleipnir.Tests/Server/DownloadEndpointTests.cs`
- Modify: `src/Sleipnir.Server/Hosting/DaemonHost.cs` (register endpoints, `DaemonOptions` gains `string DefaultDestination`, default `~/Downloads`)

**Interfaces:**
- Consumes: `DaemonFixture` (Task 4), `StubDownloadService` (tests; extend its recorded `Calls` if needed without breaking existing TUI tests).
- Produces: `record DownloadDto(Guid Id, string Url, string Filename, string DestinationPath, DownloadStatus Status, long TotalBytes, bool IsSizeKnown, long BytesDownloaded, string? ErrorMessage, DateTime CreatedAt, SegmentDto[]? Segments)` (segments only on single-download GET), `record SegmentDto(int Index, long StartOffset, long EndOffset, long BytesDownloaded)`, `record AddDownloadsRequest(string[] Urls, string? Destination)`, `record PageDto<T>(T[] Items, string? NextCursor)`.
- Endpoints (all authenticated, `/api/v1`): `GET /downloads?limit=&cursor=` (default limit 50, max 200, ordered by `CreatedAt` then `Id`, opaque cursor → `PageDto`); `POST /downloads` (201 with `PageDto`, header `Idempotency-Key` supported); `GET /downloads/{id}`; `POST /downloads/{id}/pause|resume|cancel` (200 with the updated `DownloadDto`); `DELETE /downloads/{id}` (204).
- Error codes: `download_not_found` (404), `invalid_url` (400: not an absolute `http`/`https` URL, or empty list, or more than 100 URLs; nothing is added when any entry is invalid), `invalid_cursor` (400), `invalid_request` (400, malformed JSON).

- [ ] **Step 1: Failing tests:** list is empty `{items:[],nextCursor:null}`; three seeded downloads with `limit=2` return 2 items and a cursor, the second page returns the third and `nextCursor=null`; `limit=0` and `limit=1000` are clamped (1 and 200) [my call]; garbage cursor → 400 `invalid_cursor`; POST with two valid URLs → 201, two items, stub got two `AddDownloadAsync` calls with `DefaultDestination`; `destination` is passed through when given; POST with `["http://a/x","ftp://a/y"]` → 400 `invalid_url` and zero adds; `["file:///etc/passwd"]` and `["not a url"]` → 400; 101 URLs → 400; empty body / invalid JSON → 400 `invalid_request`; a 2 MB body → 413; two POSTs with the same `Idempotency-Key` and body → same response, one add; same key, different body → 422 `idempotency_key_reused`; GET unknown id → 404 `download_not_found`; GET with segments lists them; pause/resume/cancel call the service and return the new state; DELETE of unknown id → 404; every endpoint without a token → 401.
- [ ] **Step 2: Run** `--filter DownloadEndpointTests`. Expected: FAIL (404s / compile errors).
- [ ] **Step 3: Implement.** `IdempotencyCache`: in-memory, key + SHA-256 of the body, entries expire after 1 hour, at most 1000 entries (evict oldest). Map `DownloadTask` → `DownloadDto` in one place.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): download endpoints with paging and idempotency`.

---

### Task 6: `dnm daemon`, and the TUI takes the same lock

**Files:**
- Create: `src/Sleipnir.App/DaemonCommand.cs`, `tests/Sleipnir.Tests/Server/DaemonProcessTests.cs`
- Modify: `src/Sleipnir.App/Program.cs` (dispatch), `src/Sleipnir.App/Sleipnir.App.csproj` (reference Server)

**Interfaces:**
- Consumes: `AppPaths.ForCurrentUser()`, `InstanceLock`, `DaemonInfo`, `LocalAdminToken`, `DaemonHost`, `DownloadService` etc. (existing composition from `Program.cs`).
- Produces: `static class DaemonCommand { static Task<int> RunAsync(CancellationToken stop) }` returning exit code 0 on clean stop, 1 when another instance holds the lock (message on stderr: `Sleipnir is already running (pid N, port P).` using `daemon.json` when readable).
- Produces: in `Program.cs`, `dnm daemon` calls `DaemonCommand`; plain `dnm` (TUI) first calls `InstanceLock.TryAcquire` and exits 1 with `Sleipnir daemon is running (port P). Stop it first; the TUI will connect to it in a later release.` when the lock is held.

- [ ] **Step 1: Failing tests** (`DaemonProcessTests`; launch `dotnet <test-output>/dnm.dll daemon` as child processes with `HOME`, `XDG_RUNTIME_DIR`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME` pointing at a fresh temp dir and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`; a helper waits up to 15 s for `daemon.json`):
  - daemon starts, `daemon.json` has the child's pid and a port; `GET /api/v1/server` → 200; `admin.token` exists with mode `0600`; `GET /api/v1/downloads` with that token → 200 empty page.
  - a second `dnm daemon` with the same env exits with code 1 and its stderr contains `already running`.
  - plain `dnm` (stdin redirected) with the daemon running exits 1 and prints `daemon is running`.
  - `POST /api/v1/server/shutdown` → child exits 0 within 10 s and `daemon.json` is gone.
  - after the child is killed with SIGKILL, a new daemon starts fine (stale lock) and `daemon.json` is rewritten.
  - SIGTERM (`kill -TERM <pid>`) → exit code 0 within 10 s and `daemon.json` removed.
- [ ] **Step 2: Run** `--filter DaemonProcessTests`. Expected: FAIL (`daemon` is not a command: the TUI starts instead).
- [ ] **Step 3: Implement `DaemonCommand`:** acquire lock → create token → build engine as `Program.cs` does today (same state store, `HttpNetworkClient`, `LocalFileStorage`, `DownloadOptions`) → `RestoreAsync` → start `DaemonHost` → write `daemon.json` → wait for shutdown endpoint, SIGTERM, SIGINT or SIGHUP → stop host → `ShutdownAsync` → delete `daemon.json` → dispose lock. Move the shared engine composition into one helper used by both TUI and daemon.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat: add dnm daemon and make the TUI respect the instance lock`.

---

### Task 7: `dnm service install | uninstall | status` (systemd user unit)

**Files:**
- Create: `src/Sleipnir.App/Service/SystemdUnit.cs`, `src/Sleipnir.App/Service/ServiceCommand.cs`, `src/Sleipnir.App/Service/ICommandRunner.cs`, `tests/Sleipnir.Tests/ServiceCommandTests.cs`
- Modify: `src/Sleipnir.App/Program.cs` (dispatch `service`)

**Interfaces:**
- Produces: `static class SystemdUnit { static string Render(string executablePath) }` returning exactly the unit text below with the path quoted; `interface ICommandRunner { Task<(int ExitCode, string Output)> RunAsync(string file, params string[] args) }`; `sealed class ServiceCommand(ICommandRunner runner, string configHome, string executablePath, TextWriter output) { Task<int> RunAsync(string[] args) }`.

```
[Unit]
Description=Sleipnir download manager

[Service]
ExecStart="<path>" daemon
Restart=on-failure
RestartSec=5

[Install]
WantedBy=default.target
```

- [ ] **Step 1: Failing tests** (fake runner records calls): `Render("/opt/dnm")` equals the text above; `install` writes `<configHome>/systemd/user/sleipnir.service` with that text, then runs `systemctl --user daemon-reload` and `systemctl --user enable --now sleipnir.service` in that order and prints a line mentioning `loginctl enable-linger`; `install` when `systemctl` exits non-zero returns 1 and prints the runner output; `uninstall` runs `disable --now`, deletes the file, runs `daemon-reload`, and returns 0 even if the file is missing; `status` returns the exit code of `systemctl --user status sleipnir.service` and prints its output; unknown subcommand or none → exit code 2 with a usage line; path containing a space and a `"` is rejected for install (returns 1) [my call: refuse rather than escape].
- [ ] **Step 2: Run** `--filter ServiceCommandTests`. Expected: compile failure.
- [ ] **Step 3: Implement.** The real runner starts the process with redirected output. On non-Linux, print `Service install is only available on Linux for now.` and return 1.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat: add dnm service install/uninstall/status for systemd`.

---

### Task 8: Docs and a real-world check

**Files:**
- Modify: `docs/ARCHITECTURE.md` (add the Server project and the daemon diagram), `docs/DEVELOPMENT.md` (layout + tests table), `docs/USER_GUIDE.md` (new "Running as a service" section: `dnm daemon`, `dnm service install`, `loginctl enable-linger`, where `admin.token` and `daemon.json` live, a `curl` example with the token), `README.md` (one line), `CHANGELOG.md` (Added), `docs/FEATURE_COMPARISON.md` (Remote control API row: local API exists, Phase 2)

- [ ] **Step 1: Write the docs** (keep to facts verified by the tests above).
- [ ] **Step 2: Real check in a container** with a throwaway HOME: publish the app, start `dnm daemon` in the background, `curl` `/api/v1/server`, add a real local file URL served by `python3 -m http.server`, poll `GET /api/v1/downloads` until `completed`, compare checksums, then `kill -TERM` and confirm exit 0. Expected: checksums equal, status `completed`.
- [ ] **Step 3: Final run:** `DN test Sleipnir.sln` all green; `podman build .` succeeds; `git status` shows only `docs/ideas/suite-vision.md`.
- [ ] **Step 4: Commit** `docs: document the daemon and service commands`.
