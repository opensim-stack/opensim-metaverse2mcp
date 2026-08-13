# opensim-metaverse2mcp Capability Gap TODO

This TODO is a capability review of `opensim-metaverse2mcp` against major `LibreMetaverse` surfaces (manager APIs + quick reference workflows).

Scope notes:
- Baseline compared from `libremetaverse/QUICK_REFERENCE.md`, core manager classes in `libremetaverse/LibreMetaverse/*Manager.cs`, and current bridge tools in `opensim-metaverse2mcp/src/BotMcpTools.cs`.
- This is prioritized for MCP bridge usefulness and safe bot operations, not a line-by-line API parity checklist.

## Legend

- `[P0]` Critical for production-safe agent operation
- `[P1]` High-value capability gap
- `[P2]` Nice-to-have or specialist feature
- `[ ]` Not implemented
- `[-]` Partial or incomplete
- `[x]` Implemented

---


## 1) Platform, Reliability, and Security

- [ ] **[P0] Configuration Reloading**
  - After authorizing with provider (or other prov. changes?), opencode has to be restarted somehow.
- [ ] **[P0] Auto-reconnect and session recovery**
  - Recover event subscriptions and MCP-ready state after disconnect.
  - Preserve/restore per-conversation AI session mapping safely.
- [ ] **[P0] AI responder access policy (beyond handler mode)**
  - Implement allowlist/denylist and optional group/parcel-based authorization.
  - Add policy audit trail for denied/allowed chat control attempts.
- [ ] **[P0] Tool-level permission boundaries**
  - Restrict sensitive tools (rez/delete/teleport/script upload) by role/policy.
  - Add read-only mode profile for observer bots.
- [ ] **[P0] Operational observability**
  - Add structured logs and correlation IDs across MCP request -> grid action -> result.
  - Add metrics endpoint (tool latency, failures, reconnect count, inventory offer decisions).
- [ ] **[P1] Idempotency and duplicate suppression beyond IM**
  - Duplicate request guards for object edits and inventory/script operations.
- [ ] **[P1] Failure classification and retry policy**
  - Distinguish transient simulator/caps/network errors from permanent validation errors.

### Dialog Bridge Hardening Status

- [ ] **[P0] Add channel randomization + nonce challenge**
  - Randomize dialog bridge channel and validate per-request nonce to reduce replay/spoof risk.

## 2) Eventing and Reactive Workflows

- [ ] **[P0] MCP event/notification stream**
  - Expose event subscriptions for login/disconnect, object updates, IM/chat, inventory offers, teleport status.
  - Include backpressure and bounded buffers.
- [ ] **[P1] Local chat and group chat AI routing**
  - Local and group routing are explicitly TODO in code; currently IM-focused.
- [ ] **[P1] Event filtering tools**
  - Subscribe by radius, object UUID/local ID, chat source, event type.
- [ ] **[P2] Historical event replay window**
  - Optional short-term event history query for debugging agents.

## 3) Connection and Avatar Control

- [ ] **[P1] Connection lifecycle controls**
  - Add explicit MCP tools for logout/reconnect and connection diagnostics.
  - Add login profile switching at runtime (safe rebind) instead of startup-only login.
- [ ] **[P2] Gestures and typing state controls**
  - Trigger gestures, explicit typing indicators, AFK/away style signals.

## 5) Inventory and Asset Management

- [ ] **[P1] Inventory browsing ergonomics**
  - Add search/filter by name/type/date/creator.
  - Return paginated results with cursors for large inventories.
- [ ] **[P1] Inventory CRUD tools**
  - Create/rename/move/delete folders and items.
  - Copy/link items and folder-level organization tools.
- [ ] **[P1] Notecard and script text editing workflows**
  - Fetch/edit/save textual assets with version checks.
- [ ] **[P1] Asset type coverage expansion**
  - Explicit helpers for landmarks, calling cards, bodyparts, clothing, gestures.
- [-] **[P1] Upload validation pipeline**
  - Validate image/audio/script formats pre-upload; provide clear conversion expectations.
- [ ] **[P2] Bulk transfer workflows**
  - Batch give/take with result breakdown and retry support.

## 6) Scripts and Task Inventory

