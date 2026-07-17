namespace all1box.io.Models;

public sealed class GraphWebhookCallRecord
{
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string QueryString { get; set; } = "";
    public string? RemoteIpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? ValidationToken { get; set; }
    public string? SubscriptionId { get; set; }
    public string? ChangeType { get; set; }
    public string? Resource { get; set; }
    public string? ResourceDataId { get; set; }
    public bool? ClientStateValid { get; set; }
    public string? Payload { get; set; }
}
