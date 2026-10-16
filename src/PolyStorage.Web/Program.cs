using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http.Json;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<ModelApiClient>();
builder.Services.AddSingleton<PolymerService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Initialize polymer data on startup in background or first call
var polymerService = app.Services.GetRequiredService<PolymerService>();
_ = Task.Run(async () =>
{
    try
    {
        await polymerService.EnsureDataLoadedAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[PolymerService] Error loading CSV: {ex.Message}");
    }
});

app.MapGet("/api/health", () => Results.Ok(new { status = "online", framework = ".NET 10", service = "PolyStorage.Web" }));

app.MapGet("/api/polymers/search", async (string? q, PolymerService svc) =>
{
    await svc.EnsureDataLoadedAsync();
    var results = svc.Search(q ?? string.Empty, limit: 10);
    return Results.Ok(results);
});

app.MapGet("/api/polymers/lookup", async (string? query, PolymerService svc) =>
{
    await svc.EnsureDataLoadedAsync();
    var match = svc.FindMatch(query ?? string.Empty);
    if (match != null)
    {
        return Results.Ok(new { found = true, source = "CSV", polymer = match });
    }
    return Results.Ok(new { found = false, source = "MODEL_ESTIMATE", polymer = (PolymerRecord?)null });
});

app.MapPost("/api/polymers/resolve", async (PolymerResolveRequest request, PolymerService svc, ModelApiClient modelApi, CancellationToken cancellationToken) =>
{
    await svc.EnsureDataLoadedAsync();
    var lookup = request.Query ?? request.PSMILES ?? request.SMILES ?? string.Empty;
    var match = svc.FindMatch(lookup);

    if (match is not null)
    {
        // Preserve every value already recorded in the CSV. Ask the model only to fill missing properties.
        var hasMissingProperty = string.IsNullOrWhiteSpace(match.Tg_C)
            || string.IsNullOrWhiteSpace(match.Tm_C)
            || string.IsNullOrWhiteSpace(match.Td_C);
        var structure = !string.IsNullOrWhiteSpace(match.PSMILES) ? match.PSMILES : match.SMILES;
        if (hasMissingProperty && !string.IsNullOrWhiteSpace(structure))
        {
            try
            {
                var predictions = await modelApi.PredictAsync(match.PSMILES, match.SMILES, cancellationToken);
                if (string.IsNullOrWhiteSpace(match.Tg_C) && predictions.Tg is double tg)
                {
                    match.Tg_C = tg.ToString("0.##", CultureInfo.InvariantCulture);
                    match.Tg_Source = "Model API";
                }
                if (string.IsNullOrWhiteSpace(match.Tm_C) && predictions.Tm is double tm)
                {
                    match.Tm_C = tm.ToString("0.##", CultureInfo.InvariantCulture);
                    match.Tm_Source = "Model API";
                }
                if (string.IsNullOrWhiteSpace(match.Td_C) && predictions.Td is double td)
                {
                    match.Td_C = td.ToString("0.##", CultureInfo.InvariantCulture);
                    match.Td_Source = "Model API";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
            {
                Console.WriteLine($"[ModelApi] Could not fill missing CSV properties: {ex.Message}");
            }
        }

        return Results.Ok(new { found = true, source = "CSV", polymer = match });
    }

    var repeatUnit = !string.IsNullOrWhiteSpace(request.PSMILES) ? request.PSMILES : request.SMILES;
    if (string.IsNullOrWhiteSpace(repeatUnit))
    {
        return Results.BadRequest(new { error = "No CSV match. Enter a PSMILES or SMILES repeat-unit string for model prediction." });
    }

    try
    {
        var predictions = await modelApi.PredictAsync(request.PSMILES, request.SMILES, cancellationToken);
        return Results.Ok(new
        {
            found = false,
            source = "MODEL_API",
            name = lookup,
            input = repeatUnit,
            unit = "°C",
            predictions = new { Tg = predictions.Tg, Tm = predictions.Tm, Td = predictions.Td }
        });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
    {
        Console.WriteLine($"[ModelApi] Prediction failed: {ex.Message}");
        return Results.Problem(
            title: "Model API unavailable",
            detail: "The CSV had no matching record and the model API could not return a prediction. Check that the model service and ngrok tunnel are running.",
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.Run();


public sealed record PolymerResolveRequest(string? Query, string? PSMILES, string? SMILES);
public sealed record ThermalPredictions(double? Tg, double? Tm, double? Td);

public sealed class ModelApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _predictPath;

    public ModelApiClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(configuration.GetValue("ModelApi:TimeoutSeconds", 20));
        _baseUrl = configuration["ModelApi:BaseUrl"] ?? throw new InvalidOperationException("ModelApi:BaseUrl is not configured.");
        _predictPath = configuration["ModelApi:PredictPath"] ?? "/predict";
    }

    public async Task<ThermalPredictions> PredictAsync(string? psmiles, string? smiles, CancellationToken cancellationToken)
    {
        var baseUrl = _baseUrl.EndsWith('/') ? _baseUrl : _baseUrl + "/";
        var path = _predictPath.TrimStart('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(baseUrl), path))
        {
            Content = JsonContent.Create(new Dictionary<string, string?> { ["PSMILES"] = psmiles, ["SMILES"] = smiles })
        };
        request.Headers.TryAddWithoutValidation("ngrok-skip-browser-warning", "true");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Model API returned HTTP {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ModelApiResponse>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }, cancellationToken);
        if (payload?.Predictions is null)
        {
            throw new InvalidDataException("Model API response did not contain predictions.");
        }

        return new ThermalPredictions(
            GetValue(payload.Predictions, "Tg"),
            GetValue(payload.Predictions, "Tm"),
            GetValue(payload.Predictions, "Td"));
    }

    private static double? GetValue(Dictionary<string, double> values, string key) =>
        values.TryGetValue(key, out var value) && double.IsFinite(value) ? value : null;

    private sealed class ModelApiResponse
    {
        public Dictionary<string, double>? Predictions { get; set; }
    }
}
public class PolymerRecord
{
    public string PolymerId { get; set; } = "";
    public string PolymerName { get; set; } = "";
    public string SMILES { get; set; } = "";
    public string PSMILES { get; set; } = "";
    public string? Tg_C { get; set; }
    public string? Tg_Source { get; set; }
    public string? Tm_C { get; set; }
    public string? Tm_Source { get; set; }
    public string? Td_C { get; set; }
    public string? Td_Source { get; set; }
}

public class PolymerService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebHostEnvironment _env;
    private readonly List<PolymerRecord> _records = new();
    private bool _isLoaded = false;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private const string RemoteCsvUrl = "https://qkxqklnvwjtmltyabcyv.supabase.co/storage/v1/object/public/Data/polymer_master.csv";

    public PolymerService(IHttpClientFactory httpClientFactory, IWebHostEnvironment env)
    {
        _httpClientFactory = httpClientFactory;
        _env = env;
    }

    public async Task EnsureDataLoadedAsync()
    {
        if (_isLoaded) return;
        await _lock.WaitAsync();
        try
        {
            if (_isLoaded) return;

            string localCsvPath = Path.Combine(_env.ContentRootPath, "polymer_master.csv");
            if (!File.Exists(localCsvPath))
            {
                Console.WriteLine("[PolymerService] Local CSV missing. Downloading from Supabase...");
                var client = _httpClientFactory.CreateClient();
                var data = await client.GetByteArrayAsync(RemoteCsvUrl);
                await File.WriteAllBytesAsync(localCsvPath, data);
            }

            ParseCsv(localCsvPath);
            _isLoaded = true;
            Console.WriteLine($"[PolymerService] Successfully loaded {_records.Count} polymers from CSV.");
        }
        finally
        {
            _lock.Release();
        }
    }

    private void ParseCsv(string filePath)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        string? headerLine = reader.ReadLine();
        if (headerLine == null) return;

        var headers = ParseCsvLine(headerLine);
        var idxMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
        {
            idxMap[headers[i].Trim()] = i;
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cols = ParseCsvLine(line);

            string GetCol(string name)
            {
                if (idxMap.TryGetValue(name, out int idx) && idx < cols.Count)
                {
                    return cols[idx].Trim();
                }
                return "";
            }

            var rec = new PolymerRecord
            {
                PolymerId = GetCol("polymer_id"),
                PolymerName = GetCol("polymer_name"),
                SMILES = GetCol("SMILES"),
                PSMILES = GetCol("PSMILES"),
                Tg_C = GetColOrNull(GetCol("Tg_C")),
                Tg_Source = GetColOrNull(GetCol("Tg_source")),
                Tm_C = GetColOrNull(GetCol("Tm_C")),
                Tm_Source = GetColOrNull(GetCol("Tm_source")),
                Td_C = GetColOrNull(GetCol("Td_C")),
                Td_Source = GetColOrNull(GetCol("Td_source"))
            };

            // If PSMILES is empty, fallback to SMILES or PSMILES_2
            if (string.IsNullOrEmpty(rec.PSMILES))
            {
                rec.PSMILES = GetCol("PSMILES_2");
                if (string.IsNullOrEmpty(rec.PSMILES))
                {
                    rec.PSMILES = rec.SMILES;
                }
            }

            _records.Add(rec);
        }
    }

    private static string? GetColOrNull(string val) => string.IsNullOrWhiteSpace(val) ? null : val;

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        result.Add(current.ToString());
        return result;
    }

    public List<PolymerRecord> Search(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return _records.Take(limit).ToList();
        }

        query = query.Trim();
        return _records.Where(r =>
            (!string.IsNullOrEmpty(r.PolymerName) && r.PolymerName.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.PSMILES) && r.PSMILES.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.SMILES) && r.SMILES.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.PolymerId) && r.PolymerId.Contains(query, StringComparison.OrdinalIgnoreCase))
        ).Take(limit).ToList();
    }

    public PolymerRecord? FindMatch(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        // Exact matches
        var exact = _records.FirstOrDefault(r =>
            string.Equals(r.PolymerName, input, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.PSMILES, input, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.SMILES, input, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.PolymerId, input, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Allow partial lookup for human-readable names and IDs, but never match a
        // structure by substring: short SMILES (for example, CCO) are common in
        // unrelated repeat-unit structures and must not replace the user's input.
        return _records.FirstOrDefault(r =>
            (!string.IsNullOrEmpty(r.PolymerName) && r.PolymerName.Contains(input, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.PolymerId) && r.PolymerId.Contains(input, StringComparison.OrdinalIgnoreCase)));
    }
}
