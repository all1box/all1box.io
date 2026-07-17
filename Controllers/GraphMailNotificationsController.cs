using System.Text.Json;
using System.Text.Json.Serialization;
using all1box.io.Models;
using all1box.io.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace all1box.io.Controllers;

[ApiController]
[Route("api/graph/mail/notifications")]
public sealed class GraphMailNotificationsController : ControllerBase
{
    private readonly ILogger<GraphMailNotificationsController> _logger;
    private readonly MicrosoftGraphOptions _options;
    private readonly GraphWebhookCallRepository _repository;

    public GraphMailNotificationsController(
        ILogger<GraphMailNotificationsController> logger,
        IOptions<MicrosoftGraphOptions> options,
        GraphWebhookCallRepository repository)
    {
        _logger = logger;
        _options = options.Value;
        _repository = repository;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromQuery] string? validationToken)
    {
        if (!string.IsNullOrEmpty(validationToken))
        {
            await _repository.InsertAsync(CreateRecord(validationToken, payload: null), HttpContext.RequestAborted);
            return Content(validationToken, "text/plain");
        }

        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(HttpContext.RequestAborted);
        var notification = JsonSerializer.Deserialize<GraphNotificationEnvelope>(
            payload,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (notification?.Value is null || notification.Value.Count == 0)
        {
            await _repository.InsertAsync(CreateRecord(validationToken: null, payload), HttpContext.RequestAborted);
            return Accepted();
        }

        foreach (var item in notification.Value)
        {
            var clientStateValid = string.Equals(item.ClientState, _options.ClientState, StringComparison.Ordinal);
            await _repository.InsertAsync(CreateRecord(validationToken: null, payload, item, clientStateValid), HttpContext.RequestAborted);

            if (!clientStateValid)
            {
                _logger.LogWarning("Rejected Microsoft Graph notification with invalid client state for resource {Resource}.", item.Resource);
                continue;
            }

            _logger.LogInformation(
                "Received Microsoft Graph mail notification. SubscriptionId={SubscriptionId}, ChangeType={ChangeType}, Resource={Resource}, ResourceDataId={ResourceDataId}",
                item.SubscriptionId,
                item.ChangeType,
                item.Resource,
                item.ResourceData?.Id);
        }

        return Accepted();
    }

    private GraphWebhookCallRecord CreateRecord(
        string? validationToken,
        string? payload,
        GraphNotification? notification = null,
        bool? clientStateValid = null)
    {
        return new GraphWebhookCallRecord
        {
            Method = Request.Method,
            Path = Request.Path.Value ?? "",
            QueryString = Request.QueryString.Value ?? "",
            RemoteIpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString(),
            ValidationToken = validationToken,
            SubscriptionId = notification?.SubscriptionId,
            ChangeType = notification?.ChangeType,
            Resource = notification?.Resource,
            ResourceDataId = notification?.ResourceData?.Id,
            ClientStateValid = clientStateValid,
            Payload = payload
        };
    }

    public sealed class GraphNotificationEnvelope
    {
        [JsonPropertyName("value")]
        public List<GraphNotification> Value { get; set; } = [];
    }

    public sealed class GraphNotification
    {
        [JsonPropertyName("subscriptionId")]
        public string SubscriptionId { get; set; } = "";

        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; } = "";

        [JsonPropertyName("resource")]
        public string Resource { get; set; } = "";

        [JsonPropertyName("clientState")]
        public string ClientState { get; set; } = "";

        [JsonPropertyName("resourceData")]
        public GraphResourceData? ResourceData { get; set; }
    }

    public sealed class GraphResourceData
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";
    }
}
