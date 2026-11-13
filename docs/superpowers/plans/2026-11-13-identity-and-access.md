# Identity and Access Implementation Plan (Phase 2, step 3a)

> **Superseded in part, 2026-11-14.** Users, roles, devices and tokens now belong to Bifröst (suite decisions S4 and S6). What this plan built is extracted into Bifröst, not extended here. See the spec's revision note and its §15.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single admin token with users, roles and per-device tokens stored in SQLite, and make every download endpoint obey the spec's authorization rules (own downloads only for members, per-user folders).

**Architecture:** `Sleipnir.Server` gains a SQLite store (EF Core, snake_case, migrations) holding users and devices, and an `IdentityService` that authenticates a presented bearer token by its SHA-256 hash. The existing `admin.token` becomes the token of a built-in "This computer" admin device. Ownership (`OwnerId`) lives on the engine's `DownloadTask` and is persisted in the existing JSON records (additive field).

**Tech Stack:** .NET 10, EF Core 10 SQLite (`Microsoft.EntityFrameworkCore.Sqlite` 10.0.15), `EFCore.NamingConventions` 10.0.1, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-13-sleipnir-service-design.md` (§7.1, §7.2, §7.5, §7.6, §8.2, §9, §10). Previous plans: `2026-11-13-daemon-skeleton.md`.

**Out of scope (plan 3b):** server identity key/certificate, invitations, join requests and the short code, audit log, rate limits for pairing, the SSRF guard. **Out of scope (later):** moving download records from JSON to SQLite (the JSON store stays; the spec's import is deferred, ruling below), LAN listener.

## Global Constraints

- Run dotnet via `DN` = `/tmp/claude-1000/-home-silverwolf-Projects-Asgard-Sleipnir/9f621007-23c9-4d15-8998-69438baff021/scratchpad/dn` (wraps podman + `sdk:10.0`). Baseline: 215 passing. The suite also passes with `--sysctl net.ipv6.conf.all.disable_ipv6=1`.
- `Sleipnir.Core` and `Sleipnir.Engine` stay free of ASP.NET and EF Core (`ArchitectureTests`); EF Core lives only in `Sleipnir.Server`.
- Tokens: 256-bit random, base64url (43 chars); the server stores only the **SHA-256 hash**; revocation takes effect on the next request; tokens never appear in URLs or logs (§7.2).
- Roles `admin` | `member` (JSON: lower case). More than one admin is allowed; **the last admin can't be demoted or deleted** (§7.1).
- Authorization (§7.6): members see and act on their own downloads only; admins on all (`?user=<id>` filter). A member addressing someone else's download gets **404 `download_not_found`**, never 403 (no existence leak) [my call]. Admin-only endpoints return **403 `forbidden`** to members.
- Folders (§7.5): admin default `~/Downloads`; member default `~/Downloads/<display name>/` (display name made filesystem-safe); the admin can set a per-user allowed root; a member's destination must resolve (symlinks included) inside that root.
- Database file `dnm.db` in the **data** directory, created `0600`; schema through EF migrations applied at start; a database that can't be opened or migrated is a start-up failure (exit code 74, one line).
- Errors stay RFC 9457 problem+json with a stable `code`. Existing behaviour from the daemon plan (Host allowlist, no CORS, failure throttling that never blocks a valid token, 3 s stop) is unchanged.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Stage explicit paths. Branch `phase2-users`; merge to `main` only when told.

## Review Focus

1. A member probing for another user's downloads, devices or users by guessing ids, using `?user=`, or via control verbs and DELETE: always 404/403 with no difference in timing-visible content (Task 6, 5).
2. Destination escapes: `..`, a sibling folder sharing the prefix (`/d/alice-evil` vs root `/d/alice`), a symlink inside the root pointing out, trailing separators, case on case-insensitive disks (Task 6).
3. Revoked device or deleted user with an in-flight or repeated token: rejected on the next request; deleting a user cancels their downloads and keeps files (Tasks 3, 5).
4. Last-admin protection under concurrent demotions/deletions, and display names that are empty, huge, duplicate by case, contain path separators or control characters (Tasks 3, 5, 6).
5. Corrupt, read-only or future-version database at start-up; the local `admin.token` being regenerated while the database remembers the old hash (Tasks 2, 4).

---

### Task 1: Download ownership in Core, Engine and the JSON records

**Files:**
- Modify: `src/Sleipnir.Core/Models/DownloadTask.cs`, `src/Sleipnir.Core/Models/DownloadRecord.cs`, `src/Sleipnir.Core/Interfaces/IDownloadService.cs`, `src/Sleipnir.Engine/Services/DownloadService.cs` (`AddDownloadAsync`, `ToRecord`, `FromRecord`), `tests/Sleipnir.Tests/Tui/StubDownloadService.cs`
- Create: `tests/Sleipnir.Tests/OwnershipTests.cs`

**Interfaces:**
- Produces: `DownloadTask.OwnerId : Guid?` (null = imported/legacy, belongs to the admin); `DownloadRecord` gains trailing `Guid? OwnerId = null`; `IDownloadService.AddDownloadAsync(string url, string outputDirectory, Guid? ownerId = null)`.

- [ ] **Step 1: Failing tests** (`OwnershipTests`, using the existing `FakeNetworkClient`/`TempDir`/`JsonDownloadStateStore` helpers as `PersistenceTests` does): a download added with `ownerId` has `GetDownload(id).OwnerId == ownerId`; after `ShutdownAsync` and a new `DownloadService` over the same store + `RestoreAsync`, the owner survives; a hand-written JSON record **without** an `ownerId` property loads with `OwnerId == null`; a download added without an owner has `OwnerId == null`.
- [ ] **Step 2: Run** `DN test Sleipnir.sln --filter OwnershipTests`. Expected: compile failure (`OwnerId` missing).
- [ ] **Step 3: Implement** the members above; `StubDownloadService.AddDownloadAsync` takes the same optional parameter, stores it, and records calls exactly as before (`add <url> -> <dir>`; owner is not part of the call text).
- [ ] **Step 4: Run the suite.** Expected: 215 + new, all pass.
- [ ] **Step 5: Commit** `feat: record the owner of each download`.

---

### Task 2: SQLite store with EF Core migrations

**Files:**
- Create: `src/Sleipnir.Server/Persistence/SleipnirDb.cs` (context), `src/Sleipnir.Server/Persistence/Entities.cs`, `src/Sleipnir.Server/Persistence/DesignTimeFactory.cs`, `src/Sleipnir.Server/Persistence/Migrations/*` (generated), `tests/Sleipnir.Tests/Server/DatabaseTests.cs`
- Modify: `src/Sleipnir.Server/Sleipnir.Server.csproj` (packages above; `Microsoft.EntityFrameworkCore.Design` with `PrivateAssets=all`), `Dockerfile` unchanged

**Interfaces:**
- Produces (`namespace Sleipnir.Server.Persistence`): `enum UserRole { Admin, Member }`; `class UserEntity { Guid Id; string DisplayName; UserRole Role; DateTime CreatedAt; string? AllowedRoot }`; `class DeviceEntity { Guid Id; Guid UserId; string Name; string Platform; string TokenHash; DateTime CreatedAt; DateTime? LastSeenAt; DateTime? RevokedAt; bool IsLocal }`; `class SleipnirDb(DbContextOptions<SleipnirDb>) : DbContext` with `DbSet<UserEntity> Users`, `DbSet<DeviceEntity> Devices`; `static class Database { static IDbContextFactory<SleipnirDb> OpenFile(string dbPath); static IDbContextFactory<SleipnirDb> OpenMemory(); static Task MigrateAsync(IDbContextFactory<SleipnirDb> f) }`.
- Constraints encoded in the model: `users.display_name` unique case-insensitively (`COLLATE NOCASE` unique index), `devices.token_hash` unique, `Role` and `UserRole` stored as lower-case text, tables/columns snake_case, `devices.user_id` cascade-deletes with the user. `OpenMemory` uses a uniquely named shared-cache in-memory database kept alive for the factory's lifetime.

- [ ] **Step 1: Failing tests:** `MigrateAsync` on a fresh file creates the file with mode `0600` (Unix) and tables `users` and `devices` (query `sqlite_master`); migrating twice is a no-op; inserting two users whose names differ only by case throws `DbUpdateException`; two devices with the same token hash throw; deleting a user removes their devices; a stored admin reads back as `UserRole.Admin` and the raw column text is `admin`; opening a file that is not a database throws on `MigrateAsync`.
- [ ] **Step 2: Run** `--filter DatabaseTests`. Expected: compile failure.
- [ ] **Step 3: Implement.** Generate the migration inside the container: install `dotnet-ef` to a tool path under the scratchpad, run `dotnet ef migrations add Initial --project src/Sleipnir.Server --output-dir Persistence/Migrations`. `OpenFile` creates the file with `SecretFile`-style `0600` before SQLite opens it (create empty file first).
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): SQLite store for users and devices`.

---

### Task 3: `IdentityService`

**Files:**
- Create: `src/Sleipnir.Server/Identity/IdentityService.cs`, `src/Sleipnir.Server/Identity/Principal.cs`, `src/Sleipnir.Server/Identity/UserNames.cs`, `tests/Sleipnir.Tests/Server/IdentityServiceTests.cs`

**Interfaces:**
- Consumes: `Database.OpenMemory()`, entities (Task 2).
- Produces (`namespace Sleipnir.Server.Identity`): `sealed record Principal(Guid UserId, string UserName, UserRole Role, Guid DeviceId, string DeviceName, bool IsLocal)`; `sealed record IssuedDevice(DeviceEntity Device, string Token)`; `sealed class IdentityService(IDbContextFactory<SleipnirDb> db, Func<DateTimeOffset>? clock = null)` with:
  - `Task<Principal> EnsureLocalAdminAsync(string adminName, string localToken)`: creates the first admin named `adminName` if no user exists, and the built-in device `This computer` (`IsLocal = true`, platform = OS description) for the first admin, storing `SHA-256(localToken)`; if the device exists, updates its hash to the given token (the token file may have been regenerated); returns its principal.
  - `Task<Principal?> AuthenticateAsync(string token)`: null for unknown or revoked devices; updates `LastSeenAt` at most once per minute per device.
  - `Task<IssuedDevice> IssueDeviceAsync(Guid userId, string name, string platform)`; `Task<bool> RevokeDeviceAsync(Guid deviceId)`.
  - `Task<UserEntity> CreateUserAsync(string displayName, UserRole role)` throws `IdentityException(code)` with codes `invalid_name`, `name_taken`.
  - `Task<UserEntity> UpdateUserAsync(Guid id, string? displayName, UserRole? role, string? allowedRoot)` codes also `user_not_found`, `last_admin`; `Task<IReadOnlyList<DeviceEntity>> DeleteUserAsync(Guid id)` returns the removed devices, code `last_admin`/`user_not_found`.
  - Lists/lookups: `GetUserAsync(Guid)`, `ListUsersAsync()`, `ListDevicesAsync(Guid? userId)`, `GetDeviceAsync(Guid)`.
  - `static string HashToken(string token)` (lower-case hex SHA-256).
- `UserNames.Validate(string)` → trimmed name or `invalid_name`: 1–40 characters after trimming, no control characters, no `/`, `\`, NUL; `UserNames.ToFolderName(string)` → filesystem-safe folder name (invalid characters replaced by `_`, never `.`/`..`/empty, at most 40 chars).

- [ ] **Step 1: Failing tests:** first `EnsureLocalAdminAsync("alice","tok")` creates one admin and one local device and `AuthenticateAsync("tok")` returns an admin principal with `IsLocal`; calling it again with a different token replaces the hash (old token → null, new → principal) without creating a second user or device; unknown and empty tokens → null; a revoked device → null; `LastSeenAt` is set on first use, unchanged within a minute (fake clock), updated after; stored hash is the SHA-256 hex and the plain token never appears in any column (scan all string columns); `CreateUserAsync` rejects `""`, `"  "`, 41 characters, `a/b`, `a\u0001b` with `invalid_name`, and `"Bob"` then `"bob"` with `name_taken`; names are stored trimmed; demoting or deleting the only admin → `last_admin`; with two admins one can be demoted; **two concurrent demotions of the two admins leave exactly one admin** (run 20 rounds); `DeleteUserAsync` returns and removes the user's devices, whose tokens then authenticate to null; `ToFolderName` maps `"Al/ice"`, `".."`, `"  "`, `"a\u0000b"`, a 100-character name to safe non-empty names without separators.
- [ ] **Step 2: Run** `--filter IdentityServiceTests`. Expected: compile failure.
- [ ] **Step 3: Implement.** Do the admin-count check and the write in one transaction (`BeginTransaction` with immediate locking for SQLite) so concurrent demotions serialize.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): users, devices and token authentication`.

---

### Task 4: Authenticate requests through `IdentityService`; `GET /me`; wire the daemon

**Files:**
- Create: `src/Sleipnir.Server/Api/MeEndpoints.cs`, `src/Sleipnir.Core/Api/MeDto.cs`
- Modify: `src/Sleipnir.Server/Hosting/DaemonHost.cs` (constructor takes `IdentityService`; the `Authenticate` middleware calls `AuthenticateAsync` and stores the `Principal` in `HttpContext.Items`), `src/Sleipnir.Server/Hosting/DaemonOptions.cs` (drop `AdminToken`), `src/Sleipnir.App/DaemonCommand.cs`, `tests/Sleipnir.Tests/Server/DaemonFixture.cs`, `tests/Sleipnir.Tests/Server/DaemonHostTests.cs` (adapt only construction), `docs`

**Interfaces:**
- Consumes: `IdentityService` (Task 3).
- Produces: `DaemonHost.Create(DaemonOptions options, IDownloadService downloads, IdentityService identity)`; `record MeDto(Guid UserId, string UserName, UserRole Role, Guid DeviceId, string DeviceName)` with JSON role as `admin`/`member`; extension `HttpContext.Principal() : Principal` for endpoints; `DaemonFixture` gains `IdentityService Identity`, `Principal Admin`, and `Task<(Principal Principal, string Token)> AddUserAsync(string name, UserRole role)` which creates a user plus a device and returns its token; `DaemonFixture.Token` is the local admin's token as before.
- Behaviour: the failure throttle works as before (wrong tokens counted, correct never blocked, no header not counted).

- [ ] **Step 1: Failing tests** (`MeEndpointTests` + existing `DaemonHostTests` keep passing): `GET /api/v1/me` with the local token → 200 with the admin's name, role `admin`, device name `This computer`; with a member's token → role `member`; after `RevokeDeviceAsync` the same token gets 401 on the next request; a token whose user was deleted gets 401; `GET /api/v1/me` without a token → 401; the daemon process test: after start, `GET /me` with `admin.token` returns an admin, and restarting the daemon keeps the same user id (database persisted in the data folder, file mode `0600`); regenerating `admin.token` (delete the file) and restarting still works and the old token is rejected.
- [ ] **Step 2: Run** `--filter "MeEndpointTests|DaemonProcessTests"`. Expected: compile failure / 404 on `/me`.
- [ ] **Step 3: Implement.** In `DaemonCommand`: open `Database.OpenFile(Path.Combine(paths.Data, "dnm.db"))`, `MigrateAsync`, `EnsureLocalAdminAsync(Environment.UserName, token)`; database errors fall under the existing exit-74 path.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): per-device authentication and GET /me`.

---

### Task 5: Users and devices endpoints

**Files:**
- Create: `src/Sleipnir.Server/Api/UserEndpoints.cs`, `src/Sleipnir.Server/Api/DeviceEndpoints.cs`, `src/Sleipnir.Core/Api/UserDto.cs`, `tests/Sleipnir.Tests/Server/UserEndpointTests.cs`
- Modify: `src/Sleipnir.Server/Hosting/DaemonHost.cs` (map them; needs `IDownloadService` for user deletion)

**Interfaces:**
- Consumes: `IdentityService`, `HttpContext.Principal()`, `IDownloadService`.
- Produces: `record UserDto(Guid Id, string DisplayName, UserRole Role, DateTime CreatedAt, string? AllowedRoot)`; `record DeviceDto(Guid Id, Guid UserId, string Name, string Platform, DateTime CreatedAt, DateTime? LastSeenAt, bool Revoked, bool IsLocal)`; `record CreateUserRequest(string? DisplayName, UserRole? Role)`; `record UpdateUserRequest(string? DisplayName, UserRole? Role, string? AllowedRoot)`.
- Endpoints: `GET /users`, `POST /users` (201, role defaults to `member`), `PATCH /users/{id}`, `DELETE /users/{id}?confirm=true` (all **admin only**, members get 403 `forbidden`); `GET /devices` (member: own; admin: all, optional `?user=`), `DELETE /devices/{id}` (member: own; admin: any; 204; the local device can't be revoked: 409 `local_device`; someone else's device as a member: 404 `device_not_found`).
- Error mapping: `IdentityException` codes → `invalid_name` 400, `name_taken` 409, `last_admin` 409, `user_not_found` 404; `DELETE` without `confirm=true` → 400 `confirmation_required`; an `AllowedRoot` that isn't an absolute path (or has NUL) → 400 `invalid_destination`.
- Deleting a user: cancels and removes all of that user's downloads (files stay), removes their devices, returns 204.

- [ ] **Step 1: Failing tests:** admin creates a member (201, default role `member`), duplicate by case → 409 `name_taken`, bad name → 400; the new member's token works on `/me`; a member calling `GET /users`, `POST /users`, `PATCH`, `DELETE` → 403 `forbidden` (no change made); `PATCH` can rename, change role, set `allowedRoot` (relative path → 400); demoting the only admin → 409 `last_admin`; `DELETE` without confirm → 400, with confirm → 204 and: the user's running download is cancelled and removed from the list, a completed file on disk (temp file in the test) still exists, the user's token → 401 afterwards; deleting the only admin → 409; deleting an unknown id → 404; `GET /devices` as member lists only their own, as admin lists all and honours `?user=`; revoking own device → 204 and the token stops working; a member revoking someone else's device → 404; revoking the local device → 409 `local_device`; token hashes never appear in any response body.
- [ ] **Step 2: Run** `--filter UserEndpointTests`. Expected: FAIL (404s).
- [ ] **Step 3: Implement.** Add an `RequireAdmin()` endpoint filter returning the 403 problem.
- [ ] **Step 4: Run the suite.** Expected: all pass.
- [ ] **Step 5: Commit** `feat(server): user and device management endpoints`.

---

### Task 6: Download authorization and per-user folders

**Files:**
- Create: `src/Sleipnir.Server/Identity/DestinationPolicy.cs`, `tests/Sleipnir.Tests/Server/DestinationPolicyTests.cs`, `tests/Sleipnir.Tests/Server/DownloadAccessTests.cs`
- Modify: `src/Sleipnir.Server/Api/DownloadEndpoints.cs`, `src/Sleipnir.Core/Api/DownloadDto.cs` (add `Guid? OwnerId` as the last parameter), `tests/Sleipnir.Tests/Server/DownloadEndpointTests.cs` (only where an expectation changes: add responses now include `ownerId`)

**Interfaces:**
- Consumes: `Principal`, `UserNames.ToFolderName`, `IdentityService.GetUserAsync`.
- Produces: `sealed class DestinationPolicy(string downloadsRoot)` with `string DefaultFor(UserEntity user)` (admin → `downloadsRoot`; member → `AllowedRoot` if set, else `downloadsRoot/<folder name>`) and `bool TryResolve(UserEntity user, string? requested, out string folder)`: no request → `DefaultFor`; admins → the requested absolute path unchanged; members → the requested path fully resolved (`Path.GetFullPath`, then symlinks resolved on the deepest existing ancestor via `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` / `Directory.ResolveLinkTarget`) must equal or lie below the resolved allowed root compared with a trailing-separator-aware check; otherwise false.
- Endpoint rules: add stamps `OwnerId` = caller; a failed `TryResolve` → 400 `destination_not_allowed`; `GET /downloads` lists the caller's own (members) or all (admins, `?user=<id>` filter; a member passing `?user=` of someone else → 403 `forbidden`); downloads with `OwnerId == null` are visible to admins only; `GET/pause/resume/cancel/DELETE` on a download the caller can't see → 404 `download_not_found`; idempotency keys are scoped per user (the cache key includes the user id).

- [ ] **Step 1: Failing tests (`DestinationPolicyTests`, real temp directories):** member default is `<root>/<safe name>`; a custom `AllowedRoot` is used; requests equal to, inside, and nested below the root pass; `../`, an absolute path elsewhere, a sibling sharing the prefix (`alice-evil`), a path through a symlink inside the root that points outside, and the root with `/..` appended all fail; trailing separators and `.` segments are normalized; a not-yet-existing subfolder of the root passes; admins may request any absolute path; blank request → default.
- [ ] **Step 2: Failing tests (`DownloadAccessTests` via `DaemonFixture.AddUserAsync`):** two members each add a download and each lists only their own; member B GET/pause/resume/cancel/DELETE on A's id → 404 `download_not_found` for every verb, and the stub saw no call; admin lists both and filters with `?user=`; member `?user=<other>` → 403; a legacy ownerless download is visible to the admin and invisible to members; a member's add without a destination goes to `<downloadsRoot>/<name>`; with a destination outside the root → 400 `destination_not_allowed` and nothing added; admin may add anywhere absolute; same `Idempotency-Key` used by two different users creates two downloads; `ownerId` appears in the DTO.
- [ ] **Step 3: Run** `--filter "DestinationPolicyTests|DownloadAccessTests"`. Expected: compile failure / FAIL.
- [ ] **Step 4: Implement** the policy and wire the endpoints (the owner check lives in one helper `Visible(DownloadTask, Principal)`).
- [ ] **Step 5: Run the suite.** Expected: all pass.
- [ ] **Step 6: Commit** `feat(server): per-user download access and folders`.

---

### Task 7: Docs and a real-world check

**Files:**
- Modify: `docs/USER_GUIDE.md` (users, devices, tokens, curl examples as admin and as a member), `docs/ARCHITECTURE.md` (identity section), `docs/DEVELOPMENT.md` (layout + tests table), `docs/superpowers/specs/2026-11-13-sleipnir-service-design.md` (§10 note: download records stay in JSON for now), `CHANGELOG.md`, `README.md` (one line)

- [ ] **Step 1: Write the docs** (facts from the tests above only).
- [ ] **Step 2: Real check** with the published binary in an isolated home: start the daemon, create a member with the admin token, create a device for them by the API surface that exists (use `DeviceDto`-returning flows only; if no endpoint issues member device tokens yet, record that pairing in plan 3b is what gives devices their tokens and use the test fixture path instead), add downloads as admin and member against a local `python3 -m http.server`, and confirm: the member sees only theirs, the member's file lands in `~/Downloads/<name>/`, the checksums match, the admin sees both; restart the daemon and confirm users and ownership survive; `kill -TERM` exits 0.
- [ ] **Step 3: Final run:** `DN test Sleipnir.sln` all green, also under `--sysctl net.ipv6.conf.all.disable_ipv6=1`; `podman build .` and a run of the image's `daemon`.
- [ ] **Step 4: Commit** `docs: document users, devices and access rules`.
