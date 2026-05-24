namespace RuianFeedParser.Models;

/// <summary>
/// Represents a generic ATOM feed entry.
/// </summary>
public sealed record FeedEntry
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public string? AlternateUrl { get; init; }
    public DateTime Updated { get; init; }
    public string? Author { get; init; }
    public string? Category { get; init; }
    public long? FileSizeBytes { get; init; }
    public string? MediaType { get; init; }
    /// <summary>Set when the entry links to a sub-feed (INSPIRE two-level ATOM). Must be resolved before downloading.</summary>
    public string? SubFeedUrl { get; init; }
}

/// <summary>
/// Represents a parsed ATOM feed (top-level).
/// </summary>
public sealed record AtomFeed
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public DateTime Updated { get; init; }
    public string FeedUrl { get; init; } = string.Empty;
    public string? Rights { get; init; }
    public List<FeedEntry> Entries { get; init; } = new();
}
