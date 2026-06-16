# SoftCurrency

A lightweight .NET 9 library for currency conversion backed by [ExchangeRate-API](https://www.exchangerate-api.com). Supports single conversions, batch conversions, and rate lookups — with built-in in-memory caching to minimise API calls.

## Installation

```
dotnet add package SoftCurrency
```

## Requirements

- .NET 9.0 or later
- A free or paid API key from [exchangerate-api.com](https://www.exchangerate-api.com) (free tier: 1,500 requests/month)

---

## Quick Start

```csharp
using SoftCurrency;

var fx = new SoftCurrency("YOUR_API_KEY");

// Convert 100 USD to EUR
double euros = await fx.ConvertAsync(100, "USD", "EUR");
Console.WriteLine($"100 USD = {euros:F2} EUR");
```

---

## API Reference

### Constructor

```csharp
var fx = new SoftCurrency(string apiKey, SoftCurrencyOptions? options = null);
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `apiKey` | `string` | Your ExchangeRate-API key. |
| `options` | `SoftCurrencyOptions?` | Optional. Cache TTL and/or a custom `HttpClient`. |

Throws `SoftCurrencyException` (`code: "invalid-argument"`) if the key is null or empty.

---

### `GetRatesAsync`

Fetches all conversion rates for a base currency. Results are cached in memory for the duration of `CacheTtl` (default 1 hour).

```csharp
Task<IReadOnlyDictionary<string, double>> GetRatesAsync(
    string baseCurrency,
    CancellationToken cancellationToken = default)
```

```csharp
IReadOnlyDictionary<string, double> rates = await fx.GetRatesAsync("USD");
Console.WriteLine(rates["EUR"]);  // e.g. 0.92
Console.WriteLine(rates["JPY"]);  // e.g. 149.50
```

---

### `GetRateAsync`

Returns the exchange rate between two specific currencies.

```csharp
Task<double> GetRateAsync(
    string baseCurrency,
    string targetCurrency,
    CancellationToken cancellationToken = default)
```

```csharp
double rate = await fx.GetRateAsync("USD", "GBP");
Console.WriteLine($"1 USD = {rate} GBP");
```

---

### `ConvertAsync`

Converts an amount from one currency to another.

```csharp
Task<double> ConvertAsync(
    double amount,
    string baseCurrency,
    string targetCurrency,
    CancellationToken cancellationToken = default)
```

```csharp
double result = await fx.ConvertAsync(250.00, "GBP", "JPY");
Console.WriteLine($"250 GBP = {result:F2} JPY");
```

---

### `ConvertManyAsync`

Converts an amount into multiple target currencies with a **single API request**.

```csharp
Task<IReadOnlyDictionary<string, double>> ConvertManyAsync(
    double amount,
    string baseCurrency,
    IEnumerable<string> targetCurrencies,
    CancellationToken cancellationToken = default)
```

```csharp
var results = await fx.ConvertManyAsync(1000, "USD", ["EUR", "GBP", "JPY", "CAD"]);

foreach (var (currency, amount) in results)
    Console.WriteLine($"1000 USD = {amount:F2} {currency}");
```

---

### `SupportedCurrenciesAsync`

Returns all currency codes available for a given base currency.

```csharp
Task<IReadOnlyList<string>> SupportedCurrenciesAsync(
    string baseCurrency = "USD",
    CancellationToken cancellationToken = default)
```

```csharp
IReadOnlyList<string> codes = await fx.SupportedCurrenciesAsync();
Console.WriteLine($"{codes.Count} currencies supported");
// e.g. USD, EUR, GBP, JPY, AUD, CAD, CHF, CNY, ...
```

---

### `ClearCache`

Drops all cached rates. The next call to any method will fetch fresh data from the API.

```csharp
void ClearCache()
```

```csharp
fx.ClearCache();
var freshRates = await fx.GetRatesAsync("USD"); // always hits the API
```

---

## Configuration

Pass a `SoftCurrencyOptions` object to the constructor to customise behaviour.

### Cache TTL

By default, fetched rates are cached for **1 hour**. Adjust with `CacheTtl`:

```csharp
// Cache for 15 minutes
var fx = new SoftCurrency("YOUR_API_KEY", new SoftCurrencyOptions
{
    CacheTtl = TimeSpan.FromMinutes(15),
});

// Always fetch fresh rates (no caching)
var fx = new SoftCurrency("YOUR_API_KEY", new SoftCurrencyOptions
{
    CacheTtl = TimeSpan.Zero,
});
```

### Custom `HttpClient`

Provide your own `HttpClient` — useful for setting timeouts, base headers, or plugging into your application's existing `IHttpClientFactory`:

```csharp
var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(10),
};

var fx = new SoftCurrency("YOUR_API_KEY", new SoftCurrencyOptions
{
    HttpClient = httpClient,
});
```

> When you supply your own `HttpClient`, you are responsible for its lifetime. When the library creates one internally it is disposed automatically when `SoftCurrency` is disposed.

---

## Dependency Injection

`SoftCurrency` implements `ISoftCurrency`, making it straightforward to register and mock in DI-based applications.

### Registration

```csharp
// Program.cs
builder.Services.AddSingleton<ISoftCurrency>(_ =>
    new SoftCurrency("YOUR_API_KEY", new SoftCurrencyOptions
    {
        CacheTtl = TimeSpan.FromMinutes(30),
    }));
```

### Usage in a service

```csharp
public class PricingService(ISoftCurrency fx)
{
    public async Task<decimal> GetPriceInEurAsync(decimal usdPrice)
    {
        double rate = await fx.GetRateAsync("USD", "EUR");
        return usdPrice * (decimal)rate;
    }
}
```

### Mocking in tests

Because your code depends on `ISoftCurrency`, you can substitute any mock:

```csharp
// Using NSubstitute
var fx = Substitute.For<ISoftCurrency>();
fx.GetRateAsync("USD", "EUR").Returns(0.92);

// Using Moq
var fx = new Mock<ISoftCurrency>();
fx.Setup(x => x.GetRateAsync("USD", "EUR", default)).ReturnsAsync(0.92);
```

---

## Error Handling

All failures throw `SoftCurrencyException`, which carries a machine-readable `Code` property alongside the human-readable `Message`.

```csharp
try
{
    double rate = await fx.GetRateAsync("USD", "EUR");
}
catch (SoftCurrencyException ex)
{
    Console.WriteLine($"[{ex.Code}] {ex.Message}");
}
```

| `Code` | Cause |
|--------|-------|
| `invalid-argument` | Bad currency code (not 3 letters), non-finite amount, or empty API key. |
| `network-error` | HTTP request could not be sent (no internet, DNS failure, timeout, etc.). |
| `invalid-key` | The API key is not recognised by ExchangeRate-API. |
| `inactive-account` | The API account email address has not been confirmed. |
| `quota-reached` | The monthly request quota has been exhausted. |
| `unsupported-code` | The target currency code is not in the returned rate set. |
| `malformed-request` | The request URL was malformed (should not occur in normal use). |
| `http-{status}` | An unexpected HTTP status code was returned (e.g. `http-500`). |

---

## Disposal

`SoftCurrency` implements `IDisposable`. When you let the library manage the `HttpClient` internally, dispose the instance when you are done:

```csharp
await using var fx = new SoftCurrency("YOUR_API_KEY");
double rate = await fx.GetRateAsync("USD", "EUR");
// fx is disposed here — internal HttpClient is released
```

When using DI with a singleton lifetime, the DI container handles disposal on application shutdown.

---

## License

MIT © Sachindu Kavishka
