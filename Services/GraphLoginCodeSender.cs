using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using all1box.io.Models;

namespace all1box.io.Services;

public sealed class GraphLoginCodeSender
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphLoginCodeSender> _logger;
    private readonly MicrosoftGraphOptions _options;
    private readonly MicrosoftGraphTokenService _tokenService;

    public GraphLoginCodeSender(
        HttpClient httpClient,
        ILogger<GraphLoginCodeSender> logger,
        IOptions<MicrosoftGraphOptions> options,
        MicrosoftGraphTokenService tokenService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _tokenService = tokenService;
    }

    public async Task<bool> SendEmailCodeAsync(string toEmail, string code, CancellationToken cancellationToken)
    {
        if (!IsConfigured())
        {
            _logger.LogWarning("Microsoft Graph login email is not configured.");
            return false;
        }

        var body = new
        {
            message = new
            {
                subject = "Your all1box verification code",
                body = new
                {
                    contentType = "Text",
                    content = $"Your all1box verification code is {code}"
                },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = toEmail } }
                }
            },
            saveToSentItems = true
        };

        using var request = await _tokenService.CreateGraphRequestAsync(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{_options.MailboxAddress}/sendMail",
            cancellationToken);
        request.Content = JsonContent.Create(body);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Microsoft Graph login email failed: {StatusCode} {ResponseBody}", (int)response.StatusCode, responseBody);
        return false;
    }

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_options.TenantId)
            && !string.IsNullOrWhiteSpace(_options.ClientId)
            && !string.IsNullOrWhiteSpace(_options.ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.MailboxAddress)
            && !_options.TenantId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
            && !_options.ClientId.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
            && !_options.ClientSecret.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase);
    }
}
