# Development of an Autonomous File Manager for Personal Computers

## VeyraFlow

This repository contains the implementation of the diploma project
**Development of an autonomous file manager for personal computers**.

`VeyraFlow` is the technical name of the project and repository. The system is a desktop file manager with built-in versioning, block-level deduplication, local snapshot storage, data protection, and optional cloud synchronization.

## What the project does

The application is designed for end users who need a familiar file-manager workflow combined with version control features, without requiring Git knowledge. In its current implementation, the project supports:

- local repositories bound to selected directories
- tracked file formats with category presets and custom extensions
- exclusion rules for folders and path patterns
- manual, automatic, and scheduled snapshots
- per-file version history
- text diff preview
- image, audio, and snapshot comparison workflows
- restore operations for previous versions
- repository bundle export and import
- operation journal and diagnostics
- optional cloud synchronization with remote block transfer

## Current architecture

The codebase is organized as a mono-repo with several cooperating components.

### Desktop and application layers

- **`src/Veyra.Desktop`**  
  Avalonia 11 desktop client targeting **.NET 10**.  
  Implements the user interface, navigation, MVVM view models, onboarding, diagnostics, operation monitoring, and user-facing settings.

- **`src/Veyra.Application`**  
  Application layer with use cases, commands, queries, DTOs, validation, and MediatR pipeline behaviors.

- **`src/Veyra.Domain`**  
  Domain entities for repositories, snapshots, file identities, file versions, blocks, journal entries, and sync queue items.

### Local infrastructure

- **`src/Veyra.Infrastructure.Data`**  
  Local persistence layer based on **Entity Framework Core + SQLite**.  
  Stores repository metadata, snapshot metadata, version graph, journal records, and sync queue state.

- **`src/Veyra.Infrastructure.Native`**  
  Bridge between .NET and the native Rust library.  
  Provides repository scanning, block storage access, text diff, snapshot comparison, diagnostics, and local encryption services.

- **`src/Veyra.Infrastructure.Sync`**  
  Cloud integration layer.  
  Contains HTTP clients for authentication and synchronization, token handling, retry policies, and metadata protection hooks.

### Native core

- **`native/veyra_core`**  
  Native Rust library loaded through P/Invoke.  
  In the current implementation it is responsible for:
  - directory scanning
  - block storage for the native path
  - block compression with `zstd`
  - block hashing with `BLAKE3`
  - text diff generation
  - snapshot and repository comparison helpers

### Cloud services

- **`server/cloud-api`**  
  Go-based cloud service used by the desktop client.  
  The current working implementation exposes a **REST API** for:
  - authentication
  - repository listing
  - snapshot upload
  - block upload and download
  - batch block upload
  - cloud storage metrics and repair

- **`tools/sync-agent`** and **`proto/sync.proto`**  
  Experimental/prototype gRPC direction.  
  These files exist in the repository, but the main production sync flow currently uses the REST-based `cloud-api`.

## Implementation notes

Several important technical details are worth stating explicitly because older descriptions in the repository may no longer match the code:

- the desktop client is **Avalonia**, not WPF
- the current target framework is **.NET 10**
- the active cloud path is **REST over HTTP**
- local metadata is stored in **SQLite**
- the cloud service uses **PostgreSQL**
- file-content deduplication is block-based
- the native storage path uses `BLAKE3` for block identity and `zstd` for compression
- version metadata and integrity-related fields also use **SHA-256** where appropriate
- when encrypted artifact storage is enabled, the managed storage path uses compression plus **AES-GCM** protection
- cloud metadata protection is supported and can encrypt repository and path metadata before upload

## Repository layout

```text
src/
  Veyra.Desktop/
  Veyra.Application/
  Veyra.Domain/
  Veyra.Infrastructure.Data/
  Veyra.Infrastructure.Native/
  Veyra.Infrastructure.Sync/
  Veyra.Shared.Logging/

native/
  veyra_core/

server/
  cloud-api/

proto/
  sync.proto

tools/
  sync-agent/

tests/
  ...
```

## Build prerequisites

To build the full solution as it exists now, the practical baseline is:

- **.NET 10 SDK**
- **Rust toolchain**
- **Go 1.25+** for the cloud service

## Build

### 1. Build the native Rust library

```powershell
cd native/veyra_core
cargo build --release
```

### 2. Build the .NET solution

```powershell
cd ../..
dotnet build VeyraFlow.sln
```

### 3. Build the cloud API

```powershell
cd server/cloud-api
go build ./cmd/api
```

## Run

### Desktop client

```powershell
dotnet run --project src/Veyra.Desktop/Veyra.Desktop.csproj
```

### Cloud API

```powershell
cd server/cloud-api
go run ./cmd/api
```

By default, the desktop client expects the cloud API at:

```text
http://localhost:8080
```

## Tests

### .NET tests

```powershell
dotnet test VeyraFlow.sln
```

### Rust tests

```powershell
cd native/veyra_core
cargo test
```

## Database and storage

### Local desktop storage

The desktop application creates its local SQLite database in the user profile under local application data. The database stores repository metadata, snapshots, versions, journal records, and sync queue state.

### Cloud storage

The Go cloud service stores relational metadata in PostgreSQL and keeps uploaded blocks in a separate block store. The service also supports pack-oriented block storage maintenance and storage metrics endpoints.

## Key technical mechanisms

The current codebase implements the following architectural mechanisms:

- dependency injection across all .NET modules
- MediatR-based command and query orchestration
- EF Core migrations for local schema evolution
- P/Invoke interop for Rust native functions
- scheduled background scanning and integrity verification
- block-level deduplication
- compression before encrypted managed storage
- repository bundle import/export
- operation journal and diagnostics export
- cloud batch block upload and remote storage metrics

## Status of gRPC support

The repository contains a proto definition and an experimental sync agent, but this path should currently be treated as **prototype-level**. If documentation or academic text describes gRPC as a core active transport, that should be updated or explicitly marked as a prepared extension rather than the primary runtime path.

## License

See the repository license file, if provided, for usage terms.
