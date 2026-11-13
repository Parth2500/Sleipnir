# Library Split Implementation Plan (Phase 2, step 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the single `DotNet Download Manager` project into `Sleipnir.Core`, `Sleipnir.Engine` and the `dnm` app (`Sleipnir.App`), with zero behaviour change and all 113 existing tests green and unmodified.

**Architecture:** A pure refactor. Files move with `git mv` (history kept) into `src/<Project>/`; only `.csproj`/solution/doc/Docker files are edited. Dependency direction is `App → Engine → Core`; Core has no third-party references; Engine has no console (Spectre) and no ASP.NET so a future Android app can reuse it. Guard tests pin those rules.

**Tech Stack:** .NET 10 (`net10.0`), xUnit, Spectre.Console 0.54.0 (App only), podman for builds.

**Spec:** `docs/superpowers/specs/2026-11-13-sleipnir-service-design.md` (§4.1 Projects, §11a, §12 "existing 113 tests keep passing unchanged (moved projects only)").

## Global Constraints

- No local .NET 10 SDK. Run every dotnet command in a container: `podman run --rm --security-opt label=disable -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet <args>` (called `DOTNET` below).
- Baseline before any change: `DOTNET test Sleipnir.sln` → `Passed! Failed: 0, Passed: 113`. Every task ends with that count (guard tests add to it).
- Existing test files are not edited. Only `tests/DotNetDownloadManager.Tests/*.csproj` changes, plus new test files.
- C# namespaces stay `DotNetDownloadManager.*` (renaming them is a separate change) [my call]. Only assembly/project names change.
- Project names and roles exactly as spec §4.1: `Sleipnir.Core` (models, interfaces, exceptions), `Sleipnir.Engine` (today's `Services/` + `Infrastructure/`), `dnm` app (TUI + `Program.cs`).
- Behaviour that must not change: state directory `~/.local/share/dnm/downloads/`, executable name `dnm`, single-file self-contained Linux publish.
- Work on branch `phase2-library-split`, not `main`. Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Do not stage `docs/ideas/suite-vision.md` (the owner's pending deletion); stage files by explicit path, never `git add -A`.

## Review Focus

1. Stale root `bin/` and `obj/` after the move can make restore or tests pick up old outputs: delete them (Task 1); `dotnet build` at the repo root must find exactly one solution.
2. A leaked dependency (Spectre or ASP.NET into Engine, third-party into Core) would silently break the Android reuse goal: guard tests (Tasks 2, 3).
3. The default state directory must still end in `dnm/downloads` so existing users' saved downloads are found: pin test (Task 3).
4. The published executable must still be named `dnm`, self-contained, and exit cleanly on SIGTERM: smoke check (Task 4).
5. The Docker build and VS Docker tooling break if `DockerfileContext`/paths aren't updated: `podman build` check (Task 4).

---

### Task 1: Move the app into `src/Sleipnir.App` and rename the solution

**Files:**
- Create: `Directory.Build.props`, `src/Sleipnir.App/Sleipnir.App.csproj`
- Move (`git mv`): `Program.cs`, `UI/`, `Properties/` → `src/Sleipnir.App/`; `Core/`, `Infrastructure/`, `Services/` → `src/Sleipnir.App/` (temporarily, split in Tasks 2 and 3); `DotNet Download Manager.sln` → `Sleipnir.sln`
- Delete: `DotNet Download Manager.csproj` (and its `.csproj.user`), root `bin/`, `obj/`
- Modify: `tests/DotNetDownloadManager.Tests/DotNetDownloadManager.Tests.csproj`

**Interfaces:**
- Produces: layout `src/<Project>/` and `Directory.Build.props` (shared `TargetFramework=net10.0`, `ImplicitUsings=enable`, `Nullable=enable`) used by Tasks 2 and 3.

- [ ] **Step 1: Branch and commit the approved spec and this plan**

```bash
git switch -c phase2-library-split
git add docs/superpowers/specs docs/superpowers/plans
git commit -m "docs: add Phase 2 service design and library-split plan"
```

- [ ] **Step 2: Move files with `git mv`** into `src/Sleipnir.App/` as listed; `git mv "DotNet Download Manager.sln" Sleipnir.sln`; `git rm` the old csproj and `.csproj.user`; `rm -rf bin obj`.

- [ ] **Step 3: Write `Directory.Build.props`** with the three shared properties above, and `src/Sleipnir.App/Sleipnir.App.csproj` with: `OutputType Exe`, `AssemblyName dnm`, `RootNamespace DotNetDownloadManager`, `DockerDefaultTargetOS Linux`, `DockerfileContext ..\..`, the two existing package references (versions unchanged), no `tests/**` removal lines and no `InternalsVisibleTo` (nothing is `internal`). In `Properties/PublishProfiles/linux-x64.pubxml` delete the now-redundant `AssemblyName` line and fix the usage comment path; in `launchSettings.json` rename the profile to `Sleipnir`.

- [ ] **Step 4: Repoint the solution and tests.** Edit `Sleipnir.sln` (via `DOTNET sln Sleipnir.sln remove/add`, not by hand) so it holds `src/Sleipnir.App/Sleipnir.App.csproj` and the tests project. Change the test project's `ProjectReference` to `../../src/Sleipnir.App/Sleipnir.App.csproj`.

- [ ] **Step 5: Run `DOTNET test Sleipnir.sln`.** Expected: `Passed! Failed: 0, Passed: 113`.

- [ ] **Step 6: Commit**

```bash
git add -A -- Directory.Build.props Sleipnir.sln src tests "DotNet Download Manager.sln" "DotNet Download Manager.csproj" "DotNet Download Manager.csproj.user" Program.cs UI Core Infrastructure Services Properties
git commit -m "refactor: move the app to src/Sleipnir.App and rename the solution"
```

(Docs, README and Dockerfile still mention old paths until Task 4. The pathspec avoids staging `docs/ideas`.)

---

### Task 2: Extract `Sleipnir.Core`

**Files:**
- Create: `src/Sleipnir.Core/Sleipnir.Core.csproj`, `tests/DotNetDownloadManager.Tests/ArchitectureTests.cs`
- Move: `src/Sleipnir.App/Core/Interfaces`, `src/Sleipnir.App/Core/Models` → `src/Sleipnir.Core/Interfaces`, `src/Sleipnir.Core/Models`
- Modify: `src/Sleipnir.App/Sleipnir.App.csproj`, tests csproj, `Sleipnir.sln`

**Interfaces:**
- Produces: assembly `Sleipnir.Core` containing `DownloadTask`, `DownloadSegment`, `DownloadStatus`, `RemoteFileInfo`, `DownloadRecord`, `SegmentRecord`, `IDownloadService`, `INetworkClient`, `IFileStorage`, `IDownloadStateStore`, `RangeNotSupportedException`, `ServerBusyException` (namespaces unchanged: `DotNetDownloadManager.Core.Models` / `.Core.Interfaces`).

- [ ] **Step 1: Write the failing guard test** in `ArchitectureTests.cs` (namespace `DotNetDownloadManager.Tests`, class `ArchitectureTests`):

```csharp
[Fact]
public void Core_is_its_own_assembly_with_only_framework_references()
{
    var core = typeof(DownloadTask).Assembly;
    Assert.Equal("Sleipnir.Core", core.GetName().Name);
    Assert.All(core.GetReferencedAssemblies(),
        r => Assert.True(r.Name is "netstandard" or "mscorlib" || r.Name!.StartsWith("System."),
            $"Core must not reference {r.Name}"));
}
```

- [ ] **Step 2: Run** `DOTNET test Sleipnir.sln --filter ArchitectureTests`. Expected: FAIL (`Expected: Sleipnir.Core, Actual: dnm`).

- [ ] **Step 3: Create `Sleipnir.Core.csproj`** (`RootNamespace DotNetDownloadManager`, no packages, no references), `git mv` the two folders, add the project to the solution, and add a `ProjectReference` to it from the App csproj and the tests csproj.

- [ ] **Step 4: Run** `DOTNET test Sleipnir.sln`. Expected: `Failed: 0, Passed: 114`.

- [ ] **Step 5: Commit**

```bash
git add src tests Sleipnir.sln
git commit -m "refactor: extract Sleipnir.Core (models, interfaces)"
```

---

### Task 3: Extract `Sleipnir.Engine`

**Files:**
- Create: `src/Sleipnir.Engine/Sleipnir.Engine.csproj`
- Move: `src/Sleipnir.App/Services`, `src/Sleipnir.App/Infrastructure` → `src/Sleipnir.Engine/Services`, `src/Sleipnir.Engine/Infrastructure`
- Modify: `src/Sleipnir.App/Sleipnir.App.csproj`, tests csproj, `Sleipnir.sln`, `tests/DotNetDownloadManager.Tests/ArchitectureTests.cs`

**Interfaces:**
- Consumes: `Sleipnir.Core` (Task 2).
- Produces: assembly `Sleipnir.Engine` containing `DownloadService`, `DownloadOptions`, `SegmentDownloader`, `FileNameResolver`, `HttpNetworkClient`, `HappyEyeballs`, `LocalFileStorage`, `JsonDownloadStateStore`. It references only `Sleipnir.Core` plus framework. The App references Engine and Core.

- [ ] **Step 1: Write two failing tests** in `ArchitectureTests`:

```csharp
[Fact]
public void Engine_has_no_console_or_web_dependencies()
{
    var engine = typeof(DownloadService).Assembly;
    Assert.Equal("Sleipnir.Engine", engine.GetName().Name);
    var refs = engine.GetReferencedAssemblies().Select(r => r.Name!).ToList();
    Assert.Contains("Sleipnir.Core", refs);
    Assert.DoesNotContain(refs, n => n.StartsWith("Spectre") || n.StartsWith("Microsoft.AspNetCore") || n == "dnm");
}

[Fact]
public void Default_state_directory_is_unchanged()
{
    var dir = JsonDownloadStateStore.DefaultDirectory().Replace('\\', '/');
    Assert.EndsWith("dnm/downloads", dir);
}
```

- [ ] **Step 2: Run** `DOTNET test Sleipnir.sln --filter ArchitectureTests`. Expected: the Engine test FAILS (assembly is `dnm`); the directory test passes already (it pins existing behaviour).

- [ ] **Step 3: Create `Sleipnir.Engine.csproj`** (`RootNamespace DotNetDownloadManager`, `ProjectReference` to Core, no packages), `git mv` the two folders, add to the solution, reference Engine from the App and tests csproj.

- [ ] **Step 4: Run** `DOTNET test Sleipnir.sln`. Expected: `Failed: 0, Passed: 116`.

- [ ] **Step 5: Commit**

```bash
git add src tests Sleipnir.sln
git commit -m "refactor: extract Sleipnir.Engine (download engine, no UI or web deps)"
```

---

### Task 4: Docs, Docker, and the production build

**Files:**
- Modify: `Dockerfile`, `README.md`, `docs/DEVELOPMENT.md`, `docs/USER_GUIDE.md`, `docs/ARCHITECTURE.md`, `CHANGELOG.md`

- [ ] **Step 1: Update commands and paths.** Replace `"DotNet Download Manager.sln"` with `Sleipnir.sln` and `"DotNet Download Manager.csproj"` with `src/Sleipnir.App/Sleipnir.App.csproj` in README, DEVELOPMENT, USER_GUIDE. In DEVELOPMENT.md redraw the "Project layout" tree for the three projects and delete the paragraph about `Compile Remove="tests/**"`. In ARCHITECTURE.md update the layer table and diagram labels (`Services/…` → `Engine`, `Core/…` → `Core`) and the Phase-2 settings/limiter lines stay as they are. Add a CHANGELOG entry: "Internal: code split into Sleipnir.Core, Sleipnir.Engine and the dnm app; no behaviour change."

- [ ] **Step 2: Fix the `Dockerfile`** so restore copies `Directory.Build.props` and the three csproj files (`src/Sleipnir.*/*.csproj`) first, then `COPY . .`; build and publish `src/Sleipnir.App/Sleipnir.App.csproj`; `ENTRYPOINT ["dotnet", "dnm.dll"]`. Verify: `podman build -t sleipnir-test .` exits 0.

- [ ] **Step 3: Verify the production build.**

```bash
DOTNET publish src/Sleipnir.App/Sleipnir.App.csproj -p:PublishProfile=linux-x64
ls -l artifacts/linux-x64/dnm
timeout 3 artifacts/linux-x64/dnm < /dev/null; echo "exit=$?"
```

Expected: `dnm` exists (about 43 MB, self-contained); the run is stopped by `timeout` (exit 124) and printed "Saving progress…" then "Bye." (SIGTERM exits gracefully).

- [ ] **Step 4: Final checks.** `DOTNET test Sleipnir.sln` → `Passed: 116, Failed: 0`. `grep -rn "DotNet Download Manager\.\(csproj\|sln\)" . --include='*.md' --include=Dockerfile` returns nothing. `git status` shows only `docs/ideas/suite-vision.md` as an uncommitted change.

- [ ] **Step 5: Commit**

```bash
git add Dockerfile README.md CHANGELOG.md docs/DEVELOPMENT.md docs/USER_GUIDE.md docs/ARCHITECTURE.md
git commit -m "docs: update paths, Docker and layout for the three-project split"
```
