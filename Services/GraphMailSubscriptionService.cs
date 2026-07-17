using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using all1box.io.Models;
using Microsoft.Extensions.Options;

namespace all1box.io.Services;

public sealed class GraphMailSubscriptionService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);
    private static readonly TimeSpan SubscriptionLifetime = TimeSpan.FromDays(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger<GraphMailSubscriptionService> _logger;
    private readonly MicrosoftGraphOptions _options;
    private readonly MicrosoftGraphTokenService _tokenService;
    private string? _subscriptionId;
    private DateTimeOffset _expiresAt;

    public GraphMailSubscriptionService(
        HttpClient httpClient,
        ILogger<GraphMailSubscriptionService> logger,
        IOptions<MicrosoftGraphOptions> options,
        MicrosoftGraphTokenService tokenService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options.Value;
        _tokenService = tokenService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureSubscriptionAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create or renew Microsoft Graph mail subscription.");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task EnsureSubscriptionAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured())
        {
            _logger.LogWarning("Microsoft Graph mail subscription is not configured. Missing mailbox, notification URL, or client state.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_subscriptionId) && _expiresAt > DateTimeOffset.UtcNow.AddHours(24))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_subscriptionId) && await TryUseExistingSubscriptionAsync(cancellationToken))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_subscriptionId))
        {
            try
            {
                await RenewSubscriptionAsync(cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Renewing Microsoft Graph subscription failed; a new subscription will be created.");
                _subscriptionId = null;
            }
        }

        await CreateSubscriptionAsync(cancellationToken);
    }

    private async Task<bool> TryUseExistingSubscriptionAsync(CancellationToken cancellationToken)
    {
        using var request = await _tokenService.CreateGraphRequestAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/subscriptions", cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Could not list Microsoft Graph subscriptions: {StatusCode} {ResponseBody}", (int)response.StatusCode, responseBody);
            return false;
        }

        var subscriptions = JsonSerializer.Deserialize<GraphSubscriptionListResponse>(responseBody, JsonOptions)?.Value ?? [];
        var expectedResource = $"/users/{_options.MailboxAddress}/mailFolders('Inbox')/messages";
        var matchingSubscriptions = subscriptions
            .Where(subscription =>
                string.Equals(subscription.NotificationUrl, _options.NotificationUrl, StringComparison.OrdinalIgnoreCase)
                && string.Equals(subscription.Resource, expectedResource, StringComparison.OrdinalIgnoreCase)
                && string.Equals(subscription.ChangeType, "created", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(subscription => subscription.ExpirationDateTime)
            .ToList();

        var current = matchingSubscriptions.FirstOrDefault();
        if (current is null)
        {
            return false;
        }

        _subscriptionId = current.Id;
        _expiresAt = current.ExpirationDateTime;
        _logger.LogInformation("Using existing Microsoft Graph mail subscription {SubscriptionId}; expires at {ExpiresAt}.", _subscriptionId, _expiresAt);

        foreach (var duplicate in matchingSubscriptions.Skip(1))
        {
            await DeleteSubscriptionAsync(duplicate.Id, cancellationToken);
        }

        if (_expiresAt <= DateTimeOffset.UtcNow.AddHours(24))
        {
            await RenewSubscriptionAsync(cancellationToken);
        }

        return true;
    }

    private async Task CreateSubscriptionAsync(CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(SubscriptionLifetime);
        var body = new GraphSubscriptionRequest
        {
            ChangeType = "created",
            NotificationUrl = _options.NotificationUrl,
            Resource = $"/users/{_options.MailboxAddress}/mailFolders('Inbox')/messages",
            ExpirationDateTime = expiresAt,
            ClientState = _options.ClientState
        };

        using var request = await _tokenService.CreateGraphRequestAsync(HttpMethod.Post, "https://graph.microsoft.com/v1.0/subscriptions", cancellationToken);
        request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph subscription create failed: {(int)response.StatusCode} {responseBody}");
        }

        var subscription = JsonSerializer.Deserialize<GraphSubscriptionResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Graph subscription create response was empty.");

        _subscriptionId = subscription.Id;
        _expiresAt = subscription.ExpirationDateTime;
        _logger.LogInformation("Created Microsoft Graph mail subscription {SubscriptionId}; expires at {ExpiresAt}.", _subscriptionId, _expiresAt);
    }

    private async Task RenewSubscriptionAsync(CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.Add(SubscriptionLifetime);
        var body = new { expirationDateTime = expiresAt };

        using var request = await _tokenService.CreateGraphRequestAsync(HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/subscriptions/{_subscriptionId}", cancellationToken);
        request.Content = JsonContent.Create(body);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph subscription renew failed: {(int)response.StatusCode} {responseBody}");
        }

        var subscription = JsonSerializer.Deserialize<GraphSubscriptionResponse>(responseBody, JsonOptions)
            ?? throw new InvalidOperationException("Graph subscription renew response was empty.");

        _expiresAt = subscription.ExpirationDateTime;
        _logger.LogInformation("Renewed Microsoft Graph mail subscription {SubscriptionId}; expires at {ExpiresAt}.", _subscriptionId, _expiresAt);
    }

    private async Task DeleteSubscriptionAsync(string subscriptionId, CancellationToken cancellationToken)
    {
        using var request = await _tokenService.CreateGraphRequestAsync(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/subscriptions/{subscriptionId}", cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Deleted duplicate Microsoft Graph mail subscription {SubscriptionId}.", subscriptionId);
            return;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogWarning("Could not delete duplicate Microsoft Graph subscription {SubscriptionId}: {StatusCode} {ResponseBody}", subscriptionId, (int)response.StatusCode, responseBody);
    }

    private bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(_options.TenantId)
            && !string.IsNullOrWhiteSpace(_options.ClientId)
            && !string.IsNullOrWhiteSpace(_options.ClientSecret)
            && !string.IsNullOrWhiteSpace(_options.MailboxAddress)
            && !string.IsNullOrWhiteSpace(_options.NotificationUrl)
            && !string.IsNullOrWhiteSpace(_options.ClientState);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class GraphSubscriptionRequest
    {
        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; } = "";

        [JsonPropertyName("notificationUrl")]
        public string NotificationUrl { get; set; } = "";

        [JsonPropertyName("resource")]
        public string Resource { get; set; } = "";

        [JsonPropertyName("expirationDateTime")]
        public DateTimeOffset ExpirationDateTime { get; set; }

        [JsonPropertyName("clientState")]
        public string ClientState { get; set; } = "";
    }

    private sealed class GraphSubscriptionResponse
    {
        public string Id { get; set; } = "";
        public string ChangeType { get; set; } = "";
        public string NotificationUrl { get; set; } = "";
        public string Resource { get; set; } = "";
        public DateTimeOffset ExpirationDateTime { get; set; }
    }

    private sealed class GraphSubscriptionListResponse
    {
        public List<GraphSubscriptionResponse> Value { get; set; } = [];
    }
}
