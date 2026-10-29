// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services.Implementation;

/// <summary>
/// <see cref="IPCToastService"/> 的本地实现：写入 NLog 与控制台。
/// </summary>
sealed class IPCToastServiceImpl : IPCToastService
{
    const string TAG = "Accelerator";

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text)
        => Write(icon, text, null);

    public void Show(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, params string?[] args)
    {
        string? message = null;
        if (args.Length > 0)
        {
            try
            {
                message = string.Format(GetText(text), args);
            }
            catch (FormatException)
            {
                message = GetText(text) + " " + string.Join(' ', args);
            }
        }
        Write(icon, text, message);
    }

    public void ShowAppend(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, string? appendText)
        => Write(icon, text, appendText);

    static void Write(IPCToastService.ToastIcon icon, IPCToastService.ToastText text, string? appendText)
    {
        var message = GetText(text);
        if (!string.IsNullOrEmpty(appendText))
        {
            message += " " + appendText;
        }

        var line = $"[{icon}] {message}";
        switch (icon)
        {
            case IPCToastService.ToastIcon.Error:
                Log.Error(TAG, line);
                break;
            case IPCToastService.ToastIcon.Warning:
                Log.Warn(TAG, line);
                break;
            default:
                Log.Info(TAG, line);
                break;
        }
        Console.WriteLine(line);
    }

    static string GetText(IPCToastService.ToastText text) => text switch
    {
        IPCToastService.ToastText.CreateCertificateFaild => "创建根证书失败",
        IPCToastService.ToastText.CommunityFix_DNSErrorNotify => "域名解析失败",
        IPCToastService.ToastText.CommunityFix_OnRunCatch => "加速引擎运行异常",
        _ => text.ToString(),
    };
}
