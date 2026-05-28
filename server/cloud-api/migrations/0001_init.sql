CREATE TABLE IF NOT EXISTS users (
    id            bigserial PRIMARY KEY,
    username      text NOT NULL UNIQUE,
    email         text NULL,
    password_hash text NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS email text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email_ci
    ON users (lower(email))
    WHERE email IS NOT NULL;

CREATE TABLE IF NOT EXISTS devices (
    id                 bigserial PRIMARY KEY,
    user_id            bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_fingerprint text NOT NULL,
    device_name        text NULL,
    platform           text NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_seen_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, device_fingerprint)
);

CREATE TABLE IF NOT EXISTS user_sessions (
    id                 bigserial PRIMARY KEY,
    user_id            bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id          bigint NULL REFERENCES devices(id) ON DELETE SET NULL,
    refresh_token_hash text NOT NULL,
    access_expires_at  timestamptz NOT NULL,
    refresh_expires_at timestamptz NOT NULL,
    revoked_at         timestamptz NULL,
    revoke_reason      text NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_used_at       timestamptz NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_sessions_refresh_hash
    ON user_sessions(refresh_token_hash);

CREATE INDEX IF NOT EXISTS ix_user_sessions_user_active
    ON user_sessions(user_id, revoked_at, refresh_expires_at DESC, id DESC);

CREATE TABLE IF NOT EXISTS cloud_block_packs (
    id            bigserial PRIMARY KEY,
    relative_path text NOT NULL UNIQUE,
    state         text NOT NULL DEFAULT 'active',
    bytes_written bigint NOT NULL DEFAULT 0,
    block_count   integer NOT NULL DEFAULT 0,
    created_at    timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    sealed_at     timestamptz NULL
);

CREATE TABLE IF NOT EXISTS cloud_blocks (
    block_hash   text PRIMARY KEY,
    length_bytes integer NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE cloud_blocks
    ADD COLUMN IF NOT EXISTS storage_kind text NOT NULL DEFAULT 'loose',
    ADD COLUMN IF NOT EXISTS pack_id bigint NULL REFERENCES cloud_block_packs(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS pack_offset_bytes bigint NULL,
    ADD COLUMN IF NOT EXISTS stored_size_bytes bigint NULL;

UPDATE cloud_blocks
SET storage_kind = COALESCE(NULLIF(storage_kind, ''), 'loose'),
    stored_size_bytes = COALESCE(stored_size_bytes, length_bytes)
WHERE storage_kind IS NULL
   OR btrim(storage_kind) = ''
   OR stored_size_bytes IS NULL;

CREATE INDEX IF NOT EXISTS ix_cloud_blocks_pack_lookup
    ON cloud_blocks(pack_id, pack_offset_bytes)
    WHERE pack_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_cloud_blocks_storage_kind_created
    ON cloud_blocks(storage_kind, created_at, block_hash);

CREATE INDEX IF NOT EXISTS ix_cloud_block_packs_active
    ON cloud_block_packs(state, id DESC);

CREATE TABLE IF NOT EXISTS repositories (
    id                      bigserial PRIMARY KEY,
    user_id                 bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_repository_id  integer NOT NULL,
    name                    text NOT NULL,
    description             text NULL,
    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, external_repository_id)
);

CREATE TABLE IF NOT EXISTS snapshots (
    id                  bigserial PRIMARY KEY,
    repository_id       bigint NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    external_snapshot_id bigint NOT NULL,
    title               text NULL,
    trigger             text NOT NULL,
    created_at          timestamptz NOT NULL,
    total_entries       integer NOT NULL DEFAULT 0,
    file_entries        integer NOT NULL DEFAULT 0,
    directory_entries   integer NOT NULL DEFAULT 0,
    total_file_bytes    bigint NOT NULL DEFAULT 0,
    payload_sha256      text NULL,
    inserted_at         timestamptz NOT NULL DEFAULT now(),
    UNIQUE(repository_id, external_snapshot_id)
);

CREATE TABLE IF NOT EXISTS snapshot_entries (
    id                   bigserial PRIMARY KEY,
    snapshot_id          bigint NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
    relative_path        text NOT NULL,
    parent_relative_path text NULL,
    name                 text NOT NULL,
    is_directory         boolean NOT NULL,
    extension            text NULL,
    size_bytes           bigint NOT NULL DEFAULT 0,
    last_write_utc       timestamptz NOT NULL,
    content_hash_sha256  text NULL
);

CREATE TABLE IF NOT EXISTS snapshot_file_versions (
    id                  bigserial PRIMARY KEY,
    snapshot_id         bigint NOT NULL REFERENCES snapshots(id) ON DELETE CASCADE,
    relative_path       text NOT NULL,
    file_version_id     bigint NOT NULL,
    content_hash_sha256 text NOT NULL,
    size_bytes          bigint NOT NULL,
    is_deletion_marker  boolean NOT NULL,
    created_at          timestamptz NOT NULL,
    blocks_json         jsonb NOT NULL DEFAULT '[]'::jsonb
);

CREATE TABLE IF NOT EXISTS sync_idempotency_keys (
    id                     bigserial PRIMARY KEY,
    user_id                bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    external_repository_id integer NOT NULL,
    idempotency_key        text NOT NULL,
    request_sha256         text NOT NULL,
    state                  text NOT NULL DEFAULT 'in_progress',
    status_code            integer NULL,
    response_json          jsonb NULL,
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, external_repository_id, idempotency_key)
);

CREATE INDEX IF NOT EXISTS ix_sync_idempotency_created
    ON sync_idempotency_keys(created_at DESC);

ALTER TABLE repositories
    ADD COLUMN IF NOT EXISTS description text NULL;

ALTER TABLE snapshots
    ADD COLUMN IF NOT EXISTS title text NULL,
    ADD COLUMN IF NOT EXISTS trigger text NOT NULL DEFAULT '';

ALTER TABLE snapshots
    ADD COLUMN IF NOT EXISTS total_entries integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS file_entries integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS directory_entries integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS total_file_bytes bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS payload_sha256 text NULL,
    ADD COLUMN IF NOT EXISTS inserted_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE snapshot_entries
    ADD COLUMN IF NOT EXISTS parent_relative_path text NULL,
    ADD COLUMN IF NOT EXISTS extension text NULL,
    ADD COLUMN IF NOT EXISTS size_bytes bigint NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS last_write_utc timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS content_hash_sha256 text NULL;

ALTER TABLE snapshot_file_versions
    ADD COLUMN IF NOT EXISTS blocks_json jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE INDEX IF NOT EXISTS ix_repositories_user_updated
    ON repositories(user_id, updated_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_snapshots_repo_created
    ON snapshots(repository_id, created_at DESC, id DESC);

CREATE INDEX IF NOT EXISTS ix_snapshot_entries_snapshot_relpath
    ON snapshot_entries(snapshot_id, relative_path);

CREATE INDEX IF NOT EXISTS ix_snapshot_file_versions_snapshot_relpath
    ON snapshot_file_versions(snapshot_id, relative_path);

CREATE INDEX IF NOT EXISTS ix_snapshot_file_versions_snapshot_version
    ON snapshot_file_versions(snapshot_id, file_version_id);

CREATE TABLE IF NOT EXISTS schema_migrations (
    version bigint PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now()
);

INSERT INTO schema_migrations(version)
VALUES (1)
ON CONFLICT (version) DO NOTHING;

ALTER TABLE users
    ADD COLUMN IF NOT EXISTS email text NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_users_email_ci
    ON users (lower(email))
    WHERE email IS NOT NULL;

CREATE TABLE IF NOT EXISTS devices (
    id                 bigserial PRIMARY KEY,
    user_id            bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_fingerprint text NOT NULL,
    device_name        text NULL,
    platform           text NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_seen_at       timestamptz NOT NULL DEFAULT now(),
    UNIQUE(user_id, device_fingerprint)
);

CREATE TABLE IF NOT EXISTS user_sessions (
    id                 bigserial PRIMARY KEY,
    user_id            bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    device_id          bigint NULL REFERENCES devices(id) ON DELETE SET NULL,
    refresh_token_hash text NOT NULL,
    access_expires_at  timestamptz NOT NULL DEFAULT now(),
    refresh_expires_at timestamptz NOT NULL DEFAULT now(),
    revoked_at         timestamptz NULL,
    revoke_reason      text NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    last_used_at       timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE devices
    ADD COLUMN IF NOT EXISTS device_name text NULL,
    ADD COLUMN IF NOT EXISTS platform text NULL,
    ADD COLUMN IF NOT EXISTS last_seen_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE user_sessions
    ADD COLUMN IF NOT EXISTS device_id bigint NULL REFERENCES devices(id) ON DELETE SET NULL,
    ADD COLUMN IF NOT EXISTS refresh_token_hash text NULL,
    ADD COLUMN IF NOT EXISTS access_expires_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS refresh_expires_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS revoked_at timestamptz NULL,
    ADD COLUMN IF NOT EXISTS revoke_reason text NULL,
    ADD COLUMN IF NOT EXISTS created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS last_used_at timestamptz NOT NULL DEFAULT now();

UPDATE user_sessions
SET refresh_token_hash = md5(id::text || '-' || user_id::text || '-' || now()::text)
WHERE refresh_token_hash IS NULL OR btrim(refresh_token_hash) = '';

ALTER TABLE user_sessions
    ALTER COLUMN refresh_token_hash SET NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_user_sessions_refresh_hash
    ON user_sessions(refresh_token_hash);

CREATE INDEX IF NOT EXISTS ix_user_sessions_user_active
    ON user_sessions(user_id, revoked_at, refresh_expires_at DESC, id DESC);
