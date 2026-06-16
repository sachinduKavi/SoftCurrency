using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SoftCurrency;

/// <summary>Exception thrown for any failure while talking to the exchange rate API.</summary>
public sealed class SoftCurrencyException(string message, string code) : Exception(message)
{
    /// <summary>Machine-readable error code (e.g. "invalid-key", "network-error").</summary>
    public string Code { get; } = code;
}

/// <summary>Configuration options for <see cref="SoftCurrency"/>.</summary>
public sealed class SoftCurrencyOptions
{
    /// <summary>
    /// How long fetched rates stay fresh. Default: 1 hour.
    /// Set to <see cref="TimeSpan.Zero"/> to always fetch fresh rates.
    /// </summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Optional <see cref="HttpClient"/> to use for requests.
    /// If not provided, an internal one is created and disposed with this instance.
    /// </summary>
    public HttpClient? HttpClient { get; init; }
}

/// <summary>
/// Lightweight currency conversion client backed by <see href="https://www.exchangerate-api.com"/>.
/// Rates are cached in-memory to minimise API calls.
/// </summary>
public sealed partial class SoftCurrency : ISoftCurrency, IDisposable
{
    private const string ApiBaseUrl = "https://v6.exchangerate-api.com/v6";

    private static readonly Dictionary<string, string> ErrorMessages = new()
        {
            ["unsupported-code"] = "The supplied currency code is not supported.",
            ["malformed-request"] = "The request to the exchange rate API was malformed.",
            ["invalid-key"] = "The supplied API key is not valid.",
            ["inactive-account"] = "Your API account is inactive (email address not confirmed).",
            ["quota-reached"] = "Your API account has reached its request quota.",
        };

    private readonly string _apiKey;
    private readonly TimeSpan _cacheTtl;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();

    private sealed record CacheEntry(IReadOnlyDictionary<string, double> Rates, DateTimeOffset FetchedAt);

