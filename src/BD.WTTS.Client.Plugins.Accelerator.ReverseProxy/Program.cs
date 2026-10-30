using BD.Common.Repositories.Abstractions;
using BD.WTTS.Enums;
using BD.WTTS.Models;
using BD.WTTS.Services.Implementation;
using System.Text;
using System.Text.Json;
using KeyValuePair = BD.Common.Entities.KeyValuePair;
#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32;
#endif

// ============================================================================
// Steam++ Accelerator —— 仅代理（反向代理）独立宿主程序
//
// 本程序由 Watt Toolkit(Steam++) 裁剪而来，只保留「网络加速 / 反向代理」能力。
// 它不再作为 GUI 主程序的 IPC 子进程运行，因此可以独立启动、退出与配置。
//
// 用法:
//   Steam++.Accelerator --mode system [--port 26501] [--ip 127.0.0.1]
//   Steam++.Accelerator --mode hosts
//   Steam++.Accelerator --mode pac
//   Steam++.Accelerator --mode dns            (需要管理员权限 + WinDivert)
//   Steam++.Accelerator --mode proxyonly
//
//   --mode   system(默认) | hosts | pac | dns | proxyonly
//   --port   代理监听端口，默认 26501；hosts 模式默认 443
//   --ip     监听地址，默认 127.0.0.1（如需局域网共享请改成 0.0.0.0）
//   --dns    上游 DNS，默认 223.5.5.5
//   --no-doh 关闭 DoH（默认启用 DoH）
//   --all    启用官方规则里的全部项目（默认只启用服务端标记为已勾选的项目）
//   --help   显示帮助
// ============================================================================

const string moduleName = AssemblyInfo.Accelerator;
const string tag = "Accelerator";

// 官方加速规则接口（匿名可用，无需登录账号）
const string apiBaseUrl = "https://api.steampp.net";
const string accelerateProjectGroupsPath = "/accelerator/projectgroups";

const ushort defaultProxyPort = 26501;
const ushort hostsProxyPort = 443;
const string defaultProxyDns = "223.5.5.5";
const string defaultProxyIp = "127.0.0.1";

/// <summary>规则配置文件名（位于数据目录）。</summary>
const string RuleConfigFileName = "accelerate-rules.json";

/// <summary>官方规则原始 JSON 的缓存文件名。</summary>
const string OfficialRulesCacheFileName = "accelerate-projects.json";

#if DEBUG
var consoleTitle = $"[{Environment.ProcessId}, {IsProcessElevated_DEBUG_Only().ToLowerString()}] {Constants.CUSTOM_URL_SCHEME_NAME}({moduleName}) {string.Join(' ', args)}";
SetConsoleTitle(consoleTitle);

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static void SetConsoleTitle(string title)
{
    try
    {
        Console.Title = title;
    }
    catch
    {
    }
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
static bool IsProcessElevated_DEBUG_Only()
{
    if (!OperatingSystem.IsWindows())
    {
        return false;
    }
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    WindowsPrincipal principal = new(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}
#endif

if (Windows.Win32.OSShuttingDownHelper.IsSystemShuttingDown())
{
    return 0;
}

var cli = ParseArgs(args);

if (cli.ContainsKey("help"))
{
    PrintUsage();
    return 0;
}

var proxyModeText = (cli.GetValueOrDefault("mode") ?? "system").Trim().ToLowerInvariant();
var proxyMode = proxyModeText switch
{
    "system" => ProxyMode.System,
    "hosts" => ProxyMode.Hosts,
    "pac" => ProxyMode.PAC,
    "dns" or "dnsintercept" => ProxyMode.DNSIntercept,
    "proxyonly" or "proxy" => ProxyMode.ProxyOnly,
    _ => ProxyMode.System,
};

// 引擎只在 System / PAC / VPN 模式下监听正向代理端口（ProxyPort）。
// 「仅代理」模式要的就是「在指定端口提供 HTTP 代理、但不改动任何系统设置」，
// 因此引擎侧按 System 处理，系统副作用（注册表 / hosts / WinDivert）由下面的 switch 决定，本模式一个都不施加。
var engineProxyMode = proxyMode == ProxyMode.ProxyOnly ? ProxyMode.System : proxyMode;

var proxyPort = cli.TryGetValue("port", out var portText) && ushort.TryParse(portText, out var portValue) && portValue > 0
    ? portValue
    : proxyMode == ProxyMode.Hosts ? hostsProxyPort : defaultProxyPort;

var proxyIp = GetString(cli, "ip") ?? defaultProxyIp;
var proxyDns = GetString(cli, "dns") ?? defaultProxyDns;
var useDoh = !cli.ContainsKey("no-doh");
var loadAllRules = cli.ContainsKey("all");
var refreshRules = cli.ContainsKey("refresh-rules");

// --exclude 可重复，按加速项目名排除规则（直接从原始参数扫描，避免被参数字典覆盖）
var excludedRuleNames = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].Equals("--exclude", StringComparison.OrdinalIgnoreCase))
    {
        continue;
    }
    if (i + 1 >= args.Length)
    {
        continue;
    }
    foreach (var item in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        excludedRuleNames.Add(item);
    }
}

