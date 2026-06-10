#:sdk Microsoft.NET.Sdk.Web
#:package Microsoft.Agents.AI.Foundry.Hosting@1.3.0-preview.260423.1
#:package Azure.AI.Projects@2.1.0-beta.1
#:package DotNetEnv@3.1.1
#:package CsvHelper@33.1.0
#:package OpenTelemetry.Api@1.15.3
#:package OpenTelemetry.Exporter.OpenTelemetryProtocol@1.15.3
#:property PublishAot=false

// Copyright (c) Microsoft. All rights reserved.
//
// Zava Travel Concierge — multi-agent orchestration with the Microsoft Agent Framework.
//
// A single user-facing **Concierge** agent orchestrates three specialist sub-agents
// (Flight, Hotel, Car Rental) by calling them as tools. Each specialist owns one
// CSV data source and exposes a small set of typed C# tools to query it.
//
// This is a .NET 10 file-based app — there is no .csproj. The `#:` directives above
// declare the SDK, NuGet packages, and build properties that `dotnet publish` honors.

using System.ComponentModel;
using System.Globalization;
using Azure.AI.AgentServer.Core;
using Azure.AI.Projects;
using Azure.Identity;
using CsvHelper;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;

// Load environment variables from .env (when running locally) and from the
// Foundry hosting environment (when running in the container in Azure).
Env.TraversePath().Load();

var Flights = LoadCsv("flights.csv");
var Hotels = LoadCsv("hotels.csv");
var Cars = LoadCsv("car_rentals.csv");

AIProjectClient projectClient = CreateProjectClient();
string deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");

// ---------------------------------------------------------------------------
// Tracing — the Agent Framework emits OpenTelemetry `gen_ai.*` spans (the
// "conversation turns" Foundry shows) ONLY when each agent is wrapped with the
// OpenTelemetry delegating agent. The agent `name` flows into `gen_ai.agent.name`,
// which is what the Foundry conversation/Agents view groups on. Content recording
// (user prompts, tool args, model outputs) is opt-in and gated below.
// ---------------------------------------------------------------------------
const string TelemetrySourceName = "ZavaTravelConcierge";

// AZURE_TRACING_GEN_AI_CONTENT_RECORDING_ENABLED=true (set in agent.yaml) turns on
// message-content capture in the spans. Leave it off in production.
bool recordContent =
    (Environment.GetEnvironmentVariable("AZURE_TRACING_GEN_AI_CONTENT_RECORDING_ENABLED") ?? string.Empty)
        .Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

AIAgent WithTelemetry(AIAgent agent) =>
    agent.AsBuilder()
        .UseOpenTelemetry(TelemetrySourceName, configure: otel => otel.EnableSensitiveData = recordContent)
        .Build();

// ---------------------------------------------------------------------------
// Flight search tool — queries the Zava flights catalog by route, cabin, price
// ---------------------------------------------------------------------------

[Description("Search the Zava flights catalog. Returns matching flights with id, airline, route, dates, cabin, and price.")]
string SearchFlights(
    [Description("Origin city, e.g. 'Chicago'. Optional.")] string? origin = null,
    [Description("Destination city, e.g. 'Rome'. Optional.")] string? destination = null,
    [Description("Cabin class: Economy, Business, or First. Optional.")] string? cabinClass = null,
    [Description("Maximum price in USD. Optional.")] double? maxPriceUsd = null)
{
    var results = Flights.Where(f =>
        Matches(f["origin"], origin) &&
        Matches(f["destination"], destination) &&
        Matches(f["cabin_class"], cabinClass) &&
        (maxPriceUsd is null || ParsePrice(f["price_usd"]) <= maxPriceUsd));

    return FormatRows(results);
}

// ---------------------------------------------------------------------------
// Hotel search tool — queries the Zava hotels catalog by city, rating, amenity
// ---------------------------------------------------------------------------

[Description("Search the Zava hotels catalog. Returns properties with id, name, city, star rating, nightly price, and amenities.")]
string SearchHotels(
    [Description("City name, e.g. 'Paris'. Optional.")] string? city = null,
    [Description("Minimum star rating 1-5. Optional.")] int? minStarRating = null,
    [Description("Maximum nightly price in USD. Optional.")] double? maxPricePerNightUsd = null,
    [Description("A single amenity that must be present, e.g. 'Pool'. Optional.")] string? requiredAmenity = null)
{
    var results = Hotels.Where(h =>
        Matches(h["city"], city) &&
        (minStarRating is null || ParseInt(h["star_rating"]) >= minStarRating) &&
        (maxPricePerNightUsd is null || ParsePrice(h["price_per_night_usd"]) <= maxPricePerNightUsd) &&
        (requiredAmenity is null ||
            h["amenities"].Contains(requiredAmenity.Trim(), StringComparison.OrdinalIgnoreCase)));

    return FormatRows(results);
}

