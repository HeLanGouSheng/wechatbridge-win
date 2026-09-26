using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Bridge.Core.LlmSocial;

public sealed record LlmSocialConfig(string BaseUrl, string AccountId, string SharedSecret)
{
    public static readonly Regex AccountIdPattern = new("^acct_[A-Za-z0-9_-]{12}$", RegexOptions.CultureInvariant);

    [JsonIgnore]
    public string WebhookUrl => BaseUrl.TrimEnd('/') + "/webhooks/" + AccountId;

    public string HealthUrl => BaseUrl.TrimEnd('/') + "/healthz";

    /// <summary>Everything that can be checked without a network call; the message says what to fix.</summary>
    public static string? Validate(string baseUrl, string accountId, string secret)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            return "llmsocial 地址要写成 http://127.0.0.1:8788 这样的完整地址";
        }

        if (!AccountIdPattern.IsMatch(accountId))
        {
            return "账号 ID 是 llmsocial 账号卡片上 acct_ 开头的那串（acct_ 加 12 个字符）";
        }

        if (secret.Length < 24)
        {
            return "共享密钥至少 24 个字符，和 llmsocial 账号里填的完全一致";
        }

        return null;
    }
}

public sealed record DeliveryFailure(string MessageId, int? Status, string Error);

/// <summary><see cref="AbortReason"/> is set when delivery stopped before the end; messages after
/// <see cref="Sent"/> + <see cref="Failures"/> were not attempted.</summary>
public sealed record DeliveryResult(int Sent, IReadOnlyList<DeliveryFailure> Failures, string? AbortReason)
{
    public bool Complete => AbortReason is null;
}

public sealed record ConnectionTest(bool Ok, string Detail);

/// <summary>
/// Sends messages to llmsocial's generic webhook, one request per message, oldest first. A 200 counts
/// as delivered whether the message was new or a repeat (llmsocial answers 200 to duplicates). Every
/// abort reason is written for the person who has to fix it.
/// </summary>
public sealed class LlmSocialClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly LlmSocialConfig _config;

    public LlmSocialClient(HttpClient http, LlmSocialConfig config)
    {
        _http = http;
        _config = config;
    }

    public async Task<DeliveryResult> DeliverAsync(IReadOnlyList<InboundPayload> messages, CancellationToken ct = default)
    {
        var sent = 0;
        var failures = new List<DeliveryFailure>();
        foreach (var message in messages)
        {
            var body = BridgeJson.SerializeToUtf8Bytes(message);
            var (status, error) = await PostAsync(body, ct);
            if (status is >= 500 and <= 599)
            {
                await Task.Delay(1000, ct);
                (status, error) = await PostAsync(body, ct);
            }

            switch (status)
            {
                case >= 200 and <= 299:
                    sent++;
                    break;
                case 400:
                    failures.Add(new DeliveryFailure(message.MessageId, 400, "llmsocial 拒收这条（400）：" + error));
                    break;
                case 401:
                    return new DeliveryResult(sent, failures, "共享密钥不对：设置里的密钥要和 llmsocial 账号里填的完全一致");
                case 404:
                    return new DeliveryResult(sent, failures, "llmsocial 里没有这个账号 ID，或它不是「通用 Webhook」连接方式");
                case null:
                    return new DeliveryResult(sent, failures, error);
                default:
                    return new DeliveryResult(sent, failures, $"llmsocial 返回 {status}：{error}");
            }
        }

        return new DeliveryResult(sent, failures, null);
    }

    /// <summary>Reachability, then secret and account in one signed POST with an empty body: llmsocial
    /// checks the signature before the fields, so 400 means both are right and nothing was stored.</summary>
    public async Task<ConnectionTest> TestAsync(CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var health = await _http.GetAsync(_config.HealthUrl, timeout.Token);
            if (!health.IsSuccessStatusCode)
            {
                return new ConnectionTest(false, $"{_config.HealthUrl} 返回 {(int)health.StatusCode}：这个端口上跑的不是 llmsocial 的回调服务");
            }
        }
        catch (HttpRequestException ex)
        {
            return new ConnectionTest(false, $"连不上 {_config.BaseUrl}（{ex.Message}）：llmsocial 没在运行，或端口不对（回调端口默认 8788）");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ConnectionTest(false, $"{_config.BaseUrl} 没有响应");
        }

        var (status, error) = await PostAsync("{}"u8.ToArray(), ct);
        return status switch
        {
            400 => new ConnectionTest(true, "连接正常：地址、账号 ID 和密钥都对"),
            401 => new ConnectionTest(false, "共享密钥不对：要和 llmsocial 账号里填的完全一致"),
            404 => new ConnectionTest(false, "llmsocial 里没有这个账号 ID，或它不是「通用 Webhook」连接方式"),
            null => new ConnectionTest(false, error),
            _ => new ConnectionTest(false, $"llmsocial 返回 {status}：{error}"),
        };
    }

    private async Task<(int? Status, string Error)> PostAsync(byte[] body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.WebhookUrl);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(LlmSocialSigner.HeaderName, LlmSocialSigner.Sign(_config.SharedSecret, body));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(ct);
            return ((int)response.StatusCode, text.Length > 200 ? text[..200] : text);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"llmsocial 没在运行（{_config.BaseUrl}）：{ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"llmsocial 没有响应（{_config.BaseUrl}，超过 {RequestTimeout.TotalSeconds:0} 秒）");
        }
    }
}
