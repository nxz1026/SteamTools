// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services;

/// <summary>
/// 平台服务的最小替代实现。
/// <para>原类型由 GUI 主进程通过 dotnetCampus.Ipc 提供，裁剪为独立进程后改为本地实现。</para>
/// </summary>
public partial interface IPCPlatformService
{
    /// <summary>
    /// 判断证书是否已安装
    /// </summary>
    /// <param name="certificate2">序列化后的 <see cref="X509CertificatePackable"/></param>
    /// <returns></returns>
    bool IsCertificateInstalled(byte[] certificate2);

    /// <summary>
    /// 根据证书文件路径信任根证书
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    bool? TrustRootCertificateAsync(string filePath);

    /// <summary>
    /// 删除证书
    /// </summary>
    /// <param name="certificate2">序列化后的 <see cref="X509CertificatePackable"/></param>
    void RemoveCertificate(byte[] certificate2);
}

public static class IPCPlatformServiceExtensions
{
    /// <inheritdoc cref="IPCPlatformService.IsCertificateInstalled(byte[])"/>
    public static bool IsCertificateInstalled(
        this IPCPlatformService platformService,
        X509CertificatePackable certificate2)
    {
        var certificate2_ = Serializable.SMP2(certificate2);
        return platformService.IsCertificateInstalled(certificate2_);
    }

    /// <inheritdoc cref="IPCPlatformService.RemoveCertificate(byte[])"/>
    public static void RemoveCertificate(
        this IPCPlatformService platformService,
        X509CertificatePackable certificate2)
    {
        var certificate2_ = Serializable.SMP2(certificate2);
        platformService.RemoveCertificate(certificate2_);
    }
}