// ---------------------------------------------------------------------------
// Car rental search tool — queries the Zava car rental catalog by city, type
// ---------------------------------------------------------------------------

[Description("Search the Zava car rental catalog. Returns vehicles with id, company, city, type, daily price, and availability.")]
string SearchCarRentals(
    [Description("Pickup city, e.g. 'Rome'. Optional.")] string? city = null,
    [Description("Vehicle type: Economy, SUV, Luxury, or Minivan. Optional.")] string? carType = null,
    [Description("Maximum daily price in USD. Optional.")] double? maxPricePerDayUsd = null,
    [Description("If true, only return vehicles currently available.")] bool availableOnly = true)
{
    var results = Cars.Where(c =>
        Matches(c["city"], city) &&
        Matches(c["car_type"], carType) &&
        (maxPricePerDayUsd is null || ParsePrice(c["price_per_day_usd"]) <= maxPricePerDayUsd) &&
        (!availableOnly || string.Equals(c["available"], "true", StringComparison.OrdinalIgnoreCase)));

    return FormatRows(results);
}

// ---------------------------------------------------------------------------
// Specialist sub-agents — each wraps a single CSV-backed tool
// ---------------------------------------------------------------------------

AIAgent flightAgent = WithTelemetry(projectClient.AsAIAgent(
    model: deployment,
    name: "flight_agent",
    description: "Searches the Zava flights catalog and recommends flights by route, cabin class, and price.",
    instructions:
        """
        You are the Zava Flight Specialist. Use `search_flights` to answer
        every question. Return concise results with flight ID, airline,
        route, dates, cabin class, and price. Never invent flights.
        """,
    tools: [AIFunctionFactory.Create(
        SearchFlights,
        "search_flights",
        "Search the Zava flights catalog. Returns matching flights with id, airline, route, dates, cabin, and price.")]));

AIAgent hotelAgent = WithTelemetry(projectClient.AsAIAgent(
    model: deployment,
    name: "hotel_agent",
    description: "Searches the Zava hotels catalog and recommends properties by city, star rating, amenities, and budget.",
    instructions:
        """
        You are the Zava Hotel Specialist. Use `search_hotels` to answer
        every question. Return concise results with hotel ID, name, city,
        star rating, nightly price, and key amenities. Never invent hotels.
        """,
    tools: [AIFunctionFactory.Create(
        SearchHotels,
        "search_hotels",
        "Search the Zava hotels catalog. Returns properties with id, name, city, star rating, nightly price, and amenities.")]));

AIAgent carRentalAgent = WithTelemetry(projectClient.AsAIAgent(
    model: deployment,
    name: "car_rental_agent",
    description: "Searches the Zava car rental catalog and recommends vehicles by city, type, and daily price.",
    instructions:
        """
        You are the Zava Car Rental Specialist. Use `search_car_rentals` 
        to answer every question. Return concise results with rental ID, 
        company, city, vehicle type, daily price, and availability. 
        Never invent vehicles.
        """,
    tools: [AIFunctionFactory.Create(
        SearchCarRentals,
        "search_car_rentals",
        "Search the Zava car rental catalog. Returns vehicles with id, company, city, type, daily price, and availability.")]));

// ---------------------------------------------------------------------------
// The Concierge — orchestrates the specialists as callable tools
// ---------------------------------------------------------------------------

// ============================================================================
// WORKSHOP SAFETY NOTE — DO NOT COMMIT EDITS TO ConciergeInstructions
// ============================================================================
// This string is the seed agent prompt that ships with the workshop. In
// Lab 3 you will edit it locally to test optimizations, then redeploy
// with `azd deploy` to evaluate the change.
//
// Those edits are EXPERIMENTAL and per-learner. They must stay in your
// working tree only. If you `git add` and `git commit` this file with
// your changes:
//   - Other learners pulling the workshop will inherit your hypothesis
//     as their starting point.
//   - The baseline-vs-optimized comparison flow in Lab 3 breaks (because
//     "baseline" is no longer the seed).
//
// Before committing anything in zava/src/zava-travel-concierge/, run:
//     git diff Program.cs
// and confirm ConciergeInstructions is unchanged from the template.
// If you intentionally want to update the seed (e.g. as a workshop
// maintainer), open a PR and call that out explicitly in the description.
// ============================================================================
const string ConciergeInstructions =
    """
    You are the **Zava Travel Concierge**, the single AI assistant that travelers
    talk to at Zava Travel — a premium agency that books flights, hotels, and car
    rentals across Paris, London, Tokyo, Rome, and Cancún.

    You are warm, professional, and concise. You never answer flight, hotel, or
    car-rental questions from your own knowledge — you delegate to specialist
    agents available as tools:

    - `flight_agent` — for routes, airlines, cabin classes, prices, availability
    - `hotel_agent` — for properties, star ratings, amenities, nightly rates
    - `car_rental_agent` — for vehicles, daily rates, pickup cities

    Rules:
    1. For multi-component requests (e.g. "plan a trip…"), call each relevant
        specialist independently in parallel, then merge the results into one
        itinerary.
    2. Always cite the Zava ID (e.g. ZV-FL-013, ZV-HT-010, ZV-CR-011), price, and
        dates in your recommendation.
    3. Never fabricate flights, hotels, vehicles, prices, or IDs. If a specialist
        returns no results, say so plainly.
    4. Lead with the recommendation, then a short reason it fits. Offer one
        meaningful alternative when useful. Currency is USD unless stated.
    5. Decline non-travel, unsafe, or policy-violating requests in one sentence.
    """;

