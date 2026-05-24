# RUIAN Feed Parser

.NET console tool for importing and incrementally updating RUIAN
(Registr územní identifikace, adres a nemovitostí) address data from ČÚZK ATOM feeds
into a local SQLite database.

## Prerequisites

- .NET 10 SDK or Runtime
- SQLite system library:
  - **Linux**: `sudo apt install libsqlite3-dev`
    + symlink beside binary: `ln -s /usr/lib/x86_64-linux-gnu/libsqlite3.so.0 ./out/libsqlite3.so`
  - **Windows**: `sqlite3.dll` in PATH or beside the executable
  - **macOS**: ships with Xcode CLT (`xcode-select --install`)

## Build

```bash
dotnet build
dotnet run -- help
# publish
dotnet publish -c Release -o ./out
```

## Typical workflow

```bash
# Initial load — whole Czech Republic, ~3.5M addresses
ruian-parser import-ruian --state

# Daily incremental update (add to cron / Task Scheduler)
ruian-parser update-ruian

# Force-reapply last 7 days if something went wrong
ruian-parser update-ruian --days 7 --force
```

## Commands

```
fetch-feed <url>
    Fetch any ATOM feed into DB. No file downloads. Works with any ATOM 1.0 feed.

import <url> [--force]
    Full CSV pipeline: fetch feed -> download -> validate -> parse -> insert.
    Skips files already in import_log (by content hash). --force overrides.

import-ruian [--state | --municipalities] [--municipality <name>] [--force]
    Initial load from standard ČÚZK CSV address feeds.
    --state            Whole-state CSV, ~150 MB compressed (default)
    --municipalities   Per-municipality CSVs
    --municipality     Filter by name (use with --municipalities)
    --force            Re-import even if content hash matches

update-ruian [--days <n>] [--full] [--force]
    Incremental update from RUIAN VFR změnová feed.
    Downloads only delta files not yet in import_log.
    Upserts changed addresses. Deletes addresses with PlatiDo set in VFR.
    --days <n>    Apply only last n daily deltas
    --full        Use monthly VFR full basic snapshot instead of daily deltas
    --force       Reapply already-processed files

view-feed [--url <feed_url>] [--limit <n>]
view-addr --municipality <n> | --street <n> | --postal <code> [--limit <n>]
stats
    --db <path>     Custom SQLite file location
    --verbose       Debug-level logging (note: must come after the command)
```

## Architecture

```
Program.cs                   CLI dispatcher
src/
  Models/
    FeedEntry.cs             Generic ATOM entry model
    RuianAddress.cs          RUIAN adresní místo (19 CSV fields)
  Parsers/
    AtomFeedParser.cs        Generic ATOM 1.0 XML parser
    RuianCsvParser.cs        Streaming CSV parser
                               - Windows-1250 encoding, semicolon-delimited
                               - Flexible column count (warns + continues on mismatch)
                               - Per-line error handling (bad line = warn + skip, not abort)
    VfrParser.cs             Streaming VFR/GML parser
                               - AdresniMisto elements only
                               - PlatiDo present + non-empty = deletion
                               - GZip auto-detection (magic bytes)
                               - ParseZip() materializes records before ZipArchive disposal
                               - Per-element error handling
  Data/
    Sqlite3.cs               P/Invoke wrapper for system libsqlite3 (zero NuGet deps)
    SchemaVersion.cs         Migration runner
                               - Append-only migration list
                               - Tolerates duplicate column errors (safe upgrades)
                               - Each migration runs in a transaction
    Database.cs              Thread-safe SQLite layer
                               - C# lock + SQLITE_OPEN_FULLMUTEX (double guard)
                               - BulkInsertAddresses: 5k-row batch transactions
                               - ApplyVfrChanges: UPDATE existing / INSERT new (no COALESCE subquery soup)
                               - IsAlreadyImported: hash-first, filename-fallback
                               - DeleteAddressBatch: parameterized IN clause
  Services/
    Logger.cs                Timestamped console logger (Debug/Info/Warn/Error)
    ThrottledDownloader.cs   Async HTTP with:
                               - Exponential backoff retry (5 attempts, max 60s delay)
                               - Non-retryable 4xx detection
                               - SHA-256 hash of each download
                               - File integrity check (magic bytes: ZIP / GZip / XML)
                               - Write-to-.tmp-then-rename (no corrupt partial files)
                               - SemaphoreSlim throttling + inter-request delay
    RuianImportService.cs    Import orchestrator
                               - Hash-based duplicate detection (not just filename)
                               - Oldest-first ordering for VFR deltas
                               - Temp file cleanup on success, error, and cancellation
```

## Database schema

| Table | Key columns |
|---|---|
| `ruian_addresses` | `adm_code` (PK), full address fields, coordinates |
| `feed_entries` | `(id, feed_url)` (PK), ATOM metadata |
| `import_log` | `source_file`, `file_hash`, `rows_inserted`, `rows_deleted`, `status` |
| `schema_version` | tracks applied migrations |

## Notes on upgrading from pre-migration installs

If you have a DB created before migration support was added, migration v2 runs automatically
on first start and adds the missing `file_hash` and `rows_deleted` columns to `import_log`.
Existing data is preserved.

## Notes on VFR and deletions

VFR change records carry codes but not text names. The upsert strategy:
- Existing row → UPDATE numeric fields, preserve names already in DB
- New row (adm_code not in DB) → INSERT with empty name fields

Empty names fill in on the next full CSV import. For most production setups (full CSV load
done once, then daily VFR deltas) this only affects brand-new addresses added since the
last CSV import — a small fraction of daily changes.
