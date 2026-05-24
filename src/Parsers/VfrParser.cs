using System.IO.Compression;
using System.Xml;
using RuianFeedParser.Services;

namespace RuianFeedParser.Parsers;

/// <summary>
/// Streaming SAX-style parser for RUIAN VFR (Výměnný Formát RÚIAN) files.
/// VFR is GML 3.2.1 XML. We care only about AdresniMisto elements.
///
/// Deletion detection: PlatiDo element is present and non-empty → deleted.
/// Coordinates: S-JTSK (Křovák), "Y X" order in GML pos, both negative values.
/// Encoding: GZip detection is automatic (magic bytes).
/// </summary>
public static class VfrParser
{
    private static readonly Logger Log = new("VfrParser");
    private const string AdresniMistoLocalName = "AdresniMisto";

    private static readonly XmlReaderSettings XmlSettings = new()
    {
        Async                    = false,
        IgnoreComments           = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace         = true,
        DtdProcessing            = DtdProcessing.Ignore,
        XmlResolver              = null
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a VFR stream (plain XML or GZip-compressed).
    /// Caller owns the stream lifetime.
    /// </summary>
    public static IEnumerable<VfrAddressRecord> ParseStream(
        Stream stream,
        string sourceLabel,
        CancellationToken ct = default)
    {
        Stream actual = IsGzip(stream)
            ? new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true)
            : stream;

        using var reader = XmlReader.Create(actual, XmlSettings);
        int parsed = 0;
        int errors = 0;

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element ||
                reader.LocalName != AdresniMistoLocalName)
                continue;

            VfrAddressRecord? record = null;
            try
            {
                record = ParseAdresniMisto(reader, sourceLabel);
            }
            catch (Exception ex)
            {
                errors++;
                Log.Warn($"Failed to parse AdresniMisto element #{parsed + errors} in {sourceLabel}: {ex.Message}");
                // Continue — one bad element doesn't abort the file
            }

            if (record != null)
            {
                parsed++;
                yield return record;
            }
        }

        if (errors > 0)
            Log.Warn($"{sourceLabel}: {errors} elements skipped due to parse errors ({parsed} ok)");
    }

    /// <summary>
    /// Parse a ZIP file containing one or more VFR XML/GZ entries.
    /// The ZIP is fully read before returning — this avoids the iterator-over-disposed-stream bug.
    /// For very large ZIPs, use ParseZipStreaming and handle the stream lifetime yourself.
    /// </summary>
    public static List<VfrAddressRecord> ParseZip(
        Stream zipStream,
        string sourceLabel,
        CancellationToken ct = default)
    {
        var results = new List<VfrAddressRecord>();

        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext != ".xml" && ext != ".gz" && ext != ".gml") continue;

            Log.Debug($"Parsing ZIP entry: {entry.Name} ({entry.Length:N0} bytes uncompressed)");

            // Read the entry into a MemoryStream first so it's safe to iterate after the
            // ZipArchiveEntry is closed. Entries are typically <50MB uncompressed.
            using var entryStream  = entry.Open();
            using var buffer       = new MemoryStream((int)Math.Min(entry.Length, 512 * 1024 * 1024));
            entryStream.CopyTo(buffer);
            buffer.Position = 0;

            foreach (var record in ParseStream(buffer, $"{sourceLabel}/{entry.Name}", ct))
                results.Add(record);
        }

        return results;
    }

    // ── Element parser ────────────────────────────────────────────────────────

    private static VfrAddressRecord? ParseAdresniMisto(XmlReader reader, string sourceLabel)
    {
        var record = new VfrAddressRecord { SourceFile = sourceLabel };

        using var subtree = reader.ReadSubtree();
        subtree.Read(); // position on the AdresniMisto element

        while (subtree.Read())
        {
            if (subtree.NodeType != XmlNodeType.Element) continue;

            switch (subtree.LocalName)
            {
                case "Kod":
                    if (long.TryParse(subtree.ReadElementContentAsString(), out var code))
                        record.AdmCode = code;
                    break;

                case "PlatiOd":
                    var od = subtree.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(od) && DateOnly.TryParse(od, out var platiOd))
                        record.ValidFrom = platiOd;
                    break;

                case "PlatiDo":
                    var platiDo = subtree.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(platiDo))
                    {
                        record.IsDeleted = true;
                        if (DateOnly.TryParse(platiDo, out var deletedOn))
                            record.DeletedOn = deletedOn;
                    }
                    break;

                case "CisloDomovni":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var hn))
                        record.HouseNumber = hn;
                    break;

                case "DruhCisloDomovniKod":
                    // 1 = č.p. (popisné), 2 = č.ev. (evidenční)
                    record.BuildingType = subtree.ReadElementContentAsString().Trim() == "2"
                        ? "č.ev." : "č.p.";
                    break;

                case "CisloOrientacni":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var on))
                        record.OrientationNumber = on;
                    break;

                case "CisloOrientacniPismeno":
                    record.OrientationNumberChar = subtree.ReadElementContentAsString().Trim();
                    break;

                case "Psc":
                    record.PostalCode = subtree.ReadElementContentAsString().Trim();
                    break;

                case "UliceKod":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var ulice))
                        record.StreetCode = ulice;
                    break;

                case "CastObceKod":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var cast))
                        record.PartCode = cast;
                    break;

                case "ObecKod":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var obec))
                        record.MunicipalityCode = obec;
                    break;

                case "MomcKod":
                    if (int.TryParse(subtree.ReadElementContentAsString(), out var momc))
                        record.MomcCode = momc;
                    break;

                case "pos":
                    // GML: "Y X" in S-JTSK, both negative
                    ParseGmlPos(subtree.ReadElementContentAsString(), out var cy, out var cx);
                    record.CoordinateY = cy;
                    record.CoordinateX = cx;
                    break;

                default:
                    subtree.Skip();
                    break;
            }
        }

        if (record.AdmCode == 0)
        {
            Log.Debug($"Skipping AdresniMisto with no Kod in {sourceLabel}");
            return null;
        }

        return record;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void ParseGmlPos(string pos, out double? y, out double? x)
    {
        y = null; x = null;
        if (string.IsNullOrWhiteSpace(pos)) return;
        var parts = pos.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return;
        if (double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var py)) y = py;
        if (double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var px)) x = px;
    }

    private static bool IsGzip(Stream stream)
    {
        if (!stream.CanSeek) return false;
        Span<byte> buf = stackalloc byte[2];
        int read = stream.Read(buf);
        stream.Seek(0, SeekOrigin.Begin);
        return read == 2 && buf[0] == 0x1F && buf[1] == 0x8B;
    }
}

/// <summary>
/// A single record from a VFR změnová file.
/// IsDeleted=true → remove from DB. Otherwise upsert.
/// </summary>
public sealed class VfrAddressRecord
{
    public long    AdmCode              { get; set; }
    public bool    IsDeleted            { get; set; }
    public DateOnly? DeletedOn          { get; set; }
    public int     MunicipalityCode     { get; set; }
    public int?    MomcCode             { get; set; }
    public int     PartCode             { get; set; }
    public int?    StreetCode           { get; set; }
    public string  BuildingType         { get; set; } = "č.p.";
    public int     HouseNumber          { get; set; }
    public int?    OrientationNumber    { get; set; }
    public string? OrientationNumberChar{ get; set; }
    public string  PostalCode           { get; set; } = string.Empty;
    public double? CoordinateY          { get; set; }
    public double? CoordinateX          { get; set; }
    public DateOnly? ValidFrom          { get; set; }
    public string  SourceFile           { get; set; } = string.Empty;
}
