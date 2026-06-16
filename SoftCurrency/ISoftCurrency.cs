namespace SoftCurrency;

/// <summary>
/// Abstraction over <see cref="SoftCurrency"/> for dependency injection and testing.
/// </summary>
public interface ISoftCurrency
{
    /// <summary>Fetch (or return cached) conversion rates for a base currency.</summary>
    /// <param name="baseCurrency">ISO 4217 code, e.g. "USD".</param>
    /// <returns>Map of currency code → rate relative to <paramref name="baseCurrency"/>.</returns>
    Task<IReadOnlyDictionary<string, double>> GetRatesAsync(
        string baseCurrency,
        CancellationToken cancellationToken = default);

    /// <summary>Get the conversion rate from one currency to another.</summary>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrency">ISO 4217 target currency, e.g. "EUR".</param>
    Task<double> GetRateAsync(
        string baseCurrency,
        string targetCurrency,
        CancellationToken cancellationToken = default);

    /// <summary>Convert an amount from one currency to another.</summary>
    /// <param name="amount">The amount to convert.</param>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrency">ISO 4217 target currency, e.g. "EUR".</param>
    /// <returns>The converted amount in <paramref name="targetCurrency"/>.</returns>
    Task<double> ConvertAsync(
        double amount,
        string baseCurrency,
        string targetCurrency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Convert an amount from a base currency into multiple target currencies
    /// using a single API request.
    /// </summary>
    /// <param name="amount">The amount to convert.</param>
    /// <param name="baseCurrency">ISO 4217 source currency, e.g. "USD".</param>
    /// <param name="targetCurrencies">ISO 4217 target currencies, e.g. ["EUR", "GBP", "JPY"].</param>
    /// <returns>Map of currency code → converted amount.</returns>
    Task<IReadOnlyDictionary<string, double>> ConvertManyAsync(
        double amount,
        string baseCurrency,
        IEnumerable<string> targetCurrencies,
        CancellationToken cancellationToken = default);

    /// <summary>List all currency codes supported for a given base currency.</summary>
    /// <param name="baseCurrency">ISO 4217 base currency. Default: "USD".</param>
    Task<IReadOnlyList<string>> SupportedCurrenciesAsync(
        string baseCurrency = "USD",
        CancellationToken cancellationToken = default);

    /// <summary>Drop all cached rates so the next call fetches fresh data.</summary>
    void ClearCache();
}
