using AvaPlayer.Services.Database;

namespace AvaPlayer.Services.Network;

public sealed class NetworkAccessService : INetworkAccessService
{
    private const string SettingKey = "network-enabled";

    private readonly IDatabaseService _databaseService;
    private bool _isEnabled = true;
    private bool _initialized;

    public NetworkAccessService(IDatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public bool IsNetworkEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            NetworkAccessChanged?.Invoke(this, value);
        }
    }

    public bool IsEnabled => _isEnabled;

    public event EventHandler<bool>? NetworkAccessChanged;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            var setting = await _databaseService.GetSettingAsync(SettingKey, cancellationToken);
            if (bool.TryParse(setting, out var parsed))
            {
                _isEnabled = parsed;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Network] 读取网络访问设置失败: {ex.Message}");
        }

        _initialized = true;
        Console.WriteLine($"[Network] 网络访问: {(_isEnabled ? "已启用" : "已禁用")}");
    }

    public async Task PersistAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _databaseService.SaveSettingAsync(SettingKey, _isEnabled.ToString(), cancellationToken);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Network] 保存网络访问设置失败: {ex.Message}");
        }
    }
}
