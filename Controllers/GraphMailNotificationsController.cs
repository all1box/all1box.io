using System.Text.Json;
using System.Text.Json.Serialization;
using all1box.io.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace all1box.io.Controllers;

[ApiController]
[Route("api/graph/mail/notifications")]
public sealed class GraphMailNotificationsController : ControllerBase
{
    private readonly ILogger<GraphMailNotificationsController> _logger;
    private readonly MicrosoftGraphOptions _options;

    public GraphMailNotificationsController(
        ILogger<GraphMailNotificationsController> logger,
        IOptions<MicrosoftGraphOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromQuery] string? validationToken)
    {
        if (!string.IsNullOrEmpty(validationToken))
        {
            return Content(validationToken, "text/plain");
        }

        var notification = await JsonSerializer.DeserializeAsync<GraphNotificationEnvelope>(
            Request.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            HttpContext.RequestAborted);

        if (notification?.Value is null || notification.Value.Count == 0)
        {
            return Accepted();
        }

        foreach (var item in notification.Value)
        {
            if (!string.Equals(item.ClientState, _options.ClientState, StringComparison.Ordinal))
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