- [ ] **[P1] Script lifecycle coverage**
  - Add script reset and script event queue stats where supported.
  - Add compile result normalization for common viewer/server compiler messages.
- [ ] **[P1] Multi-script object workflows**
  - Batch start/stop/status operations on all scripts in object/task inventory.
- [ ] **[P2] Script source provenance**
  - Optional hash/signature metadata for uploaded script source.

## 7) Environment, Land, and Region Features

- [ ] **[P1] Parcel management tools (`ParcelManager`)**
  - full ACL write/edit/remove primitives (library gap in current surfaced API path).
- [ ] **[P1] Estate/admin surfaces (where permissions allow)**
  - Richer estate settings mutation coverage and stronger capability/permission-specific error classification.
- [ ] **[P1] EEP/Windlight usability layer**
  - High-level presets and patch operations instead of raw LLSD only.

## 8) Social, Groups, Search, and Directory

- [ ] **[P1] Friends operations (`FriendsManager`)**
  - Friends list, friendship offers, online status, teleport offers/requests.
- [ ] **[P1] Directory/search (`DirectoryManager`)**
  - Search people, groups, land, places; return structured pagination.
- [ ] **[P2] Avatar profile tools (`AvatarManager`)**
  - Read profile/interests/picks/classifieds where supported.

## 9) Communications Beyond IM

- [ ] **[P1] Chat modalities**
  - Add explicit whisper/shout tools and receive-side filters.
- [ ] **[P1] Group chat and conference session control**
  - Start/join/leave group chat sessions; route safely to AI session model.
- [ ] **[P2] Conferencing/voice signaling integration hooks**
  - Text-side signaling for out-of-band media bridges.

## 10) Voice / WebRTC

- [ ] **[P1] Voice bridge support (`LibreMetaverse.Voice.WebRTC`)**
  - Connect/disconnect voice, peer events, mute/unmute, volume.
  - Optional audio processing toggles (NS/HPF/AGC/AEC) via MCP tools.
- [ ] **[P2] Voice moderation policies**
  - Auto-mute/auto-join policies and permission checks.

## 11) OSD/LLSD and Data Utilities

- [ ] **[P1] Generic OSD conversion tools**
  - JSON/XML/Binary LLSD conversion and validation tools for agent workflows.
- [ ] **[P1] Structured patch helpers for EEP/legacy env payloads**
  - Safer patch API instead of replacing full blobs.
- [ ] **[P2] Object/asset serialization helpers**
  - Export/import structured snapshots for reproducible scene edits.

## 12) MCP UX and Contract Quality

- [ ] **[P0] Tool contract consistency**
  - Normalize result schemas (status/error codes, transient/permanent flags, correlation IDs).
- [ ] **[P1] Long-running operation model**
  - Job IDs + polling/cancel pattern for uploads, teleports, recursive inventory, bulk ops.
- [ ] **[P1] Pagination and limits**
  - Standard pagination envelope across all list/query tools.
- [ ] **[P1] Safer defaults for destructive tools**
  - Confirm/delete guardrails similar to session delete flow.

## 13) Testing and Validation Gaps

- [ ] **[P0] Capability-level integration tests**
  - End-to-end tests for movement, prim edit, inventory transfer, script upload, policy decisions.
- [ ] **[P0] Failure-path tests**
  - Simulated disconnects, capability missing errors, permission denial, stale cache objects.
- [ ] **[P1] Contract tests for MCP schemas**
  - Ensure stable JSON output for tool consumers.
- [ ] **[P1] Regression tests for IM command parser**
  - Commands, aliases, chunking, prompt/permission/question flows.

---

## Suggested Implementation Phases

### Phase 1 (P0 hardening)

- Access policy enforcement
- Event stream foundation
- Tool schema normalization
- Integration/failure-path tests

### Phase 2 (P1 capability expansion)

- Parcel/friends/directory tools
- Rich inventory CRUD + pagination
- Local/group chat routing and controls
- Voice baseline controls

### Phase 3 (P2 ergonomics)

- Higher-level EEP/OSD patch tooling
- Advanced avatar/profile/marketplace extras
- Bulk workflow optimization and replay tooling