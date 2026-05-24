using Microsoft.Data.Sqlite;
using RuianFeedParser.Models;
using RuianFeedParser.Parsers;

namespace RuianFeedParser.Data;

/// <summary>
/// SQLite persistence layer using Microsoft.Data.Sqlite (ADO.NET).
///
/// Why the switch from P/Invoke:
/// The manual sqlite3_* P/Invoke approach required precise lifetime management of
/// statement handles (IntPtr). Three separate crashes showed that async continuations
/// firing on thread-pool threads were reaching SQLite calls while handles from a
/// previous operation were in an inconsistent state. Microsoft.Data.Sqlite wraps all
/// of this correctly — statement handles are managed objects, connections are pooled
/// safely, and there are no IntPtr values to double-free.
///
/// Thread safety: SqliteConnection is not thread-safe, so all DB access goes through
/// a single connection protected by a SemaphoreSlim. SemaphoreSlim is used instead
/// of lock() because async callers need to await entry without blocking a thread.
/// </summary>
public sealed class Database : IAsyncDisposable, IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Database(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Cache      = SqliteCacheMode.Shared
        };

        _conn = new SqliteConnection(builder.ToString());
        _conn.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA synchronous=NORMAL");
        Execute("PRAGMA temp_store=MEMORY");
        Execute("PRAGMA mmap_size=268435456");
        Execute("PRAGMA cache_size=-32000");
        Execute("PRAGMA foreign_keys=ON");

        SchemaVersion.Apply(_conn);
    }

    // ── Feed entries ──────────────────────────────────────────────────────────

    public int UpsertFeedEntries(IEnumerable<FeedEntry> entries, string feedUrl)
    {
        const string sql = """
            INSERT INTO feed_entries
                (id, feed_url, title, summary, download_url, alternate_url,
                 updated_at, author, category, file_size, media_type)
            VALUES (@id,@feedUrl,@title,@summary,@dl,@alt,@updated,@author,@cat,@size,@mt)
            ON CONFLICT(id, feed_url) DO UPDATE SET
                title        = excluded.title,
                summary      = excluded.summary,
                download_url = excluded.download_url,
                updated_at   = excluded.updated_at,
                file_size    = excluded.file_size
            """;

        _gate.Wait();
        try
        {
            int count = 0;
            using var tx  = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;

            var pId      = cmd.Parameters.Add("@id",      SqliteType.Text);
            var pFeedUrl = cmd.Parameters.Add("@feedUrl", SqliteType.Text);
            var pTitle   = cmd.Parameters.Add("@title",   SqliteType.Text);
            var pSummary = cmd.Parameters.Add("@summary", SqliteType.Text);
            var pDl      = cmd.Parameters.Add("@dl",      SqliteType.Text);
            var pAlt     = cmd.Parameters.Add("@alt",     SqliteType.Text);
            var pUpdated = cmd.Parameters.Add("@updated", SqliteType.Text);
            var pAuthor  = cmd.Parameters.Add("@author",  SqliteType.Text);
            var pCat     = cmd.Parameters.Add("@cat",     SqliteType.Text);
            var pSize    = cmd.Parameters.Add("@size",    SqliteType.Integer);
            var pMt      = cmd.Parameters.Add("@mt",      SqliteType.Text);

            pFeedUrl.Value = feedUrl;

            foreach (var e in entries)
            {
                pId.Value      = e.Id;
                pTitle.Value   = e.Title;
                pSummary.Value = (object?)e.Summary  ?? DBNull.Value;
                pDl.Value      = (object?)e.DownloadUrl  ?? DBNull.Value;
                pAlt.Value     = (object?)e.AlternateUrl ?? DBNull.Value;
                pUpdated.Value = e.Updated.ToString("O");
                pAuthor.Value  = (object?)e.Author   ?? DBNull.Value;
                pCat.Value     = (object?)e.Category ?? DBNull.Value;
                pSize.Value    = (object?)e.FileSizeBytes ?? DBNull.Value;
                pMt.Value      = (object?)e.MediaType    ?? DBNull.Value;
                cmd.ExecuteNonQuery();
                count++;
            }

            tx.Commit();
            return count;
        }
        finally { _gate.Release(); }
    }

    public List<FeedEntry> GetFeedEntries(string? feedUrl = null, int limit = 100)
    {
        var sql = feedUrl == null
            ? $"SELECT id,feed_url,title,summary,download_url,alternate_url,updated_at,author,category,file_size,media_type FROM feed_entries ORDER BY updated_at DESC LIMIT {limit}"
            : $"SELECT id,feed_url,title,summary,download_url,alternate_url,updated_at,author,category,file_size,media_type FROM feed_entries WHERE feed_url=@url ORDER BY updated_at DESC LIMIT {limit}";

        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            if (feedUrl != null) cmd.Parameters.AddWithValue("@url", feedUrl);

            var result = new List<FeedEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new FeedEntry
                {
                    Id            = reader.GetString(0),
                    Title         = reader.GetString(2),
                    Summary       = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    DownloadUrl   = reader.IsDBNull(4) ? null : reader.GetString(4),
                    AlternateUrl  = reader.IsDBNull(5) ? null : reader.GetString(5),
                    Updated       = reader.IsDBNull(6) ? DateTime.MinValue : DateTime.Parse(reader.GetString(6)),
                    Author        = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Category      = reader.IsDBNull(8) ? null : reader.GetString(8),
                    FileSizeBytes = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    MediaType     = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    // ── Import log ────────────────────────────────────────────────────────────

    public long StartImport(string sourceFile, string? feedUrl, string? fileHash = null)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO import_log (source_file, file_hash, feed_url, started_at)
                VALUES (@f, @h, @u, datetime('now'));
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@f", sourceFile);
            cmd.Parameters.AddWithValue("@h", (object?)fileHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@u", (object?)feedUrl  ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
        finally { _gate.Release(); }
    }

    public void FinishImport(long importId, int rowsInserted, int rowsDeleted = 0, string status = "ok")
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE import_log
                SET finished_at=datetime('now'), rows_inserted=@ri, rows_deleted=@rd, status=@s
                WHERE id=@id
                """;
            cmd.Parameters.AddWithValue("@ri", rowsInserted);
            cmd.Parameters.AddWithValue("@rd", rowsDeleted);
            cmd.Parameters.AddWithValue("@s",  status);
            cmd.Parameters.AddWithValue("@id", importId);
            cmd.ExecuteNonQuery();
        }
        finally { _gate.Release(); }
    }

    public bool IsAlreadyImported(string sourceFile, string? fileHash = null)
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            if (fileHash != null)
            {
                cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE file_hash=@v AND status='ok' LIMIT 1";
                cmd.Parameters.AddWithValue("@v", fileHash);
            }
            else
            {
                cmd.CommandText = "SELECT COUNT(*) FROM import_log WHERE source_file=@v AND status='ok' LIMIT 1";
                cmd.Parameters.AddWithValue("@v", sourceFile);
            }
            return (long)cmd.ExecuteScalar()! > 0;
        }
        finally { _gate.Release(); }
    }

    // ── RUIAN addresses — CSV bulk insert ─────────────────────────────────────

    public int BulkInsertAddresses(
        IEnumerable<RuianAddress> addresses,
        int batchSize = 5000,
        Action<int>? progress = null,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT OR REPLACE INTO ruian_addresses
                (adm_code,municipality_code,municipality_name,
                 momc_code,momc_name,mop_code,mop_name,
                 part_code,part_name,street_code,street_name,
                 building_type,house_number,orientation_number,orientation_char,
                 postal_code,coord_y,coord_x,valid_from,source_file,imported_at)
            VALUES (@adm,@mc,@mn,@momc_c,@momc_n,@mop_c,@mop_n,
                    @pc,@pn,@sc,@sn,@bt,@hn,@on,@oc,@psc,@cy,@cx,@vf,@sf,@ia)
            """;

        int total = 0;
        var batch = new List<RuianAddress>(batchSize);

        foreach (var addr in addresses)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(addr);
            if (batch.Count >= batchSize)
            {
                total += FlushCsvBatch(batch, sql);
                progress?.Invoke(total);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            total += FlushCsvBatch(batch, sql);
            progress?.Invoke(total);
        }
        return total;
    }

    private int FlushCsvBatch(List<RuianAddress> batch, string sql)
    {
        _gate.Wait();
        try
        {
            using var tx  = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;

            var pAdm   = cmd.Parameters.Add("@adm",    SqliteType.Integer);
            var pMc    = cmd.Parameters.Add("@mc",     SqliteType.Integer);
            var pMn    = cmd.Parameters.Add("@mn",     SqliteType.Text);
            var pMomcC = cmd.Parameters.Add("@momc_c", SqliteType.Integer);
            var pMomcN = cmd.Parameters.Add("@momc_n", SqliteType.Text);
            var pMopC  = cmd.Parameters.Add("@mop_c",  SqliteType.Integer);
            var pMopN  = cmd.Parameters.Add("@mop_n",  SqliteType.Text);
            var pPc    = cmd.Parameters.Add("@pc",     SqliteType.Integer);
            var pPn    = cmd.Parameters.Add("@pn",     SqliteType.Text);
            var pSc    = cmd.Parameters.Add("@sc",     SqliteType.Integer);
            var pSn    = cmd.Parameters.Add("@sn",     SqliteType.Text);
            var pBt    = cmd.Parameters.Add("@bt",     SqliteType.Text);
            var pHn    = cmd.Parameters.Add("@hn",     SqliteType.Integer);
            var pOn    = cmd.Parameters.Add("@on",     SqliteType.Integer);
            var pOc    = cmd.Parameters.Add("@oc",     SqliteType.Text);
            var pPsc   = cmd.Parameters.Add("@psc",    SqliteType.Text);
            var pCy    = cmd.Parameters.Add("@cy",     SqliteType.Real);
            var pCx    = cmd.Parameters.Add("@cx",     SqliteType.Real);
            var pVf    = cmd.Parameters.Add("@vf",     SqliteType.Text);
            var pSf    = cmd.Parameters.Add("@sf",     SqliteType.Text);
            var pIa    = cmd.Parameters.Add("@ia",     SqliteType.Text);

            foreach (var a in batch)
            {
                pAdm.Value   = a.AdmCode;
                pMc.Value    = a.MunicipalityCode;
                pMn.Value    = a.MunicipalityName;
                pMomcC.Value = (object?)a.MomcCode  ?? DBNull.Value;
                pMomcN.Value = (object?)a.MomcName  ?? DBNull.Value;
                pMopC.Value  = (object?)a.MopCode   ?? DBNull.Value;
                pMopN.Value  = (object?)a.MopName   ?? DBNull.Value;
                pPc.Value    = a.PartCode;
                pPn.Value    = a.PartName;
                pSc.Value    = (object?)a.StreetCode ?? DBNull.Value;
                pSn.Value    = (object?)a.StreetName ?? DBNull.Value;
                pBt.Value    = a.BuildingType;
                pHn.Value    = a.HouseNumber;
                pOn.Value    = (object?)a.OrientationNumber    ?? DBNull.Value;
                pOc.Value    = (object?)a.OrientationNumberChar ?? DBNull.Value;
                pPsc.Value   = a.PostalCode;
                pCy.Value    = (object?)a.CoordinateY  ?? DBNull.Value;
                pCx.Value    = (object?)a.CoordinateX  ?? DBNull.Value;
                pVf.Value    = (object?)a.ValidFrom?.ToString("yyyy-MM-dd") ?? DBNull.Value;
                pSf.Value    = a.SourceFile;
                pIa.Value    = a.ImportedAt.ToString("O");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return batch.Count;
        }
        finally { _gate.Release(); }
    }

    // ── RUIAN addresses — VFR incremental update ──────────────────────────────

    public (int upserted, int deleted) ApplyVfrChanges(
        IEnumerable<VfrAddressRecord> records,
        int batchSize = 5000,
        Action<int, int>? progress = null,
        CancellationToken ct = default)
    {
        int totalUpserted = 0;
        int totalDeleted  = 0;

        var upsertBatch = new List<VfrAddressRecord>(batchSize);
        var deleteBatch = new List<long>(batchSize);

        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            if (r.IsDeleted)
            {
                deleteBatch.Add(r.AdmCode);
                if (deleteBatch.Count >= batchSize)
                {
                    totalDeleted += FlushDeleteBatch(deleteBatch);
                    deleteBatch.Clear();
                    progress?.Invoke(totalUpserted, totalDeleted);
                }
            }
            else
            {
                upsertBatch.Add(r);
                if (upsertBatch.Count >= batchSize)
                {
                    totalUpserted += FlushVfrUpsertBatch(upsertBatch);
                    upsertBatch.Clear();
                    progress?.Invoke(totalUpserted, totalDeleted);
                }
            }
        }

        if (upsertBatch.Count > 0)
        {
            totalUpserted += FlushVfrUpsertBatch(upsertBatch);
            progress?.Invoke(totalUpserted, totalDeleted);
        }
        if (deleteBatch.Count > 0)
        {
            totalDeleted += FlushDeleteBatch(deleteBatch);
            progress?.Invoke(totalUpserted, totalDeleted);
        }

        return (totalUpserted, totalDeleted);
    }

    private int FlushVfrUpsertBatch(List<VfrAddressRecord> batch)
    {
        const string sqlUpdate = """
            UPDATE ruian_addresses SET
                municipality_code  = @mc,
                momc_code          = @momc_c,
                part_code          = @pc,
                street_code        = @sc,
                building_type      = @bt,
                house_number       = @hn,
                orientation_number = @on,
                orientation_char   = @oc,
                postal_code        = @psc,
                coord_y            = @cy,
                coord_x            = @cx,
                valid_from         = @vf,
                source_file        = @sf,
                imported_at        = datetime('now')
            WHERE adm_code = @adm
            """;

        const string sqlInsert = """
            INSERT OR IGNORE INTO ruian_addresses
                (adm_code, municipality_code, municipality_name,
                 momc_code, momc_name, mop_code, mop_name,
                 part_code, part_name, street_code, street_name,
                 building_type, house_number, orientation_number, orientation_char,
                 postal_code, coord_y, coord_x, valid_from, source_file, imported_at)
            VALUES (@adm, @mc, '', @momc_c, NULL, NULL, NULL,
                    @pc, '', @sc, NULL,
                    @bt, @hn, @on, @oc, @psc, @cy, @cx, @vf, @sf, datetime('now'))
            """;

        _gate.Wait();
        try
        {
            using var tx      = _conn.BeginTransaction();
            using var cmdUpd  = _conn.CreateCommand();
            using var cmdIns  = _conn.CreateCommand();
            cmdUpd.Transaction = tx;
            cmdIns.Transaction = tx;
            cmdUpd.CommandText = sqlUpdate;
            cmdIns.CommandText = sqlInsert;

            // Update parameters
            var uAdm   = cmdUpd.Parameters.Add("@adm",    SqliteType.Integer);
            var uMc    = cmdUpd.Parameters.Add("@mc",     SqliteType.Integer);
            var uMomcC = cmdUpd.Parameters.Add("@momc_c", SqliteType.Integer);
            var uPc    = cmdUpd.Parameters.Add("@pc",     SqliteType.Integer);
            var uSc    = cmdUpd.Parameters.Add("@sc",     SqliteType.Integer);
            var uBt    = cmdUpd.Parameters.Add("@bt",     SqliteType.Text);
            var uHn    = cmdUpd.Parameters.Add("@hn",     SqliteType.Integer);
            var uOn    = cmdUpd.Parameters.Add("@on",     SqliteType.Integer);
            var uOc    = cmdUpd.Parameters.Add("@oc",     SqliteType.Text);
            var uPsc   = cmdUpd.Parameters.Add("@psc",    SqliteType.Text);
            var uCy    = cmdUpd.Parameters.Add("@cy",     SqliteType.Real);
            var uCx    = cmdUpd.Parameters.Add("@cx",     SqliteType.Real);
            var uVf    = cmdUpd.Parameters.Add("@vf",     SqliteType.Text);
            var uSf    = cmdUpd.Parameters.Add("@sf",     SqliteType.Text);

            // Insert parameters (same names — SqliteCommand keeps them separate)
            var iAdm   = cmdIns.Parameters.Add("@adm",    SqliteType.Integer);
            var iMc    = cmdIns.Parameters.Add("@mc",     SqliteType.Integer);
            var iMomcC = cmdIns.Parameters.Add("@momc_c", SqliteType.Integer);
            var iPc    = cmdIns.Parameters.Add("@pc",     SqliteType.Integer);
            var iSc    = cmdIns.Parameters.Add("@sc",     SqliteType.Integer);
            var iBt    = cmdIns.Parameters.Add("@bt",     SqliteType.Text);
            var iHn    = cmdIns.Parameters.Add("@hn",     SqliteType.Integer);
            var iOn    = cmdIns.Parameters.Add("@on",     SqliteType.Integer);
            var iOc    = cmdIns.Parameters.Add("@oc",     SqliteType.Text);
            var iPsc   = cmdIns.Parameters.Add("@psc",    SqliteType.Text);
            var iCy    = cmdIns.Parameters.Add("@cy",     SqliteType.Real);
            var iCx    = cmdIns.Parameters.Add("@cx",     SqliteType.Real);
            var iVf    = cmdIns.Parameters.Add("@vf",     SqliteType.Text);
            var iSf    = cmdIns.Parameters.Add("@sf",     SqliteType.Text);

            foreach (var r in batch)
            {
                // Bind update
                uAdm.Value   = r.AdmCode;
                uMc.Value    = r.MunicipalityCode;
                uMomcC.Value = (object?)r.MomcCode    ?? DBNull.Value;
                uPc.Value    = r.PartCode;
                uSc.Value    = (object?)r.StreetCode  ?? DBNull.Value;
                uBt.Value    = r.BuildingType;
                uHn.Value    = r.HouseNumber;
                uOn.Value    = (object?)r.OrientationNumber     ?? DBNull.Value;
                uOc.Value    = (object?)r.OrientationNumberChar ?? DBNull.Value;
                uPsc.Value   = r.PostalCode;
                uCy.Value    = (object?)r.CoordinateY ?? DBNull.Value;
                uCx.Value    = (object?)r.CoordinateX ?? DBNull.Value;
                uVf.Value    = (object?)r.ValidFrom?.ToString("yyyy-MM-dd") ?? DBNull.Value;
                uSf.Value    = r.SourceFile;
                int affected = cmdUpd.ExecuteNonQuery();

                if (affected == 0)
                {
                    // New address — INSERT with blank names, filled on next CSV load
                    iAdm.Value   = r.AdmCode;
                    iMc.Value    = r.MunicipalityCode;
                    iMomcC.Value = (object?)r.MomcCode    ?? DBNull.Value;
                    iPc.Value    = r.PartCode;
                    iSc.Value    = (object?)r.StreetCode  ?? DBNull.Value;
                    iBt.Value    = r.BuildingType;
                    iHn.Value    = r.HouseNumber;
                    iOn.Value    = (object?)r.OrientationNumber     ?? DBNull.Value;
                    iOc.Value    = (object?)r.OrientationNumberChar ?? DBNull.Value;
                    iPsc.Value   = r.PostalCode;
                    iCy.Value    = (object?)r.CoordinateY ?? DBNull.Value;
                    iCx.Value    = (object?)r.CoordinateX ?? DBNull.Value;
                    iVf.Value    = (object?)r.ValidFrom?.ToString("yyyy-MM-dd") ?? DBNull.Value;
                    iSf.Value    = r.SourceFile;
                    cmdIns.ExecuteNonQuery();
                }
            }

            tx.Commit();
            return batch.Count;
        }
        finally { _gate.Release(); }
    }

    private int FlushDeleteBatch(List<long> admCodes)
    {
        if (admCodes.Count == 0) return 0;

        var placeholders = string.Join(",", admCodes.Select((_, i) => $"@p{i}"));
        var sql = $"DELETE FROM ruian_addresses WHERE adm_code IN ({placeholders})";

        _gate.Wait();
        try
        {
            using var tx  = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            for (int i = 0; i < admCodes.Count; i++)
                cmd.Parameters.AddWithValue($"@p{i}", admCodes[i]);
            cmd.ExecuteNonQuery();
            tx.Commit();
            return admCodes.Count;
        }
        finally { _gate.Release(); }
    }

    // ── Query ─────────────────────────────────────────────────────────────────

    public List<Dictionary<string, object?>> QueryAddresses(
        string? municipality = null,
        string? street = null,
        string? postalCode = null,
        int limit = 50)
    {
        var conditions = new List<string>();

        if (municipality != null) conditions.Add("municipality_name LIKE @mun");
        if (street != null)       conditions.Add("street_name LIKE @str");
        if (postalCode != null)   conditions.Add("postal_code = @psc");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var sql = $"""
            SELECT adm_code, municipality_name, part_name, street_name,
                   building_type, house_number, orientation_number, orientation_char,
                   postal_code, coord_y, coord_x, valid_from
            FROM ruian_addresses {where}
            ORDER BY municipality_name, street_name, house_number
            LIMIT {limit}
            """;

        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            if (municipality != null) cmd.Parameters.AddWithValue("@mun", $"%{municipality}%");
            if (street != null)       cmd.Parameters.AddWithValue("@str", $"%{street}%");
            if (postalCode != null)   cmd.Parameters.AddWithValue("@psc", postalCode.Replace(" ", ""));

            var result = new List<Dictionary<string, object?>>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (int i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                result.Add(row);
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    public (long addresses, long feeds, long imports) GetStats()
    {
        _gate.Wait();
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM ruian_addresses),
                    (SELECT COUNT(*) FROM feed_entries),
                    (SELECT COUNT(*) FROM import_log WHERE status='ok')
                """;
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }
        finally { _gate.Release(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _gate.Dispose();
        _conn.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _gate.Dispose();
        await _conn.DisposeAsync();
    }
}