    /// <param name="apiKey">Your <see href="https://www.exchangerate-api.com"/> API key.</param>
    /// <param name="options">Optional configuration (cache TTL, custom HttpClient).</param>
    /// <exception cref="SoftCurrencyException">Thrown when <paramref name="apiKey"/> is null or empty.</exception>
    public SoftCurrency(string apiKey, SoftCurrencyOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new SoftCurrencyException("An API key is required.", "invalid-argument");

        _apiKey = apiKey.Trim();
        _cacheTtl = options?.CacheTtl ?? TimeSpan.FromHours(1);

        if (options?.HttpClient is { } client)
        {
            _httpClient = client;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
    }

    /// <summary>Fetch (or return cached) conversion rates for a base currency.</summary>
    /// <param name="baseCurrency">ISO 4217 code, e.g. "USD".</param>
    /// <returns>Map of currency code → rate relative to <paramref name="baseCurrency"/>.</returns>
    public async Task<IReadOnlyDictionary<string, double>> GetRatesAsync(
        string baseCurrency,
        CancellationToken cancellationToken = default)
    {
        var baseCode = NormalizeCode(baseCurrency, "baseCurrency");

        if (_cache.TryGetValue(baseCode, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < _cacheTtl)
        {
            return cached.Rates;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(
                $"{ApiBaseUrl}/{_apiKey}/latest/{baseCode}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is not (SoftCurrencyException or OperationCanceledException))
        {
            throw new SoftCurrencyException(
                $"Network error while fetching rates for {baseCode}: {ex.Message}",
                "network-error");
        }

        string responseBody;
        try
        {
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch
        {
            responseBody = "{}";
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        var apiResult = root.TryGetProperty("result", out var resultProp)
            ? resultProp.GetString()
            : null;

        if (!response.IsSuccessStatusCode || apiResult != "success")
        {
            var errorType = root.TryGetProperty("error-type", out var et)
                ? et.GetString() ?? $"http-{(int)response.StatusCode}"
                : $"http-{(int)response.StatusCode}";

            var message = ErrorMessages.TryGetValue(errorType, out var msg)
                ? msg
                : $"Exchange rate API request failed ({errorType}).";

            throw new SoftCurrencyException(message, errorType);
        }

        var ratesElement = root.GetProperty("conversion_rates");
        var rates = new Dictionary<string, double>();
        foreach (var prop in ratesElement.EnumerateObject())
            rates[prop.Name] = prop.Value.GetDouble();

        var entry = new CacheEntry(rates, DateTimeOffset.UtcNow);
        _cache[baseCode] = entry;
        return rates;
    }

    /// <summary>Get the conversion rate from one currency to another.</summary>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrency">ISO 4217 target currency, e.g. "EUR".</param>
    public async Task<double> GetRateAsync(
        string baseCurrency,
        string targetCurrency,
        CancellationToken cancellationToken = default)
    {
        var target = NormalizeCode(targetCurrency, "targetCurrency");
        var rates = await GetRatesAsync(baseCurrency, cancellationToken);

        if (!rates.TryGetValue(target, out var rate))
            throw new SoftCurrencyException(
                $"Currency code {target} was not found in the returned rates.",
                "unsupported-code");

        return rate;
    }

    /// <summary>Convert an amount from one currency to another.</summary>
    /// <param name="amount">The amount to convert.</param>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrency">ISO 4217 target currency, e.g. "EUR".</param>
    /// <returns>The converted amount in <paramref name="targetCurrency"/>.</returns>
    public async Task<double> ConvertAsync(
        double amount,
        string baseCurrency,
        string targetCurrency,
        CancellationToken cancellationToken = default)
    {
        AssertAmount(amount);
        var rate = await GetRateAsync(baseCurrency, targetCurrency, cancellationToken);
        return amount * rate;
    }

    /// <summary>
    /// Convert an amount from a base currency into multiple target currencies
    /// using a single API request.
    /// </summary>
    /// <param name="amount">The amount to convert.</param>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrencies">ISO 4217 target currencies, e.g. ["EUR", "GBP", "JPY"].</param>
    /// <returns>Map of currency code → converted amount.</returns>
    public async Task<IReadOnlyDictionary<string, double>> ConvertManyAsync(
        double amount,
        string baseCurrency,
        IEnumerable<string> targetCurrencies,
        CancellationToken cancellationToken = default)
    {
        AssertAmount(amount);

        var targets = (targetCurrencies ?? throw new SoftCurrencyException(
            "targetCurrencies must be a non-empty collection.", "invalid-argument"))
            .ToList();

        if (targets.Count == 0)
            throw new SoftCurrencyException(
                "targetCurrencies must be a non-empty collection.", "invalid-argument");

        var normalizedTargets = targets
            .Select(c => NormalizeCode(c, "targetCurrencies[]"))
            .ToList();

        var rates = await GetRatesAsync(baseCurrency, cancellationToken);
        var result = new Dictionary<string, double>(normalizedTargets.Count);

        foreach (var target in normalizedTargets)
        {
            if (!rates.TryGetValue(target, out var rate))
                throw new SoftCurrencyException(
                    $"Currency code {target} was not found in the returned rates.",
                    "unsupported-code");

            result[target] = amount * rate;
        }

        return result;
    }

    /// <summary>List all currency codes supported for a given base currency.</summary>
    /// <param name="baseCurrency">ISO 4217 base currency. Default: "USD".</param>
    public async Task<IReadOnlyList<string>> SupportedCurrenciesAsync(
        string baseCurrency = "USD",
        CancellationToken cancellationToken = default)
    {
        var rates = await GetRatesAsync(baseCurrency, cancellationToken);
        return [.. rates.Keys];
    }

    /// <summary>Drop all cached rates so the next call fetches fresh data.</summary>
    public void ClearCache() => _cache.Clear();

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static readonly Regex CurrencyCodeRegex = new(@"^[A-Za-z]{3}$", RegexOptions.Compiled);

    private static string NormalizeCode(string? code, string label)
    {
        if (code is null || !CurrencyCodeRegex.IsMatch(code.Trim()))
            throw new SoftCurrencyException(
                $"{label} must be a 3-letter ISO 4217 currency code, got: {JsonSerializer.Serialize(code)}",
                "invalid-argument");

        return code.Trim().ToUpperInvariant();
    }

    private static void AssertAmount(double amount)
    {
        if (!double.IsFinite(amount))
            throw new SoftCurrencyException(
                $"amount must be a finite number, got: {amount}",
                "invalid-argument");
    }
}
