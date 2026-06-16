using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SoftCurrency.Tests;

public class SoftCurrencyTests
{
    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string MakeSuccessJson(string base_, Dictionary<string, double> rates) =>
        JsonSerializer.Serialize(new
        {
            result = "success",
            base_code = base_,
            conversion_rates = rates,
        });

    private static SoftCurrency BuildClient(
        string responseBody,
        HttpStatusCode status = HttpStatusCode.OK,
        string? apiKey = "test-key")
    {
        var handler = new FakeHandler(responseBody, status);
        return new SoftCurrency(apiKey!, new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
            CacheTtl = TimeSpan.FromHours(1),
        });
    }

    private static readonly Dictionary<string, double> SampleRates = new()
    {
        ["USD"] = 1.0,
        ["EUR"] = 0.92,
        ["GBP"] = 0.79,
        ["JPY"] = 149.50,
    };

    // ---------------------------------------------------------------------------
    // Constructor
    // ---------------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsOnNullKey()
    {
        var ex = Assert.Throws<SoftCurrencyException>(() => new SoftCurrency(null!));
        Assert.Equal("invalid-argument", ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnEmptyKey(string key)
    {
        var ex = Assert.Throws<SoftCurrencyException>(() => new SoftCurrency(key));
        Assert.Equal("invalid-argument", ex.Code);
    }

    // ---------------------------------------------------------------------------
    // GetRatesAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetRatesAsync_ReturnsRates_WhenApiSucceeds()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var rates = await client.GetRatesAsync("USD");

        Assert.Equal(0.92, rates["EUR"]);
        Assert.Equal(149.50, rates["JPY"]);
    }

    [Fact]
    public async Task GetRatesAsync_NormalisesBaseCurrencyCode()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        // lower-case and padded input should still work
        var rates = await client.GetRatesAsync("  usd  ");
        Assert.True(rates.ContainsKey("EUR"));
    }

    [Fact]
    public async Task GetRatesAsync_ReturnsCachedRates_WithoutSecondHttpCall()
    {
        var handler = new FakeHandler(MakeSuccessJson("USD", SampleRates));
        using var client = new SoftCurrency("test-key", new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
            CacheTtl = TimeSpan.FromHours(1),
        });

        await client.GetRatesAsync("USD");
        await client.GetRatesAsync("USD"); // should hit cache

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetRatesAsync_RefetchesAfterClearCache()
    {
        var handler = new FakeHandler(MakeSuccessJson("USD", SampleRates));
        using var client = new SoftCurrency("test-key", new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
            CacheTtl = TimeSpan.FromHours(1),
        });

        await client.GetRatesAsync("USD");
        client.ClearCache();
        await client.GetRatesAsync("USD");

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task GetRatesAsync_RefetchesWhenCacheTtlIsZero()
    {
        var handler = new FakeHandler(MakeSuccessJson("USD", SampleRates));
        using var client = new SoftCurrency("test-key", new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
            CacheTtl = TimeSpan.Zero,
        });

        await client.GetRatesAsync("USD");
        await client.GetRatesAsync("USD");

        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    [InlineData("1US")]
    [InlineData("")]
    public async Task GetRatesAsync_ThrowsOnInvalidCurrencyCode(string code)
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(() => client.GetRatesAsync(code));
        Assert.Equal("invalid-argument", ex.Code);
    }

    [Fact]
    public async Task GetRatesAsync_ThrowsOnKnownApiError()
    {
        using var client = BuildClient(
            """{"result":"error","error-type":"invalid-key"}""",
            HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(() => client.GetRatesAsync("USD"));
        Assert.Equal("invalid-key", ex.Code);
        Assert.Contains("API key", ex.Message);
    }

    [Fact]
    public async Task GetRatesAsync_ThrowsWithGenericMessage_ForUnknownApiError()
    {
        using var client = BuildClient(
            """{"result":"error","error-type":"some-future-error"}""",
            HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(() => client.GetRatesAsync("USD"));
        Assert.Equal("some-future-error", ex.Code);
    }

    [Fact]
    public async Task GetRatesAsync_ThrowsNetworkError_WhenHttpClientFails()
    {
        var handler = new ThrowingHandler();
        using var client = new SoftCurrency("test-key", new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
        });

        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(() => client.GetRatesAsync("USD"));
        Assert.Equal("network-error", ex.Code);
    }

    // ---------------------------------------------------------------------------
    // GetRateAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task GetRateAsync_ReturnsCorrectRate()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var rate = await client.GetRateAsync("USD", "EUR");

        Assert.Equal(0.92, rate);
    }

    [Fact]
    public async Task GetRateAsync_ThrowsOnUnsupportedTargetCurrency()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(
            () => client.GetRateAsync("USD", "XYZ"));
        Assert.Equal("unsupported-code", ex.Code);
    }

    // ---------------------------------------------------------------------------
    // ConvertAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConvertAsync_ReturnsConvertedAmount()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var result = await client.ConvertAsync(100, "USD", "EUR");

        Assert.Equal(92.0, result, precision: 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task ConvertAsync_ThrowsOnNonFiniteAmount(double badAmount)
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(
            () => client.ConvertAsync(badAmount, "USD", "EUR"));
        Assert.Equal("invalid-argument", ex.Code);
    }

    [Fact]
    public async Task ConvertAsync_AllowsNegativeAmount()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var result = await client.ConvertAsync(-50, "USD", "EUR");
        Assert.Equal(-46.0, result, precision: 6);
    }

    [Fact]
    public async Task ConvertAsync_AllowsZeroAmount()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var result = await client.ConvertAsync(0, "USD", "EUR");
        Assert.Equal(0.0, result);
    }

    // ---------------------------------------------------------------------------
    // ConvertManyAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ConvertManyAsync_ReturnsAllConvertedAmounts()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var result = await client.ConvertManyAsync(100, "USD", ["EUR", "GBP", "JPY"]);

        Assert.Equal(3, result.Count);
        Assert.Equal(92.0, result["EUR"], precision: 6);
        Assert.Equal(79.0, result["GBP"], precision: 6);
        Assert.Equal(14950.0, result["JPY"], precision: 6);
    }

    [Fact]
    public async Task ConvertManyAsync_ThrowsOnEmptyTargetList()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(
            () => client.ConvertManyAsync(100, "USD", []));
        Assert.Equal("invalid-argument", ex.Code);
    }

    [Fact]
    public async Task ConvertManyAsync_ThrowsOnNullTargetList()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(
            () => client.ConvertManyAsync(100, "USD", null!));
        Assert.Equal("invalid-argument", ex.Code);
    }

    [Fact]
    public async Task ConvertManyAsync_ThrowsOnUnsupportedCurrencyInList()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        var ex = await Assert.ThrowsAsync<SoftCurrencyException>(
            () => client.ConvertManyAsync(100, "USD", ["EUR", "ZZZ"]));
        Assert.Equal("unsupported-code", ex.Code);
    }

    // ---------------------------------------------------------------------------
    // SupportedCurrenciesAsync
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task SupportedCurrenciesAsync_ReturnsAllKeys()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));

        var currencies = await client.SupportedCurrenciesAsync();

        Assert.Contains("USD", currencies);
        Assert.Contains("EUR", currencies);
        Assert.Contains("GBP", currencies);
        Assert.Contains("JPY", currencies);
    }

    [Fact]
    public async Task SupportedCurrenciesAsync_DefaultsToUsd()
    {
        var handler = new FakeHandler(MakeSuccessJson("USD", SampleRates));
        using var client = new SoftCurrency("test-key", new SoftCurrencyOptions
        {
            HttpClient = new HttpClient(handler),
        });

        await client.SupportedCurrenciesAsync(); // no argument

        Assert.Contains("/USD", handler.LastRequestUri ?? "");
    }

    // ---------------------------------------------------------------------------
    // ISoftCurrency interface compliance
    // ---------------------------------------------------------------------------

    [Fact]
    public void SoftCurrency_ImplementsInterface()
    {
        using var client = BuildClient(MakeSuccessJson("USD", SampleRates));
        Assert.IsType<ISoftCurrency>(client, exactMatch: false);
    }
}

// ---------------------------------------------------------------------------
// Test doubles
// ---------------------------------------------------------------------------

internal sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<(string body, HttpStatusCode status)> _queue = new();
    public int CallCount { get; private set; }
    public string? LastRequestUri { get; private set; }

    public FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _queue.Enqueue((body, status));
    }

    public FakeHandler(IEnumerable<(string body, HttpStatusCode status)> responses)
    {
        foreach (var r in responses) _queue.Enqueue(r);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri?.ToString();
        var (body, status) = _queue.Count > 1 ? _queue.Dequeue() : _queue.Peek();
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class ThrowingHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Simulated network failure");
}
