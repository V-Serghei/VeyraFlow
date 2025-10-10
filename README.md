# VeyraFlow

VeyraFlow is a file explorer with built-in version control. It can create file snapshots, save space with deduplication, encrypt data, use an SQLite index, and sync with the cloud.

## Architecture

This is a mono-repo containing multiple components:

### .NET Components
- **Veyra.Desktop** - Avalonia MVVM desktop application (cross-platform UI)
- **Veyra.Application** - Application layer with business logic
- **Veyra.Domain** - Domain entities and core business models
- **Veyra.Infrastructure.Data** - Data access layer using EF Core + SQLite with WAL mode and Dapper for bulk operations

### Native Components
- **veyra_core** (Rust) - High-performance native library with:
  - FastCDC for content-defined chunking
  - BLAKE3 for fast cryptographic hashing
  - zstd for compression
  - XChaCha20-Poly1305 for encryption
  - C-ABI via cbindgen for .NET P/Invoke interop

### Services
- **sync-agent** (Go) - gRPC-based synchronization service for cloud sync

## Building

### Prerequisites
- .NET 9.0 SDK
- Rust 1.70+
- Go 1.24+
- cbindgen (install via `cargo install cbindgen`)

### Build All
```bash
# Build Rust native library
cd native/veyra_core
cargo build --release

# Build .NET solution
dotnet build

# Build Go sync agent
cd tools/sync-agent
go build
```

### Run Tests
```bash
# .NET tests
dotnet test

# Rust tests
cd native/veyra_core
cargo test
```

## Development

The solution uses:
- **SQLite WAL mode** for improved concurrency
- **Dependency Injection** throughout all .NET layers
- **Entity Framework Core** with migrations for schema management
- **P/Invoke** for native Rust library integration
- **gRPC** for service communication

### Database Migrations
```bash
cd src/Veyra.Infrastructure.Data
dotnet ef migrations add <MigrationName>
dotnet ef database update
```

## CI/CD

GitHub Actions workflows build and test on:
- Windows (win-x64)
- Linux (linux-x64)
- macOS (osx-x64)

Native libraries are automatically built for each platform and packaged with the desktop application.

## License

See LICENSE file for details.

