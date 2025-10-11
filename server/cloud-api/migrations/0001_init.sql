CREATE TABLE IF NOT EXISTS users (
                                     id            bigserial PRIMARY KEY,
                                     username      text NOT NULL UNIQUE,
                                     password_hash text NOT NULL,
                                     created_at    timestamptz NOT NULL DEFAULT now()
    );

CREATE TABLE IF NOT EXISTS schema_migrations (
                                                 version bigint PRIMARY KEY,
                                                 applied_at timestamptz NOT NULL DEFAULT now()
    );

INSERT INTO schema_migrations(version)
VALUES (1)
    ON CONFLICT (version) DO NOTHING;
