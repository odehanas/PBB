using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GovBudget.Services.Assistant
{
    /// <summary>
    /// OECD knowledge for the assistant: an offline reference document shipped with the
    /// application (always available) and live reads from OECD's public SDMX API and
    /// website (allow-listed hosts only).
    /// </summary>
    public sealed class OecdKnowledgeToolProvider : IAssistantToolProvider
    {
        public const string HttpClientName = "oecd";

        private static readonly Regex HtmlScriptOrStyle =
            new("<(script|style)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly Regex HtmlTag = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);

        private static readonly Regex DataflowEntry = new(
            "<(?:\\w+:)?Dataflow id=\"([^\"]+)\" agencyID=\"([^\"]+)\" version=\"([^\"]+)\".*?<(?:\\w+:)?Name[^>]*>(.*?)</(?:\\w+:)?Name>",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly SemaphoreSlim CatalogueLock = new(1, 1);
        private static IReadOnlyList<OecdDataflow>? _catalogue;
        private static DateTimeOffset _catalogueLoadedAt;

        private sealed record OecdDataflow(string Reference, string Name);

        private readonly AssistantOptions _options;
        private readonly IHostEnvironment _env;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OecdKnowledgeToolProvider> _logger;

        public OecdKnowledgeToolProvider(
            IOptions<AssistantOptions> options,
            IHostEnvironment env,
            IHttpClientFactory httpClientFactory,
            ILogger<OecdKnowledgeToolProvider> logger)
        {
            _options = options.Value;
            _env = env;
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        public IEnumerable<AssistantToolDefinition> GetTools()
        {
            yield return new AssistantToolDefinition(
                "search_pbb_reference",
                "Search the built-in OECD performance-based budgeting reference: principles, good practices, standard ratios and indicator definitions. Use this first for methodology questions.",
                """
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Keywords, e.g. 'cost per output', 'execution rate', 'programme budgeting'."}
                },"required":["query"],"additionalProperties":false}
                """,
                SearchReferenceAsync);

            if (!_options.OecdLiveEnabled) yield break;

            yield return new AssistantToolDefinition(
                "oecd_find_dataflow",
                "Search the OECD SDMX catalogue for dataflows matching keywords and return their exact identifiers. Always call this before oecd_data_query - identifiers guessed from memory do not exist.",
                """
                {"type":"object","properties":{
                  "query":{"type":"string","description":"Keywords, e.g. 'government expenditure cofog' or 'public finance'."}
                },"required":["query"],"additionalProperties":false}
                """,
                FindDataflowAsync);

            yield return new AssistantToolDefinition(
                "oecd_data_query",
                "Query the OECD public SDMX data API for a dataflow and return the observations as JSON. Use when the user asks for current OECD statistics or international comparators. The dataflow must come from oecd_find_dataflow.",
                """
                {"type":"object","properties":{
                  "dataflow":{"type":"string","description":"Exact 'agency,dataflow,version' from oecd_find_dataflow, e.g. 'OECD.GOV.GIP,DSD_GOV@DF_GOV_PF_2025,1.0'."},
                  "key":{"type":"string","description":"SDMX series key, e.g. 'ARE+FRA.A.GG_EXP...'. Use 'all' when unsure."},
                  "start_period":{"type":"string","description":"e.g. 2019"},
                  "end_period":{"type":"string","description":"e.g. 2024"}
                },"required":["dataflow"],"additionalProperties":false}
                """,
                QueryOecdDataAsync);

            yield return new AssistantToolDefinition(
                "oecd_read_page",
                "Fetch an OECD web page (oecd.org only) and return its readable text. Use for OECD guidance, country notes or definitions that are not in the built-in reference.",
                """
                {"type":"object","properties":{
                  "url":{"type":"string","description":"Absolute https URL on an oecd.org host."}
                },"required":["url"],"additionalProperties":false}
                """,
                ReadOecdPageAsync);
        }

        // ---------------- offline reference ----------------

        private async Task<string> SearchReferenceAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var path = Path.Combine(_env.ContentRootPath, _options.ReferenceDocPath);

            if (!File.Exists(path))
            {
                return JsonSerializer.Serialize(new { error = "The built-in OECD reference document is not installed." });
            }

            var text = await File.ReadAllTextAsync(path, ct);

            // Sections are "## " headings; score each by how many query terms it contains.
            var sections = Regex.Split(text, @"^##\s+", RegexOptions.Multiline)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            var terms = Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9%]+")
                .Where(t => t.Length > 2)
                .Distinct()
                .ToList();

            var matches = sections
                .Select(s => new
                {
                    title = s.Split('\n')[0].Trim(),
                    body = s,
                    score = terms.Count == 0 ? 0 : terms.Count(t => s.ToLowerInvariant().Contains(t))
                })
                .Where(s => s.score > 0)
                .OrderByDescending(s => s.score)
                .Take(4)
                .Select(s => new { s.title, content = Truncate(s.body, 4000) })
                .ToList();

            if (matches.Count == 0)
            {
                var headings = sections.Select(s => s.Split('\n')[0].Trim()).ToList();
                return JsonSerializer.Serialize(new { query, matches = Array.Empty<object>(), available_sections = headings });
            }

            return JsonSerializer.Serialize(new { query, source = "Built-in OECD PBB reference (docs/OECD_PBB_Reference.md)", matches });
        }

        // ---------------- live OECD ----------------

        private async Task<string> FindDataflowAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
            var terms = Regex.Split(query.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(t => t.Length > 2)
                .Distinct()
                .ToList();

            IReadOnlyList<OecdDataflow> catalogue;
            try
            {
                catalogue = await GetCatalogueAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OECD dataflow catalogue could not be loaded");
                return JsonSerializer.Serialize(new { error = "The OECD SDMX catalogue could not be reached." });
            }

            var matches = catalogue
                .Select(f => new
                {
                    f.Reference,
                    f.Name,
                    score = terms.Count == 0
                        ? 0
                        : terms.Count(t => f.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                                        || f.Reference.Contains(t, StringComparison.OrdinalIgnoreCase))
                })
                .Where(f => f.score > 0)
                .OrderByDescending(f => f.score)
                .ThenBy(f => f.Name)
                .Take(15)
                .Select(f => new { dataflow = f.Reference, name = f.Name })
                .ToList();

            return JsonSerializer.Serialize(new
            {
                query,
                source = $"{_options.OecdApiBaseUrl.TrimEnd('/')}/dataflow/all/all/latest",
                matches
            });
        }

        private async Task<IReadOnlyList<OecdDataflow>> GetCatalogueAsync(CancellationToken ct)
        {
            if (_catalogue is not null && DateTimeOffset.UtcNow - _catalogueLoadedAt < TimeSpan.FromHours(12))
            {
                return _catalogue;
            }

            await CatalogueLock.WaitAsync(ct);
            try
            {
                if (_catalogue is not null && DateTimeOffset.UtcNow - _catalogueLoadedAt < TimeSpan.FromHours(12))
                {
                    return _catalogue;
                }

                var client = _httpClientFactory.CreateClient(HttpClientName);
                var xml = await client.GetStringAsync(
                    $"{_options.OecdApiBaseUrl.TrimEnd('/')}/dataflow/all/all/latest", ct);

                var flows = DataflowEntry.Matches(xml)
                    .Select(m => new OecdDataflow(
                        $"{m.Groups[2].Value},{m.Groups[1].Value},{m.Groups[3].Value}",
                        System.Net.WebUtility.HtmlDecode(m.Groups[4].Value).Trim()))
                    .ToList();

                _catalogue = flows;
                _catalogueLoadedAt = DateTimeOffset.UtcNow;
                return flows;
            }
            finally
            {
                CatalogueLock.Release();
            }
        }

        private async Task<string> QueryOecdDataAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var dataflow = args.TryGetProperty("dataflow", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(dataflow))
            {
                return JsonSerializer.Serialize(new { error = "dataflow is required." });
            }

            var key = args.TryGetProperty("key", out var k) ? k.GetString() : null;
            var start = args.TryGetProperty("start_period", out var s) ? s.GetString() : null;
            var end = args.TryGetProperty("end_period", out var e) ? e.GetString() : null;

            string resolved;
            try
            {
                var catalogue = await GetCatalogueAsync(ct);
                var match = ResolveDataflow(catalogue, dataflow);
                if (match is null)
                {
                    return JsonSerializer.Serialize(new
                    {
                        error = $"'{dataflow}' is not a dataflow in the OECD catalogue.",
                        hint = "Call oecd_find_dataflow and use one of the identifiers it returns verbatim."
                    });
                }
                resolved = match.Reference;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OECD dataflow catalogue could not be loaded");
                resolved = dataflow;
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);

                // Narrow query first; if the OECD returns nothing, widen the key and drop the
                // period filter rather than reporting an empty result the user cannot act on.
                var attempts = new List<string> { BuildDataUrl(resolved, key, start, end) };
                if (!string.IsNullOrWhiteSpace(start) || !string.IsNullOrWhiteSpace(end))
                {
                    attempts.Add(BuildDataUrl(resolved, key, null, null));
                }
                if (!string.IsNullOrWhiteSpace(key) && key != "all")
                {
                    attempts.Add(BuildDataUrl(resolved, null, null, null));
                }

                string? lastError = null;
                string? lastUrl = null;

                foreach (var url in attempts)
                {
                    lastUrl = url;
                    using var response = await client.GetAsync(url, ct);
                    var body = await response.Content.ReadAsStringAsync(ct);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"OECD API returned {(int)response.StatusCode}.";
                        continue;
                    }

                    if (!HasObservations(body))
                    {
                        lastError = "The OECD dataflow returned no observations for that selection.";
                        continue;
                    }

                    var observations = ParseObservations(body, 120);
                    return JsonSerializer.Serialize(new
                    {
                        source = url,
                        dataflow = resolved,
                        observation_count = observations.Count,
                        note = "Report these observations exactly as given, with their unit. Do not compute ratios, shares or percentages from them.",
                        observations
                    });
                }

                return JsonSerializer.Serialize(new
                {
                    error = lastError ?? "No data was returned.",
                    hint = "Try a different dataflow from oecd_find_dataflow, or answer from the built-in reference instead.",
                    dataflow = resolved,
                    url = lastUrl
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OECD data query failed for {Dataflow}", resolved);
                return JsonSerializer.Serialize(new { error = "The OECD data service could not be reached.", dataflow = resolved });
            }
        }

        private string BuildDataUrl(string dataflow, string? key, string? start, string? end)
        {
            // SDMX path segments carry ',', '@' and '+' verbatim; percent-encoding them fails upstream.
            var url = $"{_options.OecdApiBaseUrl.TrimEnd('/')}/data/{SdmxSegment(dataflow)}/{SdmxSegment(string.IsNullOrWhiteSpace(key) ? "all" : key)}?format=jsondata&dimensionAtObservation=AllDimensions";
            if (!string.IsNullOrWhiteSpace(start)) url += $"&startPeriod={Uri.EscapeDataString(start)}";
            if (!string.IsNullOrWhiteSpace(end)) url += $"&endPeriod={Uri.EscapeDataString(end)}";
            return url;
        }

        /// <summary>
        /// Accepts an exact 'agency,dataflow,version' reference, or repairs one where the model
        /// dropped the agency, the version or the DSD prefix of the dataflow id.
        /// </summary>
        private static OecdDataflow? ResolveDataflow(IReadOnlyList<OecdDataflow> catalogue, string requested)
        {
            var wanted = requested.Trim();
            var exact = catalogue.FirstOrDefault(f => f.Reference.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var parts = wanted.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var id = parts.Length >= 2 ? parts[1] : parts.FirstOrDefault() ?? wanted;
            var agency = parts.Length >= 2 ? parts[0] : null;

            bool IdMatches(OecdDataflow f)
            {
                var flowId = f.Reference.Split(',')[1];
                return flowId.Equals(id, StringComparison.OrdinalIgnoreCase)
                    || flowId.EndsWith("@" + id, StringComparison.OrdinalIgnoreCase);
            }

            return catalogue.FirstOrDefault(f => IdMatches(f)
                       && (agency is null || f.Reference.StartsWith(agency + ",", StringComparison.OrdinalIgnoreCase)))
                   ?? catalogue.FirstOrDefault(IdMatches);
        }

        /// <summary>
        /// Turns an SDMX-JSON payload into flat observations - dimension labels plus the value -
        /// so the model reads real figures instead of guessing at a raw message.
        /// </summary>
        private static List<Dictionary<string, object?>> ParseObservations(string jsonBody, int max)
        {
            var rows = new List<Dictionary<string, object?>>();
            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                var data = doc.RootElement.GetProperty("data");
                if (!data.TryGetProperty("structures", out var structures) || structures.GetArrayLength() == 0)
                {
                    return rows;
                }

                foreach (var dataSet in data.GetProperty("dataSets").EnumerateArray())
                {
                    var structureIndex = dataSet.TryGetProperty("structure", out var si) && si.TryGetInt32(out var idx) ? idx : 0;
                    var dims = structures[structureIndex]
                        .GetProperty("dimensions")
                        .GetProperty("observation")
                        .EnumerateArray()
                        .Select(d => (
                            Id: d.GetProperty("id").GetString() ?? "",
                            Values: d.GetProperty("values").EnumerateArray()
                                .Select(v => v.TryGetProperty("name", out var n) ? n.GetString() : v.GetProperty("id").GetString())
                                .ToList()))
                        .ToList();

                    if (!dataSet.TryGetProperty("observations", out var obs) || obs.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    foreach (var ob in obs.EnumerateObject())
                    {
                        if (rows.Count >= max) return rows;

                        var indices = ob.Name.Split(':');
                        var row = new Dictionary<string, object?>();
                        for (var i = 0; i < dims.Count && i < indices.Length; i++)
                        {
                            if (int.TryParse(indices[i], out var vi) && vi >= 0 && vi < dims[i].Values.Count)
                            {
                                row[dims[i].Id] = dims[i].Values[vi];
                            }
                        }

                        var first = ob.Value.ValueKind == JsonValueKind.Array && ob.Value.GetArrayLength() > 0
                            ? ob.Value[0]
                            : default;
                        row["value"] = first.ValueKind == JsonValueKind.Number ? first.GetDouble() : (object?)null;
                        rows.Add(row);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return rows;
            }

            return rows;
        }

        private static bool HasObservations(string jsonBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonBody);
                if (!doc.RootElement.TryGetProperty("data", out var data)) return false;
                if (!data.TryGetProperty("dataSets", out var sets) || sets.ValueKind != JsonValueKind.Array) return false;

                foreach (var set in sets.EnumerateArray())
                {
                    if (set.TryGetProperty("observations", out var obs)
                        && obs.ValueKind == JsonValueKind.Object
                        && obs.EnumerateObject().Any())
                    {
                        return true;
                    }
                    if (set.TryGetProperty("series", out var series)
                        && series.ValueKind == JsonValueKind.Object
                        && series.EnumerateObject().Any())
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task<string> ReadOecdPageAsync(JsonElement args, AssistantUserContext user, CancellationToken ct)
        {
            var url = args.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return JsonSerializer.Serialize(new { error = "Provide an absolute https URL." });
            }

            var host = uri.Host.ToLowerInvariant();
            var allowed = _options.OecdAllowedHosts.Any(h =>
                host.Equals(h, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return JsonSerializer.Serialize(new { error = "Only OECD hosts may be read.", allowed_hosts = _options.OecdAllowedHosts });
            }

            try
            {
                var client = _httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.GetAsync(uri, ct);
                if (!response.IsSuccessStatusCode)
                {
                    return JsonSerializer.Serialize(new { error = $"OECD returned {(int)response.StatusCode}.", url });
                }

                var html = await response.Content.ReadAsStringAsync(ct);
                var text = HtmlScriptOrStyle.Replace(html, " ");
                text = HtmlTag.Replace(text, " ");
                text = System.Net.WebUtility.HtmlDecode(text);
                text = Whitespace.Replace(text, " ").Trim();

                return JsonSerializer.Serialize(new { source = url, text = Truncate(text, 10000) });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OECD page read failed for {Url}", url);
                return JsonSerializer.Serialize(new { error = "The OECD page could not be reached.", url });
            }
        }

        private static string SdmxSegment(string value) =>
            Regex.Replace(value.Trim(), @"[^A-Za-z0-9_,.@\-+:*]", "");

        private static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + " …[truncated]";
    }
}
