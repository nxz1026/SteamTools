// ReSharper disable once InconsistentNaming
namespace BD.WTTS.Services.Implementation;

/// <summary>
/// 加速规则配置文件。
/// <para>
/// 文件位置（默认）：<c>%LOCALAPPDATA%\Steam++Accelerator\accelerate-rules.json</c>，
/// 也可用 <c>--rules &lt;路径&gt;</c> 指定其它位置。
/// </para>
/// <para>
/// 配置是规则的唯一来源：程序启动时直接读文件，不再联网。
/// 需要拉取官方最新规则时用 <c>--refresh-rules</c> 重新生成（已改动的 <c>enabled</c> 状态按名字保留）。
/// </para>
/// </summary>
public sealed class RuleConfig
{
    /// <summary>配置文件格式版本。</summary>
    public int Version { get; set; } = 1;

    /// <summary>生成时间（UTC）。</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>规则来源接口，仅作记录。</summary>
    public string Source { get; set; } = "https://api.steampp.net/accelerator/projectgroups";

    /// <summary>规则列表。</summary>
    public List<RuleEntry> Rules { get; set; } = new();

    /// <summary>
    /// 单条规则。
    /// </summary>
    public sealed class RuleEntry
    {
        /// <summary>规则名称，同时也是与官方规则集比对的键。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>是否启用。关掉的规则不会参与加速。</summary>
        public bool Enabled { get; set; }

        /// <summary>命中域名，分号分隔。</summary>
        public string MatchDomainNames { get; set; } = string.Empty;

        /// <summary>转发目标域名或 IP。</summary>
        public string ForwardDomainNames { get; set; } = string.Empty;

        /// <summary>监听域名，分号分隔；hosts 模式用它。</summary>
        public string ListenDomainNames { get; set; } = string.Empty;

        /// <summary>目标端口。</summary>
        public ushort Port { get; set; }

        /// <summary>
        /// 代理类型：0 = 本地代理，1 = 重定向，2 = 直连成功，3 = 服务端代理。
        /// </summary>
        public byte ProxyType { get; set; }

        /// <summary>是否忽略服务端证书名不匹配。</summary>
        public bool IgnoreSSLCertVerification { get; set; }

        /// <summary>伪造的 SNI，为空表示用原始域名。</summary>
        public string? FakeServerName { get; set; }

        /// <summary>伪造的 User-Agent。</summary>
        public string? FakeUserAgent { get; set; }

        /// <summary>子规则，用于更细的路径匹配。</summary>
        public List<RuleEntry> Items { get; set; } = new();
    }

