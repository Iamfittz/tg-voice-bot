using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace VoiceBot.Graph;

// Application permissions only: call records do not support delegated access.
public sealed class GraphTokenProvider(
    IHttpClientFactory httpFactory,
    IOptions<GraphOptions> options,
    TimeProvider time)
{
    public const string HttpClientName = "graph-login";

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly GraphOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_token is null || time.GetUtcNow() >= _refreshAt)
                await RequestTokenAsync(ct);
            return _token!;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task RequestTokenAsync(CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(HttpClientName);
        var url = new Uri(new Uri(_options.LoginBaseUrl), $"{Uri.EscapeDataString(_options.TenantId)}/oauth2/v2.0/token");

        using var response = await http.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default"
        }), ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new InvalidOperationException("The token response is empty");

        _token = token.AccessToken;
        _refreshAt = time.GetUtcNow().AddSeconds(token.ExpiresIn) - RefreshMargin;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