var exitCode = 0;
var sideEffectApplied = false;

// 宿主目录必须在使用 IOPath.AppDataDirectory 之前初始化
IOPath.InitFileSystem(GetAppDataDirectory, GetCacheDirectory);
InitNLog();

// DNS 拦截模式会把命中域名的解析结果改写成 127.0.0.1。若上次运行是被强杀（任务管理器结束），
// 系统 DNS 缓存里会残留这些假 IP，导致「程序已退出但网站仍连不上」。
// 启动前先清一次缓存，避免继承上次的残留。
FlushDnsCache();

// 规则配置文件路径；--rules 可指定其它位置
var ruleConfigPath = GetString(cli, "rules") ?? Path.Combine(IOPath.AppDataDirectory, RuleConfigFileName);

Console.WriteLine($"{Constants.CUSTOM_URL_SCHEME_NAME} Accelerator");
Console.WriteLine($"  mode      : {proxyMode} ({proxyModeText})");
Console.WriteLine($"  listen    : {proxyIp}:{proxyPort}");
Console.WriteLine($"  dns       : {proxyDns}{(useDoh ? " (DoH)" : string.Empty)}");
Console.WriteLine($"  appdata   : {IOPath.AppDataDirectory}");
Console.WriteLine($"  rules     : {ruleConfigPath}");
Console.WriteLine();

