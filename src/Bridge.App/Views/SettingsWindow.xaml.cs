using System.Globalization;
using System.Windows;
using Bridge.App.Delivery;
using Bridge.Core.Config;
using Bridge.Core.LlmSocial;

namespace Bridge.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsStore _store = DeliveryFlow.Store;
    private readonly Settings _current;
    private readonly string _currentSecret;

    public SettingsWindow()
    {
        InitializeComponent();
        _current = _store.Load();
        _currentSecret = TryReadSecret(_current);
        BaseUrl.Text = _current.LlmSocial.BaseUrl;
        AccountId.Text = _current.LlmSocial.AccountId;
        MyNames.Text = string.Join("\r\n", _current.MyNames);
        AutoMode.IsChecked = _current.LlmSocialMode;
        HistoryDays.Text = _current.HistoryDays.ToString(CultureInfo.InvariantCulture);
        if (_current.LlmSocial.SecretProtected.Length > 0)
        {
            Status.Text = _currentSecret.Length == 0
                ? "已保存的密钥读不出来，请重新填一次。"
                : "密钥已保存；留空表示不改。";
        }
    }

    public bool Saved { get; private set; }

    private string TryReadSecret(Settings settings)
    {
        try
        {
            return _store.Secret(settings);
        }
        catch (SettingsException)
        {
            return "";
        }
    }

    private (Settings Settings, string Secret, string? Problem) Collect()
    {
        var names = MyNames.Text
            .Split(new[] { '\r', '\n', '，', ',', '、' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (!int.TryParse(HistoryDays.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days < 0)
        {
            return (_current, "", "记录保留天数要填 0 或正整数");
        }

        var secret = Secret.Password.Length > 0 ? Secret.Password : _currentSecret;
        var baseUrl = BaseUrl.Text.Trim();
        var accountId = AccountId.Text.Trim();
        var problem = LlmSocialConfig.Validate(baseUrl, accountId, secret);
        var settings = _current with
        {
            MyNames = names,
            LlmSocialMode = AutoMode.IsChecked == true,
            HistoryDays = days,
            LlmSocial = _current.LlmSocial with { BaseUrl = baseUrl, AccountId = accountId },
        };
        return (settings, secret, problem);
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var (settings, secret, problem) = Collect();
        if (problem is not null)
        {
            Status.Text = problem;
            return;
        }

        TestButton.IsEnabled = false;
        Status.Text = "正在连接…";
        try
        {
            var config = new LlmSocialConfig(settings.LlmSocial.BaseUrl, settings.LlmSocial.AccountId, secret);
            var client = new LlmSocialClient(DeliveryFlow.CreateHttpClient(config.BaseUrl), config);
            var result = await client.TestAsync();
            Status.Text = result.Detail;
        }
        catch (Exception ex)
        {
            Status.Text = $"测试出错：{ex.Message}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var (settings, secret, problem) = Collect();
        var filledAnything = settings.LlmSocial.AccountId.Length > 0 || secret.Length > 0;
        if (problem is not null && filledAnything)
        {
            Status.Text = problem;
            return;
        }

        try
        {
            var toSave = filledAnything ? _store.WithSecret(settings, secret) : _store.WithSecret(settings, "");
            _store.Save(toSave);
        }
        catch (Exception ex)
        {
            Status.Text = $"没保存成：{ex.Message}";
            return;
        }

        Saved = true;
        DialogResult = true;
    }
}
