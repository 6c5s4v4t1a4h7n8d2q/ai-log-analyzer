using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);

var apiKey = builder.Configuration["OpenAI:ApiKey"] ?? builder.Configuration["OPENAI_API_KEY"];
var scrapeDoToken = builder.Configuration["ScrapeDo:Token"] ?? builder.Configuration["SCRAPEDO_API_TOKEN"];
var chatClient = string.IsNullOrWhiteSpace(apiKey)
    ? null
    : new ChatClient(model: "gpt-4.1-mini", apiKey: apiKey);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/api/analyze", async (HttpRequest request) =>
{
    if (chatClient is null)
    {
        return Results.Problem(
            title: "OpenAI API key is not configured",
            detail: "Set OpenAI:ApiKey with dotnet user-secrets, or set the OPENAI_API_KEY environment variable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload a text log file using multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("logFile");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Choose a non-empty log file." });
    }

    const long maxFileBytes = 2 * 1024 * 1024;
    if (file.Length > maxFileBytes)
    {
        return Results.BadRequest(new { error = "The log file is too large. The current limit is 2 MB." });
    }

    var extension = Path.GetExtension(file.FileName);
    if (!string.IsNullOrWhiteSpace(extension) &&
        !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) &&
        !extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Upload a .txt or .log file." });
    }

    string logs;
    using (var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
    {
        logs = await reader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(logs))
    {
        return Results.BadRequest(new { error = "The uploaded file does not contain readable log text." });
    }

    var systemMessage = """
        You are a senior production support engineer who analyzes application logs.
        Return a concise analysis in Markdown with exactly these headings:
        ## Summary
        ## Errors
        ## Likely root cause
        ## Severity
        ## Recommended next steps
        ## Useful signals

        Under Errors, list the concrete error messages, exception names, status codes, stack frames, or failure events you can identify.
        If the logs do not contain enough evidence for a section, say that clearly instead of guessing.
        Keep the response practical and focused on what an engineer should investigate next.
        """;

    var userMessage = $"""
        Analyze this uploaded log file.

        File name: {file.FileName}
        Size: {file.Length} bytes

        Logs:
        ```text
        {logs}
        ```
        """;

    try
    {
        var completion = await chatClient.CompleteChatAsync(
            messages:
            [
                new SystemChatMessage(systemMessage),
                new UserChatMessage(userMessage)
            ]);

        var analysis = completion.Value.Content.Count > 0
            ? completion.Value.Content[0].Text
            : "The model returned no analysis.";

        return Results.Ok(new
        {
            fileName = file.FileName,
            fileSize = file.Length,
            analysis
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "OpenAI log analysis failed.");
        return Results.Problem(
            title: "OpenAI analysis failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapPost("/api/describe-image", async (HttpRequest request) =>
{
    if (chatClient is null)
    {
        return Results.Problem(
            title: "OpenAI API key is not configured",
            detail: "Set OpenAI:ApiKey with dotnet user-secrets, or set the OPENAI_API_KEY environment variable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "Upload an image file using multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("imageFile");

    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Choose a non-empty image file." });
    }

    const long maxImageBytes = 10 * 1024 * 1024;
    if (file.Length > maxImageBytes)
    {
        return Results.BadRequest(new { error = "The image is too large. The current limit is 10 MB." });
    }

    var mediaType = GetSupportedImageMediaType(file);
    if (mediaType is null)
    {
        return Results.BadRequest(new { error = "Upload a PNG, JPG, JPEG, WEBP, or non-animated GIF image." });
    }

    byte[] imageBytes;
    await using (var stream = file.OpenReadStream())
    using (var memory = new MemoryStream())
    {
        await stream.CopyToAsync(memory);
        imageBytes = memory.ToArray();
    }

    var systemMessage = """
        You are a careful visual analysis assistant.
        Explain what is visible in the uploaded image in Markdown with exactly these headings:
        ## Overview
        ## Main subjects
        ## Text visible
        ## Notable details
        ## Uncertainties

        Describe only what can be reasonably inferred from the image.
        If text is visible, transcribe the useful parts.
        If something is unclear or ambiguous, say so.
        """;

    try
    {
        var completion = await chatClient.CompleteChatAsync(
            messages:
            [
                new SystemChatMessage(systemMessage),
                new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart("Explain what is in this image."),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), mediaType, ChatImageDetailLevel.Auto))
            ]);

        var description = completion.Value.Content.Count > 0
            ? completion.Value.Content[0].Text
            : "The model returned no description.";

        return Results.Ok(new
        {
            fileName = file.FileName,
            fileSize = file.Length,
            description
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "OpenAI image description failed.");
        return Results.Problem(
            title: "OpenAI image description failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.MapGet("/api/flight-locations", async (string query) =>
{
    if (chatClient is null)
    {
        return Results.Problem(
            title: "OpenAI API key is not configured",
            detail: "Set OpenAI:ApiKey with dotnet user-secrets, or set the OPENAI_API_KEY environment variable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
    {
        return Results.Ok(Array.Empty<AirportSuggestion>());
    }

    var suggestions = await SuggestAirportsAsync(chatClient, query);
    return Results.Ok(suggestions);
});

app.MapPost("/api/search-flights", async (FlightSearchRequest search) =>
{
    if (chatClient is null)
    {
        return Results.Problem(
            title: "OpenAI API key is not configured",
            detail: "Set OpenAI:ApiKey with dotnet user-secrets, or set the OPENAI_API_KEY environment variable.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    if (!DateOnly.TryParse(search.FromDate, out var outboundDate) ||
        !DateOnly.TryParse(search.ToDate, out var returnDate))
    {
        return Results.BadRequest(new { error = "Enter valid from and to dates." });
    }

    if (returnDate < outboundDate)
    {
        return Results.BadRequest(new { error = "The to date must be the same as or later than the from date." });
    }

    if (string.IsNullOrWhiteSpace(search.Departure) || string.IsNullOrWhiteSpace(search.Destination))
    {
        return Results.BadRequest(new { error = "Enter both departure and destination cities." });
    }

    var departure = await ResolveAirportAsync(chatClient, search.Departure);
    var destination = await ResolveAirportAsync(chatClient, search.Destination);

    if (departure is null || destination is null)
    {
        return Results.BadRequest(new { error = "Could not resolve one of those cities to an airport code." });
    }

    if (string.Equals(departure.Iata, destination.Iata, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Departure and destination resolved to the same airport." });
    }

    if (string.IsNullOrWhiteSpace(scrapeDoToken))
    {
        return Results.Ok(new FlightSearchResponse(
            departure,
            destination,
            search.FromDate,
            search.ToDate,
            false,
            "Live fare search is not configured. Set ScrapeDo:Token with dotnet user-secrets, or set SCRAPEDO_API_TOKEN.",
            [],
            null,
            BuildGoogleFlightsUrl(departure.Iata, destination.Iata, search.FromDate, search.ToDate)));
    }

    try
    {
        using var httpClient = new HttpClient();
        using var response = await httpClient.GetAsync(BuildScrapeDoFlightsUri(
            scrapeDoToken,
            departure.Iata,
            destination.Iata,
            search.FromDate,
            search.ToDate));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            return Results.Problem(
                title: "Flight search failed",
                detail: body,
                statusCode: StatusCodes.Status502BadGateway);
        }

        var flights = ExtractFlightOptions(body);
        var priceInsight = ExtractPriceInsight(body);
        var summary = await SummarizeFlightOptionsAsync(chatClient, departure, destination, search, flights, priceInsight);

        return Results.Ok(new FlightSearchResponse(
            departure,
            destination,
            search.FromDate,
            search.ToDate,
            true,
            summary,
            flights,
            priceInsight,
            BuildGoogleFlightsUrl(departure.Iata, destination.Iata, search.FromDate, search.ToDate)));
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Flight search failed.");
        return Results.Problem(
            title: "Flight search failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status502BadGateway);
    }
});

app.Run();

static string? GetSupportedImageMediaType(IFormFile file)
{
    var contentType = file.ContentType.ToLowerInvariant();
    var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

    return (contentType, extension) switch
    {
        ("image/png", _) or (_, ".png") => "image/png",
        ("image/jpeg", _) or (_, ".jpg") or (_, ".jpeg") => "image/jpeg",
        ("image/webp", _) or (_, ".webp") => "image/webp",
        ("image/gif", _) or (_, ".gif") => "image/gif",
        _ => null
    };
}

static async Task<List<AirportSuggestion>> SuggestAirportsAsync(ChatClient chatClient, string query)
{
    var completion = await chatClient.CompleteChatAsync(
        messages:
        [
            new SystemChatMessage("""
                Return only valid JSON. No markdown.
                Given a partial city or airport name, return up to 5 likely commercial passenger airports.
                Shape: [{"city":"New York","country":"United States","airportName":"John F. Kennedy International Airport","iata":"JFK","label":"New York, United States - JFK"}]
                Use IATA airport codes only. Prefer major airports for city names.
                """),
            new UserChatMessage($"Location query: {query.Trim()}")
        ]);

    var text = completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : "[]";
    return ParseAirportSuggestions(text);
}

static async Task<AirportSuggestion?> ResolveAirportAsync(ChatClient chatClient, string value)
{
    var trimmed = value.Trim();
    var iataMatch = Regex.Match(trimmed, @"\b[A-Za-z]{3}\b");
    if (iataMatch.Success)
    {
        var iata = iataMatch.Value.ToUpperInvariant();
        return new AirportSuggestion(trimmed, "", "", iata, $"{trimmed} - {iata}");
    }

    var suggestions = await SuggestAirportsAsync(chatClient, trimmed);
    return suggestions.FirstOrDefault();
}

static List<AirportSuggestion> ParseAirportSuggestions(string text)
{
    var json = ExtractJsonArray(text);
    using var document = JsonDocument.Parse(json);
    var suggestions = new List<AirportSuggestion>();

    foreach (var item in document.RootElement.EnumerateArray())
    {
        var city = GetJsonString(item, "city");
        var country = GetJsonString(item, "country");
        var airportName = GetJsonString(item, "airportName");
        var iata = GetJsonString(item, "iata").ToUpperInvariant();
        var label = GetJsonString(item, "label");

        if (Regex.IsMatch(iata, "^[A-Z]{3}$"))
        {
            suggestions.Add(new AirportSuggestion(city, country, airportName, iata, label));
        }
    }

    return suggestions;
}

static string ExtractJsonArray(string text)
{
    var start = text.IndexOf('[');
    var end = text.LastIndexOf(']');
    return start >= 0 && end > start ? text[start..(end + 1)] : "[]";
}

static Uri BuildScrapeDoFlightsUri(string token, string departureId, string arrivalId, string outboundDate, string returnDate)
{
    var query = new Dictionary<string, string?>
    {
        ["token"] = token,
        ["departure_id"] = departureId,
        ["arrival_id"] = arrivalId,
        ["outbound_date"] = outboundDate,
        ["return_date"] = returnDate,
        ["type"] = "1",
        ["adults"] = "1",
        ["travel_class"] = "1",
        ["currency"] = "USD",
        ["gl"] = "us",
        ["hl"] = "en",
        ["sort_by"] = "2"
    };

    var queryString = string.Join("&", query.Select(pair =>
        $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value ?? "")}"));

    return new Uri($"https://api.scrape.do/plugin/google/flights?{queryString}");
}

static string BuildGoogleFlightsUrl(string departureId, string arrivalId, string outboundDate, string returnDate)
{
    var query = Uri.EscapeDataString($"flights from {departureId} to {arrivalId} {outboundDate} to {returnDate}");
    return $"https://www.google.com/travel/flights?q={query}";
}

static List<FlightOption> ExtractFlightOptions(string json)
{
    using var document = JsonDocument.Parse(json);
    var options = new List<FlightOption>();

    AddFlightsFromProperty(document.RootElement, "best_flights", options);
    AddFlightsFromProperty(document.RootElement, "other_flights", options);

    return options
        .Where(option => option.Price is not null)
        .OrderBy(option => option.Price)
        .Take(10)
        .ToList();
}

static void AddFlightsFromProperty(JsonElement root, string propertyName, List<FlightOption> options)
{
    if (!root.TryGetProperty(propertyName, out var flights) || flights.ValueKind != JsonValueKind.Array)
    {
        return;
    }

    foreach (var itinerary in flights.EnumerateArray())
    {
        var segments = itinerary.GetProperty("flights").EnumerateArray().ToList();
        if (segments.Count == 0)
        {
            continue;
        }

        var first = segments.First();
        var last = segments.Last();
        var airlines = segments
            .Select(segment => GetJsonString(segment, "airline"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct()
            .ToArray();

        options.Add(new FlightOption(
            GetJsonInt(itinerary, "price"),
            string.Join(", ", airlines),
            GetJsonInt(itinerary, "total_duration"),
            Math.Max(0, segments.Count - 1),
            GetAirportTime(first, "departure_airport"),
            GetAirportTime(last, "arrival_airport"),
            GetAirportId(first, "departure_airport"),
            GetAirportId(last, "arrival_airport"),
            GetJsonString(itinerary, "type")));
    }
}

static PriceInsight? ExtractPriceInsight(string json)
{
    using var document = JsonDocument.Parse(json);
    if (!document.RootElement.TryGetProperty("price_insights", out var insight))
    {
        return null;
    }

    int? low = null;
    int? high = null;
    if (insight.TryGetProperty("typical_price_range", out var range) &&
        range.ValueKind == JsonValueKind.Array &&
        range.GetArrayLength() >= 2)
    {
        low = range[0].GetInt32();
        high = range[1].GetInt32();
    }

    return new PriceInsight(
        GetJsonInt(insight, "lowest_price"),
        GetJsonString(insight, "price_level"),
        low,
        high);
}

static async Task<string> SummarizeFlightOptionsAsync(
    ChatClient chatClient,
    AirportSuggestion departure,
    AirportSuggestion destination,
    FlightSearchRequest search,
    List<FlightOption> flights,
    PriceInsight? priceInsight)
{
    if (flights.Count == 0)
    {
        return "No priced flight options were returned for this route and date range.";
    }

    var payload = JsonSerializer.Serialize(new
    {
        departure,
        destination,
        search.FromDate,
        search.ToDate,
        priceInsight,
        cheapestOptions = flights.Take(5)
    });

    var completion = await chatClient.CompleteChatAsync(
        messages:
        [
            new SystemChatMessage("""
                You summarize cheap flight search results for a traveler.
                Be concise. Mention the cheapest option, whether price looks low/typical/high when provided, and any tradeoffs such as stops or duration.
                Do not invent booking links or baggage rules.
                """),
            new UserChatMessage(payload)
        ]);

    return completion.Value.Content.Count > 0
        ? completion.Value.Content[0].Text
        : "Flight options were found, but no summary was returned.";
}

static string GetAirportTime(JsonElement segment, string propertyName)
{
    return segment.TryGetProperty(propertyName, out var airport)
        ? GetJsonString(airport, "time")
        : "";
}

static string GetAirportId(JsonElement segment, string propertyName)
{
    return segment.TryGetProperty(propertyName, out var airport)
        ? GetJsonString(airport, "id")
        : "";
}

static string GetJsonString(JsonElement element, string propertyName)
{
    return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString() ?? ""
        : "";
}

static int? GetJsonInt(JsonElement element, string propertyName)
{
    if (!element.TryGetProperty(propertyName, out var property))
    {
        return null;
    }

    return property.ValueKind switch
    {
        JsonValueKind.Number when property.TryGetInt32(out var value) => value,
        JsonValueKind.String when int.TryParse(property.GetString(), out var value) => value,
        _ => null
    };
}

record AirportSuggestion(string City, string Country, string AirportName, string Iata, string Label);

record FlightSearchRequest(string FromDate, string ToDate, string Destination, string Departure);

record FlightSearchResponse(
    AirportSuggestion Departure,
    AirportSuggestion Destination,
    string FromDate,
    string ToDate,
    bool LiveFaresConfigured,
    string Summary,
    List<FlightOption> Flights,
    PriceInsight? PriceInsight,
    string FallbackSearchUrl);

record FlightOption(
    int? Price,
    string Airlines,
    int? TotalDuration,
    int Stops,
    string DepartureTime,
    string ArrivalTime,
    string DepartureAirport,
    string ArrivalAirport,
    string Type);

record PriceInsight(int? LowestPrice, string PriceLevel, int? TypicalLow, int? TypicalHigh);
