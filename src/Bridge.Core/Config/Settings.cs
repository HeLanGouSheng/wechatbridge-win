using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.Core.Config;

public sealed record LlmSocialSettings(string BaseUrl, string AccountId, string SecretProtected)
{
    public static LlmSocialSettings Default => new("http://127.0.0.1:8788", string.Empty, string.Empty);

    [JsonIgnore]
    public bool IsFilledIn => AccountId.Length > 0 && SecretProtected.Length > 0;
}

/// <summary>Everything the user can set. The secret is stored protected (DPAPI in the app); Core never
/// sees the mechanism, only <see cref="ISecretProtector"/>.</summary>
public sealed record Settings(
    int Version,
    IReadOnlyList<string> MyNames,
    bool LlmSocialMode,
    LlmSocialSettings LlmSocial,
    int HistoryDays)
{
    public const int CurrentVersion = 1;

    public static Settings Default => new(CurrentVersion, Array.Empty<string>(), false, LlmSocialSettings.Default, 7);
}

public interface ISecretProtector
{
    string Protect(string plain);

    string Unprotect(string protectedValue);
}

public sealed class SettingsException(string message) : Exception(message);

/// <summary>settings.json under the data folder. Written atomically (tmp + move) because a share
/// process may be reading it at the same moment; a corrupt file is reported with its path rather than
/// silently replaced by defaults.</summary>
public sealed class SettingsStore
{
    private readonly ISecretProtector _protector;

    public SettingsStore(string path, ISecretProtector protector)
    {
        Path = path;
        _protector = protector;
    }

    public string Path { get; }

    public Settings Load()
    {
        if (!File.Exists(Path))
        {
            return Settings.Default;
        }

        try
        {
            var loaded = BridgeJson.Deserialize<Settings>(File.ReadAllText(Path));
            if (loaded is null)
            {
                throw new SettingsException($"设置文件是空的：{Path}");
            }

            return loaded with
            {
                MyNames = loaded.MyNames ?? Array.Empty<string>(),
                LlmSocial = loaded.LlmSocial ?? LlmSocialSettings.Default,
            };
        }
        catch (JsonException ex)
        {
            throw new SettingsException($"设置文件读不了（{ex.Message}）：{Path}。改好它，或者删掉它重新设置。");
        }
    }

    public void Save(Settings settings)
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, BridgeJson.Serialize(settings));
        File.Move(tmp, Path, overwrite: true);
    }

    public string Secret(Settings settings) => settings.LlmSocial.SecretProtected.Length == 0 ? string.Empty : _protector.Unprotect(settings.LlmSocial.SecretProtected);

    public Settings WithSecret(Settings settings, string plainSecret) =>
        settings with { LlmSocial = settings.LlmSocial with { SecretProtected = plainSecret.Length == 0 ? string.Empty : _protector.Protect(plainSecret) } };
}
