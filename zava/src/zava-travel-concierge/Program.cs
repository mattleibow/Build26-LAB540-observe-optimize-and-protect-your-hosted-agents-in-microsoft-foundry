// Copyright (c) Microsoft. All rights reserved.
//
// Zava Travel Concierge — multi-agent orchestration with the Microsoft Agent Framework.
//
// A single user-facing **Concierge** agent orchestrates three specialist sub-agents
// (Flight, Hotel, Car Rental) by calling them as tools. Each specialist owns one
// CSV data source and exposes a small set of typed C# tools to query it.

using Azure.AI.Projects;
using Azure.Identity;
using DotNetEnv;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Foundry.Hosting;
using Microsoft.Extensions.AI;

Env.TraversePath().Load();

// Load the grounding data once at startup. Fail fast with a clear message if the
// CSVs are missing from the published output / container image.
ZavaData.Load();

AIProjectClient projectClient = CreateProjectClient();
string deployment = Environment.GetEnvironmentVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
    ?? throw new InvalidOperationException("AZURE_AI_MODEL_DEPLOYMENT_NAME is not set.");

// ---------------------------------------------------------------------------
// Specialist sub-agents — each wraps a single CSV-backed tool
// ---------------------------------------------------------------------------

AIAgent flightAgent = projectClient.AsAIAgent(
    model: deployment,
    name: "flight_agent",
    description: "Searches the Zava flights catalog and recommends flights by route, cabin class, and price.",
    instructions:
        "You are the Zava Flight Specialist. Use `search_flights` to answer "
        + "every question. Return concise results with flight ID, airline, "
        + "route, dates, cabin class, and price. Never invent flights.",
    tools: [AIFunctionFactory.Create(
        ZavaTools.SearchFlights,
        "search_flights",
        "Search the Zava flights catalog. Returns matching flights with id, airline, route, dates, cabin, and price.")]);

AIAgent hotelAgent = projectClient.AsAIAgent(
    model: deployment,
    name: "hotel_agent",
    description: "Searches the Zava hotels catalog and recommends properties by city, star rating, amenities, and budget.",
    instructions:
        "You are the Zava Hotel Specialist. Use `search_hotels` to answer "
        + "every question. Return concise results with hotel ID, name, city, "
        + "star rating, nightly price, and key amenities. Never invent hotels.",
    tools: [AIFunctionFactory.Create(
        ZavaTools.SearchHotels,
        "search_hotels",
        "Search the Zava hotels catalog. Returns properties with id, name, city, star rating, nightly price, and amenities.")]);

AIAgent carRentalAgent = projectClient.AsAIAgent(
    model: deployment,
    name: "car_rental_agent",
    description: "Searches the Zava car rental catalog and recommends vehicles by city, type, and daily price.",
    instructions:
        "You are the Zava Car Rental Specialist. Use `search_car_rentals` "
        + "to answer every question. Return concise results with rental ID, "
        + "company, city, vehicle type, daily price, and availability. "
        + "Never invent vehicles.",
    tools: [AIFunctionFactory.Create(
        ZavaTools.SearchCarRentals,
        "search_car_rentals",
        "Search the Zava car rental catalog. Returns vehicles with id, company, city, type, daily price, and availability.")]);

// ---------------------------------------------------------------------------
// The Concierge — orchestrates the specialists as callable tools
// ---------------------------------------------------------------------------

AIAgent concierge = projectClient.AsAIAgent(
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
    ]);

// ---------------------------------------------------------------------------
// Host the Concierge over the Foundry Responses protocol on :8088
// ---------------------------------------------------------------------------

var builder = AgentHost.CreateBuilder(args);
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

partial class Program
{
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
}