try
{
    // 使用 BD.Common 的 Ioc 容器，引擎内部的 Ioc.Get<T>() 依赖它
    Ioc.ConfigureServices(ConfigureServices);

    var reverseProxyService = Ioc.Get<IReverseProxyService>();

    var proxyDomains = await LoadAccelerateProjectsAsync(
        ruleConfigPath,
        loadAllRules,
        excludedRuleNames,
        refreshRules);
    if (proxyDomains.Count <= 0)
    {
        Console.Error.WriteLine("没有启用任何加速规则，代理将以「无规则直连」方式启动。");
    }
    else
    {
        Console.WriteLine($"已加载 {proxyDomains.Count} 条启用的加速规则。");
    }

    var settings = new ReverseProxySettings(
        ProxyDomains: proxyDomains,
        Scripts: null,
        IsEnableScript: false,
        IsOnlyWorkSteamBrowser: false,
        ProxyPort: proxyPort,
        ProxyIp: proxyIp,
        ProxyMode: engineProxyMode,
        IsProxyGOG: false,
        OnlyEnableProxyScript: false,
        EnableHttpProxyToHttps: true,
        Socks5ProxyEnable: false,
        Socks5ProxyPortId: 8868,
        TwoLevelAgentEnable: false,
        TwoLevelAgentProxyType: ExternalProxyType.Socks5,
        TwoLevelAgentIp: "127.0.0.1",
        TwoLevelAgentPortId: 7890,
        TwoLevelAgentUserName: null,
        TwoLevelAgentPassword: null,
        ProxyDNS: proxyDns,
        IsSupportIpv6: Socket.OSSupportsIPv6,
        UseDoh: useDoh,
        CustomDohAddres: null,
        ServerSideProxyToken: null);

    var startResult = await reverseProxyService.StartProxyAsync(Serializable.SMP2(settings));
    if (startResult.Code != StartProxyResultCode.Ok)
    {
        Console.Error.WriteLine($"启动代理失败: {startResult.Code}({(byte)startResult.Code})");
        if (startResult.Exception != null)
        {
            Console.Error.WriteLine(startResult.Exception.ToString());
        }
        return 3;
    }

    Console.WriteLine($"代理引擎已启动，监听模式 {proxyMode}。");

    // 按模式应用「系统级副作用」——引擎只负责代理与拦截本身
    switch (proxyMode)
    {
        case ProxyMode.System:
            sideEffectApplied = await SystemProxyHelper.SetSystemProxyAsync(true, proxyIp, proxyPort);
            break;
        case ProxyMode.PAC:
            sideEffectApplied = await SystemProxyHelper.SetPacAsync(true, $"http://{proxyIp}:{proxyPort}/proxy.pac");
            break;
        case ProxyMode.Hosts:
            sideEffectApplied = await HostsFileHelper.ApplyAsync(proxyDomains, "127.0.0.1");
            break;
        case ProxyMode.DNSIntercept:
            // DNS 拦截由 WinDivert 驱动完成，需要管理员权限
            Console.WriteLine("DNS 拦截模式：请确认以管理员身份运行，且 WinDivert.dll / WinDivert64.sys 与程序同目录。");
            sideEffectApplied = true;
            break;
        case ProxyMode.ProxyOnly:
            Console.WriteLine($"仅代理模式：请把客户端代理指向 http://{proxyIp}:{proxyPort}");
            sideEffectApplied = true;
            break;
    }

    Console.WriteLine(sideEffectApplied ? "已应用系统设置。" : "警告：应用系统设置失败，请检查权限。");
    Console.WriteLine("按 Ctrl+C 停止并退出。");
    Console.WriteLine();

    // 注意：不能使用 using，ProcessExit 处理器在 CTS 释放后仍可能被回调
    var shutdown = new CancellationTokenSource();
    void RequestShutdown()
    {
        try
        {
            shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 进程退出阶段 CTS 已释放，忽略
        }
    }

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        RequestShutdown();
    };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => RequestShutdown();
    try
    {
        await Task.Delay(Timeout.Infinite, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
    }

    Console.WriteLine("正在停止代理...");

    // 先撤销系统级副作用，再停止引擎
    switch (proxyMode)
    {
        case ProxyMode.System:
            await SystemProxyHelper.SetSystemProxyAsync(false, null, 0);
            break;
        case ProxyMode.PAC:
            await SystemProxyHelper.SetPacAsync(false, null);
            break;
        case ProxyMode.Hosts:
            await HostsFileHelper.RevertAsync();
            break;
    }

    await reverseProxyService.StopProxyAsync();

    // 退出后再清一次 DNS 缓存：DNS 拦截期间命中域名的解析结果被改写成了 127.0.0.1，
    // 不清掉的话系统会继续用这些假 IP，表现为「程序退出了但网站还是连不上」。
    FlushDnsCache();

    Console.WriteLine("已退出。");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.ToString());
    exitCode = 500;
}
finally
{
    try
    {
        await Ioc.DisposeAsync();
    }
    catch
    {
    }
#if DEBUG
    Console.ReadLine();
#endif
}

return exitCode;

// ---------------------------------------------------------------------------
// 宿主基础设置
// ---------------------------------------------------------------------------

