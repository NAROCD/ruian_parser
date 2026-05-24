using Microsoft.Data.Sqlite;

namespace RuianFeedParser.Data;

/// <summary>
/// Schema versioning and migration runner.
/// Each migration is a plain SQL string applied exactly once, in a transaction.
/// Append-only — never edit or remove an existing entry.
/// </summary>
internal static class SchemaVersion
{
    private static readonly (int version, string sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER PRIMARY KEY,
                applied_at  TEXT NOT NULL DEFAULT (datetime('now')),
                description TEXT
            );

            CREATE TABLE IF NOT EXISTS feed_entries (
                id            TEXT NOT NULL,
                feed_url      TEXT NOT NULL,
                title         TEXT NOT NULL,
                summary       TEXT,
                download_url  TEXT,
                alternate_url TEXT,
                updated_at    TEXT,
                author        TEXT,
                category      TEXT,
                file_size     INTEGER,
                media_type    TEXT,
                fetched_at    TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (id, feed_url)
            );

            CREATE TABLE IF NOT EXISTS ruian_addresses (
                adm_code            INTEGER PRIMARY KEY,
                municipality_code   INTEGER NOT NULL,
                municipality_name   TEXT NOT NULL,
                momc_code           INTEGER,
                momc_name           TEXT,
                mop_code            INTEGER,
                mop_name            TEXT,
                part_code           INTEGER NOT NULL,
                part_name           TEXT NOT NULL,
                street_code         INTEGER,
                street_name         TEXT,
                building_type       TEXT,
                house_number        INTEGER NOT NULL,
                orientation_number  INTEGER,
                orientation_char    TEXT,
                postal_code         TEXT,
                coord_y             REAL,
                coord_x             REAL,
                valid_from          TEXT,
                source_file         TEXT,
                imported_at         TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS import_log (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                source_file   TEXT NOT NULL,
                file_hash     TEXT,
                feed_url      TEXT,
                started_at    TEXT NOT NULL,
                finished_at   TEXT,
                rows_inserted INTEGER,
                rows_deleted  INTEGER,
                status        TEXT NOT NULL DEFAULT 'running'
            );

            CREATE INDEX IF NOT EXISTS idx_addr_municipality ON ruian_addresses(municipality_code);
            CREATE INDEX IF NOT EXISTS idx_addr_postal       ON ruian_addresses(postal_code);
            CREATE INDEX IF NOT EXISTS idx_addr_street       ON ruian_addresses(street_code);
            CREATE INDEX IF NOT EXISTS idx_feed_updated      ON feed_entries(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_import_file       ON import_log(source_file, file_hash);
            """),

        // v2: add columns missing in pre-migration databases.
        // Each ALTER TABLE is run individually; "duplicate column name" errors are swallowed
        // so this is safe to run against both old and new databases.
        (2, """
            ALTER TABLE import_log ADD COLUMN file_hash TEXT;
            ALTER TABLE import_log ADD COLUMN rows_deleted INTEGER;
            CREATE INDEX IF NOT EXISTS idx_import_file ON import_log(source_file, file_hash);
            """),
    ];

    public static void Apply(SqliteConnection conn)
    {
        // Bootstrap: schema_version table must exist before we can query it
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_version (
                    version     INTEGER PRIMARY KEY,
                    applied_at  TEXT NOT NULL DEFAULT (datetime('now')),
                    description TEXT
                );
                """;
            cmd.ExecuteNonQuery();
        }

        int current = GetCurrentVersion(conn);

        foreach (var (version, sql) in Migrations)
        {
            if (version <= current) continue;
            ApplyMigration(conn, version, sql);
        }
    }

    private static int GetCurrentVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void ApplyMigration(SqliteConnection conn, int version, string sql)
    {
        using var tx = conn.BeginTransaction();
        try
        {
            // Run each semicolon-delimited statement individually so ALTER TABLE
            // "duplicate column name" errors can be swallowed without aborting the migration
            var statements = sql
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => s.Length > 0);

            foreach (var stmt in statements)
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = stmt;
                    cmd.ExecuteNonQuery();
                }
                catch (SqliteException ex) when (ex.Message.Contains("duplicate column name",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Column already exists — safe to continue
                }
            }

            using var versionCmd = conn.CreateCommand();
            versionCmd.Transaction = tx;
            versionCmd.CommandText = "INSERT INTO schema_version (version) VALUES (@v)";
            versionCmd.Parameters.AddWithValue("@v", version);
            versionCmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
