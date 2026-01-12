# DotNet Download Manager (DNM)

A high-performance, multi-threaded **download manager for .NET**, inspired by the functionality of Internet Download Manager (IDM), built as a **console-first application** with a modern **Text User Interface (TUI)** using **Spectre.Console**.

The goal of this project is not just speed, but **reliability, control, and clean architecture**.

---

## 🚀 Features (Planned & In Progress)

### Core Download Features
- Multi-threaded (segmented) downloads using HTTP Range requests
- Automatic fallback to single-threaded downloads if server does not support ranges
- Pause / Resume downloads
- Resume after application restart
- Robust retry & error recovery per segment
- Download queue management
- Per-download progress tracking
- Speed calculation (per segment & total)
- File integrity verification (optional)

### Terminal UI (TUI)
- Interactive console UI using Spectre.Console
- Live progress bars
- Download list with status:
  - Queued
  - Downloading
  - Paused
  - Completed
  - Failed
- Keyboard-driven navigation
- ASCII-safe rendering (works in all consoles)

### Browser Integration (Future)
- Browser extension (Chrome / Edge / Firefox)
- Intercept download links
- Send download tasks to the running download manager via local API / IPC

---

## 🧠 Why another download manager?

Browsers prioritize:
- Safety
- Simplicity
- Fair server usage

They intentionally limit aggressive parallelism.

This project focuses on:
- Maximum bandwidth utilization
- Stability on unreliable networks
- Full user control
- Learning & experimenting with real-world systems concepts

---

## 🏗 Architecture Overview

The application follows **SOLID principles** and a layered architecture.

DotNetDownloadManager
│
├── Core
│ ├── Models // DownloadTask, DownloadSegment, DownloadState
│ ├── Interfaces // Abstractions for FS, Network, Scheduler
│ └── Contracts // DTOs & shared contracts
│
├── Services
│ ├── DownloadService // Orchestrates downloads
│ ├── SegmentDownloader // Handles individual segments
│ ├── SchedulerService // Queue & concurrency control
│ ├── ResumeService // Persistence & resume logic
│
├── Infrastructure
│ ├── Http // HttpClient, range handling
│ ├── Persistence // Metadata storage (JSON / DB)
│ └── System // OS integration
│
├── UI
│ ├── TUI // Spectre.Console rendering
│ └── Input // Keyboard commands
│
└── Program.cs


### Design Principles
- **Single Responsibility**: Each service does one thing
- **Open / Closed**: New protocols or UIs can be added without modifying core logic
- **Dependency Inversion**: Core depends on interfaces, not implementations
- **Testability**: Download logic is decoupled from UI

---

## 📦 Technology Stack

- .NET 8 / .NET 9 (Console Application)
- C#
- Spectre.Console – Terminal UI
- HttpClient – Network layer
- Task / async-await – Concurrency
- JSON persistence (initially)

---

## 🔄 How Multi-Threaded Downloading Works

1. Perform a HEAD request to detect:
   - File size
   - Range request support
2. Split file into segments
3. Download segments in parallel
4. Persist progress per segment
5. Merge segments into final file
6. Resume from last known state if interrupted

---

## 🖥 Example TUI (Concept)

┌──────────────────────────────────────────────┐
│ DotNet Download Manager │
├──────────────────────────────────────────────┤
│ ubuntu.iso [███████░░░░░░] 42% 2.3MB/s│
│ movie.mkv [████████████░] 95% 5.1MB/s│
│ sdk.zip Queued │
│ tools.exe Paused │
└──────────────────────────────────────────────┘
[ A ] Add [ P ] Pause [ R ] Resume [ Q ] Quit


---

## 🧩 Planned Extensions

- Browser extension integration
- REST / IPC API for external control
- Plugin system (post-processing, checksum, extraction)
- Scheduler rules (time-based, speed limits)
- Proxy / VPN-aware downloads
- HTTP/2 and HTTP/3 optimization experiments

---

## 🎯 Project Goals

- Build a **real-world systems application**
- Learn advanced networking and concurrency
- Maintain clean, maintainable architecture
- Avoid demo-only code
- Stay console-first (GUI optional later)

---

## ⚠ Disclaimer

This project is for **educational and personal use**.  
Always respect server policies, rate limits, and legal constraints when downloading content.

---

## 📌 Status

🚧 **Early Development / Design Phase**

Core architecture and foundational components are being actively designed and implemented.