static string GetAppDataDirectory()
{
    var root = OperatingSystem.IsWindows()
        ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    if (string.IsNullOrWhiteSpace(root))
    {
        root = AppContext.BaseDirectory;
    }
    var dir = Path.Combine(root, "Steam++Accelerator");
    Directory.CreateDirectory(dir);
    return dir;
}

static string GetCacheDirectory()
{
    var dir = Path.Combine(GetAppDataDirectory(), "Cache");
    Directory.CreateDirectory(dir);
    return dir;
}

/// <summary>
/// 清理系统 DNS 解析缓存。
/// <para>
/// DNS 拦截模式会把命中域名的应答改写成 127.0.0.1。这些假 IP 会留在系统缓存里，
/// 程序退出后仍被复用，表现为「程序停了但网站连不上」。清缓存可消除该残留。
/// </para>
/// </summary>
static void FlushDnsCache()
{
#if WINDOWS
    try
    {
        DnsFlushResolverCache();
    }
    catch (Exception ex)
    {
        Log.Warn(tag, $"清理 DNS 缓存失败: {ex.Message}");
    }
#endif
}

#if WINDOWS
[DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
static extern void DnsFlushResolverCache();
#endif

static void InitNLog()
{
    var logDirectory = Path.Combine(IOPath.CacheDirectory, "Logs");
    Directory.CreateDirectory(logDirectory);

    var layout = "${longdate}|${level:uppercase=true}|${logger:shortName=true}|${message} ${exception:format=tostring}";

    var configuration = new LoggingConfiguration();

    var consoleTarget = new ColoredConsoleTarget("console")
    {
        Layout = layout,
    };
    configuration.AddRule(NLogLevel.Info, NLogLevel.Fatal, consoleTarget);

    var fileTarget = new FileTarget("file")
    {
        FileName = Path.Combine(logDirectory, $"accelerator-${{shortdate}}.log"),
        Layout = layout,
        ArchiveAboveSize = 10 * 1024 * 1024,
        MaxArchiveFiles = 14,
        KeepFileOpen = false,
    };
    configuration.AddRule(NLogLevel.Debug, NLogLevel.Fatal, fileTarget);

    NLogManager.Configuration = configuration;
}

static void ConfigureServices(IServiceCollection services)
{
    services.AddLogging(l =>
    {
        l.ClearProviders();
        l.AddNLog(LogManager.Configuration!); // 添加 NLog 日志
        l.AddConsole();
        l.AddProvider(new LogConsoleService.Utf8StringLoggerProvider(moduleName));
    });

    // 设置仓储层数据库文件存放路径
    Repository.DataBaseDirectory = IOPath.AppDataDirectory;
    services.TryAddSingleton<ISecureStorage, RepositorySecureStorage>();

    services.AddHttpClient();
    services.AddCommonHttpClientFactory();
    services.AddSingleton<IHttpPlatformHelperService, HttpPlatformHelperConsoleService>();

    // 本机实现，替代原来由 IPC 主进程提供的平台/提示服务
    services.AddSingleton<IPCPlatformService, IPCPlatformServiceImpl>();
    services.AddSingleton<IPCToastService, IPCToastServiceImpl>();

    services.AddDnsAnalysisService();
    services.AddReverseProxyService();
    services.AddSingleton<ICertificateManager, CertificateManagerImpl>();
}

// ---------------------------------------------------------------------------
// 加速规则：本地配置文件为唯一来源
//
// 首次运行（或 --refresh-rules）时从官方接口拉取规则并生成配置文件，
// 之后每次启动都直接读配置文件，不再联网——想改规则就编辑那个 JSON。
// ---------------------------------------------------------------------------

static async Task<IReadOnlyList<AccelerateProjectDTO>> LoadAccelerateProjectsAsync(
    string configPath,
    bool all,
    IReadOnlyCollection<string> excludes,
    bool refresh)
{
    // 1) 已有配置文件：直接使用（--refresh-rules 时才重新拉取官方规则）
    var config = RuleConfig.Load(configPath, out var configError);
    if (config != null && !refresh)
    {
        return SelectEnabled(config.ToProjects(), all, excludes);
    }

    // 配置文件存在但解析失败：不能覆盖用户的文件，直接报错退出
    if (config == null && configError != null && !refresh)
    {
        throw new InvalidOperationException(
            $"规则配置文件解析失败: {configError}\n请检查文件格式，或删除该文件让程序重新生成：{configPath}");
    }

    // 2) 没有配置文件或要求刷新：拉官方规则生成配置文件
    var official = await FetchOfficialProjectsAsync();
    if (official.Count <= 0)
    {
        if (config != null)
        {
            Log.Warn(tag, "拉取官方规则失败，继续使用现有配置文件。");
            return SelectEnabled(config.ToProjects(), all, excludes);
        }
        return Array.Empty<AccelerateProjectDTO>();
    }

    // 刷新时保留用户改过的启用状态
    var newConfig = RuleConfig.FromProjects(official, config);
    try
    {
        newConfig.Save(configPath);
        Console.WriteLine($"规则配置已写入: {configPath}");
    }
    catch (Exception ex)
    {
        Log.Error(tag, $"写入规则配置文件失败: {ex.Message}");
    }

    return SelectEnabled(newConfig.ToProjects(), all, excludes);
}

/// <summary>
/// 从官方接口拉取全部规则（顶层项目）。
/// </summary>
static async Task<IReadOnlyList<AccelerateProjectDTO>> FetchOfficialProjectsAsync()
{
    var cacheFilePath = Path.Combine(IOPath.AppDataDirectory, OfficialRulesCacheFileName);

    try
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(HttpPlatformHelperConsoleService.DefaultUserAgent);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, apiBaseUrl + accelerateProjectGroupsPath)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var projects = ParseOfficialProjects(json);
        if (projects.Count > 0)
        {
            await File.WriteAllTextAsync(cacheFilePath, json, Encoding.UTF8);
            return projects;
        }
    }
    catch (Exception ex)
    {
        Log.Warn(tag, $"从官方接口获取加速规则失败: {ex.Message}");
    }

    // 接口不可用时回落到上次抓取的原始 JSON
    try
    {
        if (File.Exists(cacheFilePath))
        {
            var json = await File.ReadAllTextAsync(cacheFilePath);
            var projects = ParseOfficialProjects(json);
            if (projects.Count > 0)
            {
                Console.WriteLine("官方接口不可用，使用本地缓存的原始规则。");
                return projects;
            }
        }
    }
    catch (Exception ex)
    {
        Log.Warn(tag, $"读取本地加速规则缓存失败: {ex.Message}");
    }

    return Array.Empty<AccelerateProjectDTO>();
}