AIAgent concierge = WithTelemetry(projectClient.AsAIAgent(
    model: deployment,
    name: "zava-concierge",
    description: "Zava Travel Concierge — orchestrates flight, hotel, and car rental specialists to plan complete itineraries.",
    instructions: ConciergeInstructions,
    // Sub-agents *are* tools to their parent — the multi-agent orchestration pattern.
    // Note: the Python version set `store=False` on the concierge to stop the Responses
    // API from persisting conversation state. Under the .NET Foundry hosting model,
    // conversation history is owned and managed by the hosting infrastructure (the
    // Responses endpoint registered below) rather than configured per-agent here.
    tools:
    [
        flightAgent.AsAIFunction(),
        hotelAgent.AsAIFunction(),
        carRentalAgent.AsAIFunction(),
    ]));

// ---------------------------------------------------------------------------
// Host the Concierge over the Foundry Responses protocol on :8088
// ---------------------------------------------------------------------------

var builder = AgentHost.CreateBuilder(args);

// The hosting runtime already exports telemetry to the project's Application
// Insights (via APPLICATIONINSIGHTS_CONNECTION_STRING). We only need to register
// our custom ActivitySource so the agents' `gen_ai.*` spans flow into that same
// pipeline and show up as conversation turns in the Foundry Traces view.
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(TelemetrySourceName));

builder.Services.AddFoundryResponses(concierge);
builder.RegisterProtocol("responses", endpoints => endpoints.MapFoundryResponses());

var app = builder.Build();
app.Run();

static AIProjectClient CreateProjectClient()
{
    // FOUNDRY_PROJECT_ENDPOINT is auto-injected by the Foundry hosting runtime.
    // When running locally (azd ai agent run / direct dotnet run) we accept the
    // AZURE_AI_PROJECT_ENDPOINT name used in `.env` / Bicep outputs.
    string? endpoint = Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT")
        ?? Environment.GetEnvironmentVariable("AZURE_AI_PROJECT_ENDPOINT");
    if (string.IsNullOrWhiteSpace(endpoint))
    {
        throw new InvalidOperationException(
            "Neither FOUNDRY_PROJECT_ENDPOINT nor AZURE_AI_PROJECT_ENDPOINT is set. "
            + "Run scripts/discover-env.sh or copy scripts/sample.env to .env and fill it in.");
    }

    return new AIProjectClient(new Uri(endpoint), new DefaultAzureCredential());
}

/// <summary>
/// Loads the three Zava CSV catalogs once at startup into in-memory rows using
/// CsvHelper. No database, no I/O on the hot path — keeps cold-start fast and
/// behavior deterministic for evaluation.
/// </summary>
static IReadOnlyList<IReadOnlyDictionary<string, string>> LoadCsv(string name)
{
    string path = Path.Combine("data", name);
    if (!File.Exists(path))
    {
        throw new FileNotFoundException(
            $"Required data file '{name}' was not found at '{path}'. " +
            "Ensure the Docker build copies data/** alongside the published app.",
            path);
    }

    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
    csv.Read();
    csv.ReadHeader();

    var rows = new List<IReadOnlyDictionary<string, string>>();
    while (csv.Read())
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string header in csv.HeaderRecord!)
        {
            row[header] = csv.GetField(header) ?? string.Empty;
        }

        rows.Add(row);
    }

    return rows;
}

// ---------------------------------------------------------------------------
// Shared helpers for the search tools — case-insensitive matching, row
// formatting, and lenient numeric parsing of CSV fields.
// ---------------------------------------------------------------------------

static bool Matches(string value, string? query) =>
    query is null || value.Trim().Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);

static string FormatRows(IEnumerable<IReadOnlyDictionary<string, string>> rows)
{
    var formatted = rows
        .Select(row => "- {" + string.Join(", ", row.Select(kv => $"'{kv.Key}': '{kv.Value}'")) + "}")
        .ToList();

    return formatted.Count == 0 ? "No matching records found." : string.Join("\n", formatted);
}

static double ParsePrice(string value) =>
    double.TryParse(value, CultureInfo.InvariantCulture, out double result) ? result : double.MaxValue;

static int ParseInt(string value) =>
    int.TryParse(value, CultureInfo.InvariantCulture, out int result) ? result : 0;
