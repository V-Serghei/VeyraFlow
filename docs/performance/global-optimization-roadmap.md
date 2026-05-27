# Global Optimization Roadmap

## Goal

Make the desktop app feel instant under normal interaction:

- no visible UI freezes on navigation, filtering, compare, sync, or settings
- progressive page loading instead of blocking full-screen waits
- CPU-heavy work moved out of Avalonia/UI paths
- deterministic offload of compute-heavy tasks into Rust/native code

## Current Native Baseline

The project already has a Rust core in `native/veyra_core` with native entrypoints for:

- repository scan
- block store / restore
- text diff
- snapshot comparison
- repository path comparison
- version planning

This means the next step is not "add Rust", but "standardize how compute reaches Rust and keep UI fully decoupled from it".

## Frontend Execution Rules

These rules should become project-wide:

1. Never do sorting, filtering, tree building, diff formatting, or large collection projection directly on the UI thread.
2. Any page should render shell chrome first, then load secondary sections progressively.
3. Every filter/search action must be:
   - debounced
   - cancelable
   - last-request-wins
4. Background work must not share mutable EF/DbContext state across concurrent tasks.
5. Large lists must support staged population, virtualization, or chunked append.

## Rust Offload Strategy

### Keep in C#

- orchestration
- EF/database access
- HTTP/API pipelines
- UI state and navigation
- DI/composition

### Move to Rust

- large set comparison
- diff/hunk generation
- repository path planning
- bulk hash / checksum work
- scan projections and metadata aggregation
- retention candidate computation
- dashboard/search aggregation over large in-memory datasets

### Optional Later Native Targets

- preview indexing for large text files
- duplicate detection / similarity clustering
- bulk retention rule evaluation
- packed metrics aggregation for dashboards

## Phase Plan

### Phase 1: UI Responsiveness Baseline

- make all main screens progressively load
- move dashboard/search/explorer filtering to cancelable background pipelines
- make window/page navigation show content first and hydrate later
- add bounded native execution scheduler

### Phase 2: Data Flow Stabilization

- isolate all concurrent mediator/EF calls with scoped executors
- remove remaining sync-over-async and blocking waits from startup/shutdown
- split expensive repository refresh flows into independent sections
- add page-level cancellation tokens for every load path

### Phase 3: Native Compute Expansion

- route all existing Rust entrypoints through one scheduler
- move managed fallback hot paths behind the same abstraction
- add native aggregation entrypoints for dashboard/search over repository metadata
- add native retention planning entrypoint

### Phase 4: Incremental Rendering

- chunk large observable collection updates
- add virtualization where item count can spike
- avoid full collection clears when only deltas changed
- keep selected state stable while background refresh replaces data

### Phase 5: Perf Guardrails

- add timing checkpoints around every screen load phase
- record slow-path diagnostics only when runtime diagnostics are enabled
- build regression tests around:
  - dashboard load
  - repository explorer refresh
  - compare window open
  - settings open
  - search filter latency

## Immediate Execution Order

1. Native scheduler for Rust/FFI work
2. Dashboard async filtering and background card projection
3. Remove remaining blocking waits from `App.axaml.cs`
4. Split compare windows into staged loading
5. Add Rust-native aggregation entrypoints for dashboard/search
6. Rework retention and snapshot planning into native compute

## Success Metrics

- navigation response starts within 100 ms for common screens
- filter interactions remain responsive under repeated rapid input
- settings/dashboard/search render shell immediately
- no UI-thread-bound processing on large collections
- native work runs with bounded concurrency and does not starve the thread pool
