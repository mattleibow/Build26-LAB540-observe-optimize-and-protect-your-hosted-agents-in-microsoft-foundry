// Copyright (c) Microsoft. All rights reserved.

using System.ComponentModel;
using System.Text;

/// <summary>
/// Loads the three Zava CSV catalogs once at startup into in-memory rows.
/// No database, no I/O on the hot path — keeps cold-start fast and behavior
/// deterministic for evaluation.
/// </summary>
internal static class ZavaData
{
    private static readonly string DataDir = Path.Combine(AppContext.BaseDirectory, "data");

    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> Flights { get; private set; } = [];
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> Hotels { get; private set; } = [];
    internal static IReadOnlyList<IReadOnlyDictionary<string, string>> Cars { get; private set; } = [];

    internal static void Load()
    {
        Flights = LoadCsv("flights.csv");
        Hotels = LoadCsv("hotels.csv");
        Cars = LoadCsv("car_rentals.csv");
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> LoadCsv(string name)
    {
        string path = Path.Combine(DataDir, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required data file '{name}' was not found at '{path}'. " +
                "Ensure the csproj copies data/** to the output directory and the Docker build includes it.",
                path);
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        string? headerLine = reader.ReadLine();
        if (headerLine is null)
        {
            return [];
        }

        string[] headers = ParseCsvLine(headerLine);
        var rows = new List<IReadOnlyDictionary<string, string>>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            string[] values = ParseCsvLine(line);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                row[headers[i]] = i < values.Length ? values[i] : string.Empty;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Quote-aware CSV line parser (handles fields with embedded commas and escaped quotes).</summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        fields.Add(sb.ToString());
        return [.. fields];
    }
}

/// <summary>
/// The typed search tools each specialist agent exposes. Optional parameters map
/// to the original Python tool signatures; <see cref="DescriptionAttribute"/>
/// gives the model a clean JSON schema for each argument.
/// </summary>
internal static class ZavaTools
{
    private static bool Matches(string value, string? query) =>
        query is null || value.Trim().Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FormatRows(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        var formatted = rows
            .Select(row => "- {" + string.Join(", ", row.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}")
            .ToList();

        return formatted.Count == 0 ? "No matching records found." : string.Join("\n", formatted);
    }

    [Description("Search the Zava flights catalog. Returns matching flights with id, airline, route, dates, cabin, and price.")]
    internal static string SearchFlights(
        [Description("Origin city, e.g. 'Chicago'. Optional.")] string? origin = null,
        [Description("Destination city, e.g. 'Rome'. Optional.")] string? destination = null,
        [Description("Cabin class: Economy, Business, or First. Optional.")] string? cabinClass = null,
        [Description("Maximum price in USD. Optional.")] double? maxPriceUsd = null)
    {
        var results = ZavaData.Flights.Where(f =>
            Matches(f["origin"], origin) &&
            Matches(f["destination"], destination) &&
            Matches(f["cabin_class"], cabinClass) &&
            (maxPriceUsd is null || ParsePrice(f["price_usd"]) <= maxPriceUsd));

        return FormatRows(results);
    }

    [Description("Search the Zava hotels catalog. Returns properties with id, name, city, star rating, nightly price, and amenities.")]
    internal static string SearchHotels(
        [Description("City name, e.g. 'Paris'. Optional.")] string? city = null,
        [Description("Minimum star rating 1-5. Optional.")] int? minStarRating = null,
        [Description("Maximum nightly price in USD. Optional.")] double? maxPricePerNightUsd = null,
        [Description("A single amenity that must be present, e.g. 'Pool'. Optional.")] string? requiredAmenity = null)
    {
        var results = ZavaData.Hotels.Where(h =>
            Matches(h["city"], city) &&
            (minStarRating is null || ParseInt(h["star_rating"]) >= minStarRating) &&
            (maxPricePerNightUsd is null || ParsePrice(h["price_per_night_usd"]) <= maxPricePerNightUsd) &&
            (requiredAmenity is null ||
                h["amenities"].Contains(requiredAmenity.Trim(), StringComparison.OrdinalIgnoreCase)));

        return FormatRows(results);
    }

    [Description("Search the Zava car rental catalog. Returns vehicles with id, company, city, type, daily price, and availability.")]
    internal static string SearchCarRentals(
        [Description("Pickup city, e.g. 'Rome'. Optional.")] string? city = null,
        [Description("Vehicle type: Economy, SUV, Luxury, or Minivan. Optional.")] string? carType = null,
        [Description("Maximum daily price in USD. Optional.")] double? maxPricePerDayUsd = null,
        [Description("If true, only return vehicles currently available.")] bool availableOnly = true)
    {
        var results = ZavaData.Cars.Where(c =>
            Matches(c["city"], city) &&
            Matches(c["car_type"], carType) &&
            (maxPricePerDayUsd is null || ParsePrice(c["price_per_day_usd"]) <= maxPricePerDayUsd) &&
            (!availableOnly || string.Equals(c["available"], "true", StringComparison.OrdinalIgnoreCase)));

        return FormatRows(results);
    }

    private static double ParsePrice(string value) =>
        double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out double result) ? result : double.MaxValue;

    private static int ParseInt(string value) =>
        int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int result) ? result : 0;
}
