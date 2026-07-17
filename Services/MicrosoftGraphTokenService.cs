using System.Net.Http.Headers;
using System.Text.Json;
using all1box.io.Models;
using Microsoft.Extensions.Options;

namespace all1box.io.Services;

public sealed class MicrosoftGraphTokenService
{
    private readonly HttpClient _httpClient;
    private readonly MicrosoftGraphOptions _options;
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public MicrosoftGraphTokenService(HttpClient httpClient, IOptions<MicrosoftGraphOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return _accessToken;
        }

        var tokenEndpoint = $"https://login.microsoftonline.com/{_options.TenantId}/oauth2/v2.0/token";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        using var response = await _httpClient.PostAsync(tokenEndpoint, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        _accessToken = document.RootElement.GetProperty("access_token").GetString();
        var expiresIn = document.RootElement.GetProperty("expires_in").GetInt32();
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        return _accessToken ?? throw new InvalidOperationException("Microsoft Graph token response did not include an access token.");
    }

    public async Task<HttpRequestMessage> CreateGraphRequestAsync(HttpMethod method, string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
        return request;
    }
}