/// <summary>
/// 解析官方接口返回的原始 JSON，取出全部顶层项目。
/// </summary>
static IReadOnlyList<AccelerateProjectDTO> ParseOfficialProjects(string json)
{
    var rsp = JsonSerializer.Deserialize<ApiRsp<List<AccelerateProjectGroupDTO>>>(json);
    var groups = rsp?.Content;
    if (groups == null || groups.Count <= 0)
    {
        return Array.Empty<AccelerateProjectDTO>();
    }

    return groups
        .SelectMany(x => x.Items ?? new List<AccelerateProjectDTO>())
        .ToArray();
}

/// <summary>
/// 按配置文件里的开关与命令行覆盖项筛选启用的规则。
/// </summary>
static IReadOnlyList<AccelerateProjectDTO> SelectEnabled(
    IReadOnlyList<AccelerateProjectDTO> projects,
    bool all,
    IReadOnlyCollection<string> excludes)
{
    IEnumerable<AccelerateProjectDTO> result = projects;

    if (!all)
    {
        result = result.Where(x => x.Checked);
    }

    if (excludes.Count > 0)
    {
        result = result.Where(x => !excludes.Any(e =>
            string.Equals(e, x.Name, StringComparison.OrdinalIgnoreCase)));
    }

    return result.ToArray();
}