    /// <summary>
    /// 默认启用的规则：名称以 Steam 或 Github 开头的项目。
    /// </summary>
    internal static bool IsEnabledByDefault(string name)
        => name.StartsWith("Steam", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Github", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 从官方规则集生成配置。
    /// </summary>
    /// <param name="projects">官方规则（顶层项目）。</param>
    /// <param name="previous">已有配置，用于保留用户改过的 <see cref="RuleEntry.Enabled"/> 状态。</param>
    public static RuleConfig FromProjects(
        IEnumerable<AccelerateProjectDTO> projects,
        RuleConfig? previous = null)
    {
        var previousEnabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (previous != null)
        {
            foreach (var rule in previous.Rules)
            {
                previousEnabled[rule.Name] = rule.Enabled;
            }
        }

        var config = new RuleConfig
        {
            GeneratedAt = DateTimeOffset.UtcNow,
        };

        foreach (var project in projects)
        {
            config.Rules.Add(Convert(project, previousEnabled));
        }

        return config;
    }

    static RuleEntry Convert(AccelerateProjectDTO project, Dictionary<string, bool> previousEnabled)
    {
        var enabled = previousEnabled.TryGetValue(project.Name, out var previous)
            ? previous
            : IsEnabledByDefault(project.Name);

        var entry = new RuleEntry
        {
            Name = project.Name,
            Enabled = enabled,
            MatchDomainNames = project.MatchDomainNames,
            ForwardDomainNames = project.ForwardDomainNames,
            ListenDomainNames = project.ListenDomainNames,
            Port = project.Port,
            ProxyType = (byte)project.ProxyType,
            IgnoreSSLCertVerification = project.IgnoreSSLCertVerification,
            FakeServerName = project.FakeServerName,
            FakeUserAgent = project.FakeUserAgent,
        };

        if (project.Items != null)
        {
            foreach (var child in project.Items)
            {
                entry.Items.Add(Convert(child, previousEnabled));
            }
        }

        return entry;
    }

    /// <summary>
    /// 转成引擎使用的规则对象。
    /// </summary>
    public List<AccelerateProjectDTO> ToProjects()
    {
        var result = new List<AccelerateProjectDTO>();
        foreach (var rule in Rules)
        {
            var project = ToProject(rule);
            if (project != null)
            {
                result.Add(project);
            }
        }
        return result;
    }

    static AccelerateProjectDTO? ToProject(RuleEntry rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return null;
        }

        var project = new AccelerateProjectDTO
        {
            Name = rule.Name,
            Checked = rule.Enabled,
            MatchDomainNames = rule.MatchDomainNames,
            ForwardDomainNames = rule.ForwardDomainNames,
            ListenDomainNames = rule.ListenDomainNames,
            Port = rule.Port,
            ProxyType = (ProxyType)rule.ProxyType,
            IgnoreSSLCertVerification = rule.IgnoreSSLCertVerification,
            FakeServerName = rule.FakeServerName,
            FakeUserAgent = rule.FakeUserAgent,
        };

        if (rule.Items.Count > 0)
        {
            var items = new List<AccelerateProjectDTO>();
            foreach (var child in rule.Items)
            {
                var childProject = ToProject(child);
                if (childProject != null)
                {
                    items.Add(childProject);
                }
            }
            if (items.Count > 0)
            {
                project.Items = items;
            }
        }

        return project;
    }

    /// <summary>
    /// 写出配置文件（UTF-8，缩进便于手工编辑，每条规则后附一行中文说明）。
    /// </summary>
    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var header =
            "// Steam++ Accelerator 加速规则配置\n" +
            "// 直接改这个文件即可控制启用的规则，改完重启程序生效。\n" +
            "//   enabled = true/false  是否启用该规则\n" +
            "//   matchDomainNames     命中域名（分号分隔）\n" +
            "//   forwardDomainNames   转发目标（国内加速节点）\n" +
            "//   listenDomainNames    hosts 模式写入的域名\n" +
            "// 每条规则下面都有一行 // 注释，说明这条规则的作用。\n" +
            "// 用 --refresh-rules 可从官方接口拉取最新规则，已改动的 enabled 状态按名字保留。\n";

        File.WriteAllText(path, header + WriteJson() + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// 手工拼装 JSON：标准序列化器不支持注释，这里逐条写出并在规则后插入说明。
    /// </summary>
    string WriteJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append($"  \"version\": {Version},\n");
        sb.Append($"  \"generatedAt\": {Quote(GeneratedAt.ToString("O"))},\n");
        sb.Append($"  \"source\": {Quote(Source)},\n");
        sb.Append("  \"rules\": [\n");
        for (var i = 0; i < Rules.Count; i++)
        {
            WriteRule(sb, Rules[i], 4);
            if (i < Rules.Count - 1)
            {
                sb.Append(',');
            }
            sb.Append('\n');
            sb.Append("    // ").Append(Describe(Rules[i])).Append('\n');
        }
        sb.Append("  ]\n");
        sb.Append('}');
        return sb.ToString();
    }

