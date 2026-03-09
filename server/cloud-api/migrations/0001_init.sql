CREATE TABLE IF NOT EXISTS users (
    id            bigserial PRIMARY KEY,
    username      text NOT NULL UNIQUE,
    password_hash text NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT now()
);

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

CREATE TABLE IF NOT EXISTS cloud_blocks (
    block_hash   text PRIMARY KEY,
    length_bytes integer NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now()
);

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