// ---------------------------------------------------------------------------
// 命令行
// ---------------------------------------------------------------------------

static Dictionary<string, string?> ParseArgs(string[] args)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        var arg = args[i];
        if (!arg.StartsWith("--"))
        {
            continue;
        }
        var key = arg[2..];
        string? value = null;
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        {
            value = args[i + 1];
            i++;
        }
        result[key] = value;
    }
    return result;
}

static string? GetString(Dictionary<string, string?> cli, string key)
    => cli.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

static void PrintUsage()
{
    Console.WriteLine($"{Constants.CUSTOM_URL_SCHEME_NAME} Accelerator —— 仅代理（反向代理）独立宿主程序");
    Console.WriteLine();
    Console.WriteLine("用法: Steam++.Accelerator [选项]");
    Console.WriteLine();
    Console.WriteLine("  --mode <system|hosts|pac|dns|proxyonly>  运行模式，默认 system");
    Console.WriteLine("      system      设置 Windows 系统代理（推荐）");
    Console.WriteLine("      hosts       改写 hosts 并本地反向代理（监听 443）");
    Console.WriteLine("      pac         通过 PAC 脚本分流");
    Console.WriteLine("      dns         WinDivert 驱动级 DNS 拦截（需要管理员）");
    Console.WriteLine("      proxyonly   只启动反向代理，不修改任何系统设置");
    Console.WriteLine("  --port <端口>                            代理监听端口，默认 26501（hosts / dns 模式固定 443）");
    Console.WriteLine("  --ip <地址>                              监听地址，默认 127.0.0.1");
    Console.WriteLine("  --dns <地址>                             上游 DNS，默认 223.5.5.5");
    Console.WriteLine("  --no-doh                                 关闭 DoH");
    Console.WriteLine("  --all                                    忽略配置文件里的开关，启用全部规则");
    Console.WriteLine("  --exclude <项目名>[,<项目名>...]        按项目名排除规则，可重复指定");
    Console.WriteLine("  --rules <路径>                          指定规则配置文件，默认 <数据目录>\\accelerate-rules.json");
    Console.WriteLine("  --refresh-rules                         从官方接口重新拉取规则并更新配置文件（保留已改动的开关）");
    Console.WriteLine("  --help                                   显示帮助");
}

// ---------------------------------------------------------------------------
// 本地服务实现
// ---------------------------------------------------------------------------

sealed class RepositorySecureStorage : Repository<KeyValuePair, string>, ISecureStorage
{
    bool ISecureStorage.IsNativeSupportedBytes => true;

    static string GetKey(string key) => Hashs.String.SHA256(key);

    async Task<byte[]?> ISecureStorage.GetBytesAsync(string key)
    {
        key = GetKey(key);
        var item = await FirstOrDefaultAsync(x => x.Id == key);
        var value = item?.Value;
        var value2 = value;
        return value2;
    }

    async Task InsertOrUpdateAsync(string key, byte[] value)
    {
        var value2 = value;
        await InsertOrUpdateAsync(new KeyValuePair
        {
            Id = key,
            Value = value2.ThrowIsNull(nameof(value2)),
        });
    }

    Task ISecureStorage.SetAsync(string key, byte[]? value)
    {
        key = GetKey(key);
        if (value == null || value.Length <= 0)
        {
            return DeleteAsync(key);
        }
        else
        {
            return InsertOrUpdateAsync(key, value);
        }
    }

    async Task<bool> ISecureStorage.RemoveAsync(string key)
    {
        key = GetKey(key);
        var result = await DeleteAsync(key);
        return result > 0;
    }

    async Task<bool> ISecureStorage.ContainsKeyAsync(string key)
    {
        key = GetKey(key);
        var item = await FirstOrDefaultAsync(x => x.Id == key);
        return item != null;
    }
}

