using System.Xml.Linq;
using RuianFeedParser.Models;

namespace RuianFeedParser.Parsers;

/// <summary>
/// Parses ATOM 1.0 feeds. Works with any ATOM feed, not just ČÚZK.
///
/// INSPIRE two-level ATOM structure (used by all ČÚZK dataset feeds):
///   Level 1 feed  — one entry per dataset, links to a dataset sub-feed (type="application/atom+xml")
///   Level 2 feed  — one entry per file, links to the actual download (ZIP/CSV/XML)
///
/// The parser detects level-1 entries and sets SubFeedUrl. The caller is responsible
/// for fetching the sub-feed and resolving the real download URL.
/// </summary>
public static class AtomFeedParser
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";

    public static AtomFeed Parse(Stream stream, string feedUrl)
    {
        var doc  = XDocument.Load(stream);
        var root = doc.Root ?? throw new InvalidDataException("Empty XML document.");
        var ns   = root.Name.Namespace;

        return new AtomFeed
        {
            FeedUrl     = feedUrl,
            Id          = GetText(root, ns + "id") ?? feedUrl,
            Title       = GetText(root, ns + "title") ?? string.Empty,
            Description = GetText(root, ns + "subtitle") ?? GetText(root, ns + "description"),
            Rights      = GetText(root, ns + "rights"),
            Updated     = ParseDate(GetText(root, ns + "updated")) ?? DateTime.UtcNow,
            Entries     = ParseEntries(root, ns)
        };
    }

    public static AtomFeed ParseFromString(string xml, string feedUrl)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        return Parse(ms, feedUrl);
    }

    private static List<FeedEntry> ParseEntries(XElement root, XNamespace ns)
    {
        var entries = new List<FeedEntry>();

        foreach (var e in root.Elements(ns + "entry"))
        {
            string? downloadUrl = null;
            string? subFeedUrl  = null;
            string? alternateUrl = null;
            long?   fileSize    = null;
            string? mediaType   = null;

            foreach (var link in e.Elements(ns + "link"))
            {
                var rel    = (string?)link.Attribute("rel")  ?? "alternate";
                var href   = (string?)link.Attribute("href");
                var type   = (string?)link.Attribute("type") ?? string.Empty;
                var length = (string?)link.Attribute("length");

                if (href == null) continue;

                switch (rel)
                {
                    case "enclosure":
                        downloadUrl = href;
                        mediaType   = type;
                        if (long.TryParse(length, out var sz)) fileSize = sz;
                        break;

                    case "alternate":
                        // INSPIRE level-1: alternate link with type=application/atom+xml → sub-feed
                        if (type.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase))
                            subFeedUrl = href;
                        else
                        {
                            // Level-2 data link — treat as download
                            // ČÚZK uses rel=alternate even for the actual ZIP on the dataset sub-feed
                            downloadUrl ??= IsDataFile(href, type) ? href : null;
                            if (downloadUrl == href)
                            {
                                mediaType = type;
                                if (long.TryParse(length, out var sz2)) fileSize = sz2;
                            }
                            alternateUrl = href;
                        }
                        break;

                    case "section":
                        downloadUrl ??= href;
                        mediaType   ??= type;
                        break;
                }
            }

            // Bare <link href="..."/> with no rel — last resort for non-standard feeds
            if (downloadUrl == null && subFeedUrl == null)
            {
                var plain = e.Element(ns + "link");
                var href  = (string?)plain?.Attribute("href");
                var type  = (string?)plain?.Attribute("type") ?? string.Empty;
                if (href != null)
                {
                    if (type.Equals("application/atom+xml", StringComparison.OrdinalIgnoreCase))
                        subFeedUrl = href;
                    else if (IsDataFile(href, type))
                        downloadUrl = href;
                }
            }

            var category = (string?)e.Element(ns + "category")?.Attribute("term")
                        ?? GetText(e, ns + "category");

            entries.Add(new FeedEntry
            {
                Id            = GetText(e, ns + "id")    ?? string.Empty,
                Title         = GetText(e, ns + "title") ?? string.Empty,
                Summary       = GetText(e, ns + "summary") ?? GetText(e, ns + "content") ?? string.Empty,
                DownloadUrl   = downloadUrl,
                SubFeedUrl    = subFeedUrl,
                AlternateUrl  = alternateUrl,
                Updated       = ParseDate(GetText(e, ns + "updated")) ?? DateTime.MinValue,
                Author        = GetText(e, ns + "author", ns + "name"),
                Category      = category,
                FileSizeBytes = fileSize,
                MediaType     = mediaType
            });
        }

        return entries;
    }

    /// <summary>True when the href/type combination looks like a downloadable data file.</summary>
    private static bool IsDataFile(string href, string type)
    {
        if (type is "application/zip" or "text/csv" or "application/xml" or "application/gml+xml")
            return true;
        var lower = href.ToLowerInvariant();
        return lower.EndsWith(".zip") || lower.EndsWith(".csv")
            || lower.EndsWith(".xml") || lower.EndsWith(".gz")
            || lower.EndsWith(".gml");
    }

    private static string? GetText(XElement parent, XName name)
        => parent.Element(name)?.Value?.Trim();

    private static string? GetText(XElement parent, XName container, XName child)
        => parent.Element(container)?.Element(child)?.Value?.Trim();

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            return dt.ToUniversalTime();
        return null;
    }
}
