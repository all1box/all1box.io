namespace all1box.io.Models;

public sealed class MicrosoftGraphOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string Authority { get; set; } = "";
    public string Scopes { get; set; } = "";
    public string RedirectUri { get; set; } = "";
    public string MailboxAddress { get; set; } = "";
    public string NotificationUrl { get; set; } = "";
    public string ClientState { get; set; } = "";
}