sealed class HttpPlatformHelperConsoleService : HttpPlatformHelperService
{
    public new const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/90.0.4430.93 Safari/537.36 Edg/90.0.818.51";

    public override string UserAgent => DefaultUserAgent;
}

// ---------------------------------------------------------------------------
// Windows 系统代理 / PAC
// ---------------------------------------------------------------------------

static class SystemProxyHelper
{
    const string TAG = "SystemProxy";

    /// <summary>Internet Settings 注册表键（相对 HKCU）。</summary>
    const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>不使用代理的地址，与上游保持一致（本地与内网直连）。</summary>
    const string ProxyOverride = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    /// <summary>
    /// 启用前的原始值，退出时还原，避免破坏用户原有代理配置。
    /// </summary>
    static readonly Dictionary<string, (object? Value, RegistryValueKind Kind)> originalValues = new();

    static bool originalCaptured;

    public static Task<bool> SetSystemProxyAsync(bool state, string? ip, int port)
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn(TAG, "系统代理设置仅支持 Windows。");
            return Task.FromResult(false);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                Log.Error(TAG, $"无法打开注册表项 HKCU\\{InternetSettingsKey}");
                return Task.FromResult(false);
            }

            CaptureOriginal(key);

            if (state)
            {
                if (string.IsNullOrWhiteSpace(ip) || port <= 0)
                {
                    Log.Error(TAG, "代理地址或端口无效。");
                    return Task.FromResult(false);
                }
                key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                key.SetValue("ProxyServer", $"{ip}:{port}", RegistryValueKind.String);
                key.SetValue("ProxyOverride", ProxyOverride, RegistryValueKind.String);
            }
            else
            {
                RestoreOriginal(key, "ProxyEnable", "ProxyServer", "ProxyOverride");
            }

            RefreshWinINet();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(TAG, ex.ToString());
            return Task.FromResult(false);
        }
    }

    public static Task<bool> SetPacAsync(bool state, string? url)
    {
        if (!OperatingSystem.IsWindows())
        {
            Log.Warn(TAG, "PAC 设置仅支持 Windows。");
            return Task.FromResult(false);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(InternetSettingsKey, writable: true);
            if (key == null)
            {
                Log.Error(TAG, $"无法打开注册表项 HKCU\\{InternetSettingsKey}");
                return Task.FromResult(false);
            }

            CaptureOriginal(key);

            if (state)
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    Log.Error(TAG, "PAC 地址无效。");
                    return Task.FromResult(false);
                }
                key.SetValue("AutoConfigURL", url, RegistryValueKind.String);
            }
            else
            {
                RestoreOriginal(key, "AutoConfigURL");
            }

            RefreshWinINet();
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Log.Error(TAG, ex.ToString());
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 记录首次修改前的原始值（只记录一次）。
    /// </summary>
    static void CaptureOriginal(RegistryKey key)
    {
        if (originalCaptured)
        {
            return;
        }
        originalCaptured = true;

        foreach (var name in new[] { "ProxyEnable", "ProxyServer", "ProxyOverride", "AutoConfigURL" })
        {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null)
            {
                originalValues[name] = (value, key.GetValueKind(name));
            }
        }
    }

    /// <summary>
    /// 还原原始值；原本不存在的值则删除。
    /// </summary>
    static void RestoreOriginal(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            if (originalValues.TryGetValue(name, out var original) && original.Value != null)
            {
                key.SetValue(name, original.Value, original.Kind);
            }
            else
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    const int InternetOptionRefresh = 37;
    const int InternetOptionSettingsChanged = 39;

    [DllImport("wininet.dll", SetLastError = true)]
    static extern bool InternetSetOption(nint hInternet, int dwOption, nint lpBuffer, int dwBufferLength);

    /// <summary>通知 WinINet 配置已变化，避免必须重启浏览器/系统才生效。</summary>
    static void RefreshWinINet()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        try
        {
            InternetSetOption(default, InternetOptionSettingsChanged, default, 0);
            InternetSetOption(default, InternetOptionRefresh, default, 0);
        }
        catch (Exception ex)
        {
            Log.Warn(TAG, ex.Message);
        }
    }
}

