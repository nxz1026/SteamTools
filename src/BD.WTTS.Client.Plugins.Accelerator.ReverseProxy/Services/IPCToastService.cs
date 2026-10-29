// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

/// <summary>
/// 吐司通知管理的最小替代实现。
/// <para>原类型由 GUI 主进程通过 dotnetCampus.Ipc 提供，裁剪为独立进程后改为本地 NLog/控制台输出。</para>
/// </summary>
public interface IPCToastService
{
    enum ToastText
    {
        CreateCertificateFaild = 1,
        CommunityFix_DNSErrorNotify,

        [Obsolete]
        CommunityFix_OnRunCatch,
    }

    /// <summary>
    /// Toast 显示的图标
    /// </summary>
    enum ToastIcon : byte
    {
        /// <summary>
        /// 无图标
        /// </summary>
        None,

        /// <summary>
        /// ℹ
        /// </summary>
        Info,

        /// <summary>
        /// ✅
        /// </summary>
        Success,

        /// <summary>
        /// ⚠️
        /// </summary>
        Warning,

        /// <summary>
        /// ❌
        /// </summary>
        Error,
    }

    void Show(ToastIcon icon, ToastText text);

    void Show(ToastIcon icon, ToastText text, params string?[] args);

    void ShowAppend(ToastIcon icon, ToastText text, string? appendText);
}