    static void WriteRule(StringBuilder sb, RuleEntry rule, int indent)
    {
        var pad = new string(' ', indent);
        sb.Append(pad).Append("{\n");
        sb.Append(pad).Append("  \"name\": ").Append(Quote(rule.Name)).Append(",\n");
        sb.Append(pad).Append("  \"enabled\": ").Append(rule.Enabled ? "true" : "false").Append(",\n");
        sb.Append(pad).Append("  \"matchDomainNames\": ").Append(Quote(rule.MatchDomainNames)).Append(",\n");
        sb.Append(pad).Append("  \"forwardDomainNames\": ").Append(Quote(rule.ForwardDomainNames)).Append(",\n");
        sb.Append(pad).Append("  \"listenDomainNames\": ").Append(Quote(rule.ListenDomainNames)).Append(",\n");
        sb.Append(pad).Append("  \"port\": ").Append(rule.Port).Append(",\n");
        sb.Append(pad).Append("  \"proxyType\": ").Append(rule.ProxyType).Append(",\n");
        sb.Append(pad).Append("  \"ignoreSslCertVerification\": ").Append(rule.IgnoreSSLCertVerification ? "true" : "false").Append(",\n");
        sb.Append(pad).Append("  \"fakeServerName\": ").Append(Quote(rule.FakeServerName)).Append(",\n");
        sb.Append(pad).Append("  \"fakeUserAgent\": ").Append(Quote(rule.FakeUserAgent)).Append(",\n");

        if (rule.Items.Count > 0)
        {
            sb.Append(pad).Append("  \"items\": [\n");
            for (var i = 0; i < rule.Items.Count; i++)
            {
                WriteRule(sb, rule.Items[i], indent + 4);
                if (i < rule.Items.Count - 1)
                {
                    sb.Append(',');
                }
                sb.Append('\n');
                sb.Append(new string(' ', indent + 4)).Append("// ").Append(Describe(rule.Items[i])).Append('\n');
            }
            sb.Append(pad).Append("  ]\n");
        }
        else
        {
            sb.Append(pad).Append("  \"items\": []\n");
        }

        sb.Append(pad).Append('}');
    }

    /// <summary>
    /// JSON 字符串转义。
    /// </summary>
    static string Quote(string? value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>
    /// 生成规则的中文说明。
    /// </summary>
    internal static string Describe(RuleEntry rule)
    {
        var domains = SplitDomains(rule.MatchDomainNames);
        var domainText = domains.Count switch
        {
            0 => "（未指定域名）",
            1 => domains[0],
            <= 3 => string.Join("、", domains),
            _ => $"{domains[0]} 等 {domains.Count} 个域名",
        };

        var target = string.IsNullOrWhiteSpace(rule.ForwardDomainNames) ? "（无转发目标）" : rule.ForwardDomainNames;

        var text = rule.ProxyType switch
        {
            1 => $"重定向：{domainText} 的请求整体跳转到 {target}",
            2 => $"直接成功：命中 {domainText} 后直接返回成功，不做转发",
            3 => $"直接失败：命中 {domainText} 后直接返回失败",
            4 => $"服务器加速：{domainText} 由加速服务器 {target} 中转",
            _ => $"本地代理：{domainText} 直连 {target} 取数据，保留原始 Host",
        };

        if (rule.Items.Count > 0)
        {
            text += $"；含 {rule.Items.Count} 条更细的子规则";
        }

        return text;
    }

    static List<string> SplitDomains(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// 读取配置文件。
    /// </summary>
    /// <param name="path">配置文件路径。</param>
    /// <param name="error">解析失败时的原因；文件不存在时为 null。</param>
    /// <returns>配置；文件不存在或解析失败时为 null。</returns>
    public static RuleConfig? Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                // 允许用户在配置里写 // 注释
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var config = JsonSerializer.Deserialize<RuleConfig>(json, options);
            if (config == null || config.Rules.Count <= 0)
            {
                error = "配置文件里没有 rules 规则项";
                return null;
            }
            return config;
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }
}
