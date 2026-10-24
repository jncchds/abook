using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ABook.Api.Telegram;

/// <summary>
/// One bot per deployment, configured by environment variables:
/// <list type="bullet">
/// <item><c>TELEGRAM_BOT_TOKEN</c> — turns the bot on; without it there is no Telegram at all.</item>
/// <item><c>ABOOK_HOSTNAME</c> — the public host name: updates come by webhook at
/// <c>https://{ABOOK_HOSTNAME}/api/telegram/webhook</c>. Unset = long polling.</item>
/// <item><c>TELEGRAM_WEBHOOK_SECRET</c> — optional; checked against Telegram's secret-token header. Unset = a random
/// secret per process (the webhook is registered again at every start).</item>
/// </list>
/// </summary>
public sealed partial class TelegramOptions
{
    public const string TokenKey = "TELEGRAM_BOT_TOKEN";
    public const string HostnameKey = "ABOOK_HOSTNAME";
    public const string SecretKey = "TELEGRAM_WEBHOOK_SECRET";
    public const string WebhookPath = "/api/telegram/webhook";

    public string? BotToken { get; init; }
    /// <summary>The public base URL (scheme + host [+ port]), or null for long polling.</summary>
    public string? BaseUrl { get; init; }
    public string WebhookSecret { get; init; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(BotToken);
    public bool UseWebhook => Enabled && BaseUrl is not null;
    public string? WebhookUrl => BaseUrl is null ? null : BaseUrl + WebhookPath;

    public static TelegramOptions FromConfiguration(IConfiguration config)
    {
        var token = config[TokenKey] ?? config["Telegram:BotToken"];
        var host = config[HostnameKey] ?? config["Telegram:Hostname"];
        var secret = config[SecretKey] ?? config["Telegram:WebhookSecret"];
        return new TelegramOptions
        {
            BotToken = string.IsNullOrWhiteSpace(token) ? null : token.Trim(),
            BaseUrl = NormalizeBaseUrl(host),
            WebhookSecret = !string.IsNullOrWhiteSpace(secret) && SecretPattern().IsMatch(secret.Trim())
                ? secret.Trim()
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
        };
    }

    /// <summary>"book.example.com", "book.example.com:8443/" or "https://book.example.com" → "https://book.example.com[:port]".</summary>
    internal static string? NormalizeBaseUrl(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var value = host.Trim().TrimEnd('/');
        if (!value.Contains("://")) value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,256}$")]
    private static partial Regex SecretPattern();
}
