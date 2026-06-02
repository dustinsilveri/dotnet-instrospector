using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace DotnetIntrospector.Services;

public sealed class CweLookupService
{
    private static readonly Regex TitleRegex = new(
        "<title>\\s*CWE\\s*-\\s*(?<value>.*?)\\s*</title>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DescriptionRegex = new(
        "<div\\s+name=\\\"oc_\\d+_Description\\\"[^>]*>(?<value>.*?)</div>\\s*</div>\\s*<div\\s+id=\\\"Extended_Description\\\"",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<.*?>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IntrospectionPersistenceService _persistenceService;
    private readonly ILogger<CweLookupService> _logger;
    private readonly ConcurrentDictionary<int, CweItem> _cache = new();

    public CweLookupService(
        IHttpClientFactory httpClientFactory,
        IntrospectionPersistenceService persistenceService,
        ILogger<CweLookupService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _persistenceService = persistenceService;
        _logger = logger;
    }

    public async Task<CweLookupResponse> LookupAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
    {
        var items = new List<CweItem>();
        var warnings = new List<string>();
        var storedItems = await TryGetStoredItemsAsync(ids, warnings, cancellationToken);

        foreach (var id in ids)
        {
            if (storedItems.TryGetValue(id, out var storedItem))
            {
                if (storedItem.IsDeleted)
                {
                    _cache.TryRemove(id, out _);
                    warnings.Add($"CWE-{id} is deleted in the local catalog.");
                    continue;
                }

                var persistedItem = ToCweItem(storedItem);
                _cache[id] = persistedItem;
                items.Add(persistedItem);
                continue;
            }

            if (_cache.TryGetValue(id, out var cached))
            {
                items.Add(cached);
                continue;
            }

            try
            {
                var fetched = await FetchFromMitreAsync(id, cancellationToken);
                _cache[id] = fetched;
                items.Add(fetched);
                _logger.LogInformation("Fetched CWE-{Id} from MITRE, persisting to catalog...", id);
                await TryPersistItemAsync(fetched, warnings, cancellationToken);
            }
            catch (Exception ex)
            {
                warnings.Add($"CWE-{id} could not be retrieved from MITRE: {ex.Message}");
                _logger.LogError(ex, "Failed to retrieve CWE-{Id} from MITRE", id);
            }
        }

        return new CweLookupResponse(
            DateTimeOffset.UtcNow,
            items.OrderBy(item => item.Id).ToArray(),
            warnings);
    }

    public async Task UpsertAsync(CweItem item, CancellationToken cancellationToken)
    {
        await _persistenceService.UpsertCweItemAsync(item, cancellationToken);
        _cache[item.Id] = item;
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        await _persistenceService.DeleteCweItemAsync(id, cancellationToken);
        _cache.TryRemove(id, out _);
    }

    private async Task<CweItem> FetchFromMitreAsync(int id, CancellationToken cancellationToken)
    {
        var url = $"https://cwe.mitre.org/data/definitions/{id}.html";
        var client = _httpClientFactory.CreateClient(nameof(CweLookupService));

        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        var rawTitle = MatchOrDefault(TitleRegex, html, "value", $"CWE-{id}");
        var title = NormalizeText(RemoveVersionSuffix(rawTitle));

        var rawDescription = MatchOrDefault(DescriptionRegex, html, "value", "Description not available from MITRE.");
        var description = NormalizeText(rawDescription);

        return new CweItem(id, title, description, url);
    }

    private static string MatchOrDefault(Regex regex, string input, string group, string fallback)
    {
        var match = regex.Match(input);
        if (!match.Success)
        {
            return fallback;
        }

        var value = match.Groups[group].Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string RemoveVersionSuffix(string title)
    {
        return Regex.Replace(title, "\\s*\\([0-9]+\\.[0-9]+(\\.[0-9]+)?\\)\\s*$", string.Empty);
    }

    private static string NormalizeText(string htmlFragment)
    {
        var withoutTags = TagRegex.Replace(htmlFragment, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private async Task<Dictionary<int, StoredCweItem>> TryGetStoredItemsAsync(
        IReadOnlyList<int> ids,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var storedItems = await _persistenceService.GetCweItemsAsync(ids, cancellationToken);
            return storedItems.ToDictionary(item => item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CWE catalog lookup unavailable. Falling back to live MITRE fetch.");
            warnings.Add("Local CWE persistence is unavailable. Showing live MITRE data only.");
            return new Dictionary<int, StoredCweItem>();
        }
    }

    private async Task TryPersistItemAsync(CweItem item, List<string> warnings, CancellationToken cancellationToken)
    {
        try
        {
            await _persistenceService.UpsertCweItemAsync(item, cancellationToken);
            _logger.LogInformation("Successfully persisted CWE-{Id} to catalog.", item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist fetched CWE-{Id} entry.", item.Id);
            warnings.Add($"CWE-{item.Id} was loaded from MITRE but could not be persisted locally.");
        }
    }

    private static CweItem ToCweItem(StoredCweItem item)
    {
        return new CweItem(item.Id, item.Name, item.Description, item.SourceUrl);
    }
}
