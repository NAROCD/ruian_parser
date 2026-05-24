using System.Text;
using RuianFeedParser.Models;
using RuianFeedParser.Services;

namespace RuianFeedParser.Parsers;

/// <summary>
/// Streaming parser for RUIAN CSV files (adresní místa).
/// ČÚZK publishes these as Windows-1250, semicolon-delimited.
///
/// Column layout (as of current ČÚZK spec):
/// [0]  Kód ADM
/// [1]  Kód obce           [2]  Název obce
/// [3]  Kód MOMC           [4]  Název MOMC
/// [5]  Kód MOP            [6]  Název MOP
/// [7]  Kód části obce     [8]  Název části obce
/// [9]  Kód ulice          [10] Název ulice
/// [11] Typ SO
/// [12] Číslo domovní      [13] Číslo orientační   [14] Znak č.o.
/// [15] PSČ
/// [16] Souřadnice Y       [17] Souřadnice X
/// [18] Platí Od
///
/// Resilience: if ČÚZK adds columns (has happened before), we log a warning
/// and keep parsing with what we have. Hard minimum is 19 columns.
/// </summary>
public static class RuianCsvParser
{
    private const int MinColumns = 19;
    private static readonly Logger Log = new("RuianCsvParser");
    private static readonly Encoding Win1250;

    static RuianCsvParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Win1250 = Encoding.GetEncoding(1250);
    }

    public static IEnumerable<RuianAddress> ParseStream(
        Stream csvStream,
        string sourceFile,
        CancellationToken ct = default)
    {
        using var reader = new StreamReader(csvStream, Win1250,
            detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var header = reader.ReadLine();
        if (header == null) { Log.Warn($"{sourceFile}: empty file"); yield break; }

        // Detect if ČÚZK changed the column layout
        var headerCols = header.Split(';');
        if (headerCols.Length != MinColumns)
            Log.Warn($"{sourceFile}: expected {MinColumns} columns, got {headerCols.Length} — parsing may be incomplete");

        var importedAt = DateTime.UtcNow;
        string? line;
        int lineNumber    = 1;
        int parseErrors   = 0;
        const int maxLoggedErrors = 10; // Don't flood logs on a broken file

        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line)) continue;

            RuianAddress? address = null;
            try
            {
                address = ParseLine(line, sourceFile, importedAt);
            }
            catch (Exception ex)
            {
                parseErrors++;
                if (parseErrors <= maxLoggedErrors)
                    Log.Warn($"{sourceFile}:{lineNumber}: {ex.Message}");
                else if (parseErrors == maxLoggedErrors + 1)
                    Log.Warn($"{sourceFile}: further parse errors suppressed");
            }

            if (address != null) yield return address;
        }

        if (parseErrors > 0)
            Log.Warn($"{sourceFile}: {parseErrors} lines skipped due to parse errors");
    }

    private static RuianAddress ParseLine(string line, string sourceFile, DateTime importedAt)
    {
        var cols = line.Split(';');

        if (cols.Length < MinColumns)
            throw new FormatException(
                $"Only {cols.Length} columns (min {MinColumns}): {Truncate(line, 80)}");

        return new RuianAddress
        {
            AdmCode          = ParseLong(cols[0]),
            MunicipalityCode = ParseInt(cols[1]),
            MunicipalityName = cols[2].Trim(),
            MomcCode         = ParseIntNull(cols[3]),
            MomcName         = NullIfEmpty(cols[4]),
            MopCode          = ParseIntNull(cols[5]),
            MopName          = NullIfEmpty(cols[6]),
            PartCode         = ParseInt(cols[7]),
            PartName         = cols[8].Trim(),
            StreetCode       = ParseIntNull(cols[9]),
            StreetName       = NullIfEmpty(cols[10]),
            BuildingType     = cols[11].Trim(),
            HouseNumber      = ParseInt(cols[12]),
            OrientationNumber     = ParseIntNull(cols[13]),
            OrientationNumberChar = NullIfEmpty(cols[14]),
            PostalCode       = cols[15].Trim().Replace(" ", ""),
            CoordinateY      = ParseDoubleNull(cols[16]),
            CoordinateX      = ParseDoubleNull(cols[17]),
            ValidFrom        = ParseDateNull(cols[18]),
            SourceFile       = sourceFile,
            ImportedAt       = importedAt
        };
    }

    private static long    ParseLong(string s)      => long.Parse(s.Trim());
    private static int     ParseInt(string s)       => int.Parse(s.Trim());
    private static int?    ParseIntNull(string s)   => string.IsNullOrWhiteSpace(s) ? null : int.Parse(s.Trim());
    private static string? NullIfEmpty(string s)    => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static double? ParseDoubleNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var n = s.Trim().Replace(',', '.');
        return double.TryParse(n, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateOnly? ParseDateNull(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParseExact(s.Trim(), "d.M.yyyy",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d)) return d;
        if (DateOnly.TryParse(s.Trim(), out var d2)) return d2;
        return null;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
