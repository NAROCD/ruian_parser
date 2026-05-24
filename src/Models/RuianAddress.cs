namespace RuianFeedParser.Models;

/// <summary>
/// An adresní místo from RUIAN CSV.
/// CSV columns (as of 2024 ČÚZK spec):
/// Kód ADM;Kód obce;Název obce;Kód MOMC;Název MOMC;Kód MOP;Název MOP;
/// Kód části obce;Název části obce;Kód ulice;Název ulice;Typ SO;
/// Číslo domovní;Číslo orientační;Znak čísla orientačního;
/// PSČ;Souřadnice Y;Souřadnice X;Platí Od
/// </summary>
public sealed class RuianAddress
{
    // Primary key in RUIAN
    public long AdmCode { get; set; }

    // Municipality
    public int MunicipalityCode { get; set; }
    public string MunicipalityName { get; set; } = string.Empty;

    // Sub-units (may be null)
    public int? MomcCode { get; set; }
    public string? MomcName { get; set; }
    public int? MopCode { get; set; }
    public string? MopName { get; set; }

    // Part of municipality (část obce)
    public int PartCode { get; set; }
    public string PartName { get; set; } = string.Empty;

    // Street
    public int? StreetCode { get; set; }
    public string? StreetName { get; set; }

    // Building numbers
    public string BuildingType { get; set; } = string.Empty; // "č.p." or "č.ev."
    public int HouseNumber { get; set; }
    public int? OrientationNumber { get; set; }
    public string? OrientationNumberChar { get; set; } // "a", "b", etc.

    // Postal code
    public string PostalCode { get; set; } = string.Empty;

    // JTSK coordinates (S-JTSK / Krovak East-North)
    public double? CoordinateY { get; set; }
    public double? CoordinateX { get; set; }

    // Validity
    public DateOnly? ValidFrom { get; set; }

    // Source tracking
    public string SourceFile { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
}