// ---------------------------------------------------------------------------
// hosts 文件改写（hosts 模式）
// ---------------------------------------------------------------------------

static class HostsFileHelper
{
    const string TAG = "Hosts";

    const string BeginTag = "# --- Steam++Accelerator BEGIN ---";
    const string EndTag = "# --- Steam++Accelerator END ---";

    static string GetHostsFilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrWhiteSpace(system32))
            {
                return Path.Combine(system32, "drivers", "etc", "hosts");
            }
        }
        return "/etc/hosts";
    }

    public static async Task<bool> ApplyAsync(IEnumerable<AccelerateProjectDTO> projects, string ip)
    {
        try
        {
            // 与上游一致：使用监听域名（ListenDomainNames），并支持 "ip domain" 形式指定单独的目标地址
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                foreach (var host in project.ListeningDomainNamesArray)
                {
                    if (string.IsNullOrWhiteSpace(host))
                    {
                        continue;
                    }
                    if (host.Contains(' '))
                    {
                        var parts = host.Split(' ', 2);
                        entries[parts[1].Trim()] = parts[0].Trim();
                    }
                    else
                    {
                        entries[host.Trim()] = ip;
                    }
                }
            }

            if (entries.Count <= 0)
            {
                Log.Warn(TAG, "没有可写入 hosts 的域名。");
                return false;
            }

            var lines = new List<string>
            {
                BeginTag,
                "# Auto-generated by Steam++ Accelerator. Do not edit manually.",
            };
            lines.AddRange(entries.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Value} {x.Key}"));
            lines.Add(EndTag);

            return await UpdateAsync(lines);
        }
        catch (Exception ex)
        {
            Log.Error(TAG, ex.ToString());
            return false;
        }
    }

    public static Task<bool> RevertAsync() => UpdateAsync(Array.Empty<string>());

    static async Task<bool> UpdateAsync(IReadOnlyList<string> block)
    {
        try
        {
            var hostsFilePath = GetHostsFilePath();
            if (!File.Exists(hostsFilePath))
            {
                Log.Error(TAG, $"hosts 文件不存在: {hostsFilePath}");
                return false;
            }

            // hosts 文件由系统按 ANSI/UTF-8 混合读取；这里按 Latin-1 做字节保真的读写，
            // 避免剥掉 BOM、把用户原有的非 ASCII 注释改写成乱码，或吞掉文件末尾的空行。
            // 只替换 BEGIN/END 标记之间的内容，其余部分原样保留。
            var rawBytes = await File.ReadAllBytesAsync(hostsFilePath);
            var content = Encoding.Latin1.GetString(rawBytes);
            var lines = content.Split('\n').ToList();
            var lineEnding = content.Contains("\r\n", StringComparison.Ordinal) ? "\r" : string.Empty;

            var beginIndex = lines.FindIndex(x => x.TrimEnd('\r').Trim() == BeginTag);
            var endIndex = lines.FindIndex(x => x.TrimEnd('\r').Trim() == EndTag);
            if (beginIndex >= 0 && endIndex > beginIndex)
            {
                lines.RemoveRange(beginIndex, endIndex - beginIndex + 1);
            }

            if (block.Count > 0)
            {
                var insertIndex = lines.FindLastIndex(x => !string.IsNullOrWhiteSpace(x)) + 1;
                lines.InsertRange(insertIndex, block.Select(x => x + lineEnding));
            }

            var result = string.Join("\n", lines);
            await File.WriteAllBytesAsync(hostsFilePath, Encoding.Latin1.GetBytes(result));

            Log.Info(TAG, $"hosts 已更新: {hostsFilePath} ({block.Count} 行)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(TAG, ex.ToString());
            return false;
        }
    }
}
