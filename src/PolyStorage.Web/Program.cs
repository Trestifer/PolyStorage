using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
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

app.Run();

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

        // Substring / Contains match
        return _records.FirstOrDefault(r =>
            (!string.IsNullOrEmpty(r.PolymerName) && r.PolymerName.Contains(input, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.PSMILES) && r.PSMILES.Contains(input, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(r.SMILES) && r.SMILES.Contains(input, StringComparison.OrdinalIgnoreCase)));
    }
}
