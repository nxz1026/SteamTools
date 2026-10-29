// ReSharper disable once CheckNamespace
namespace BD.WTTS.Services.Implementation;

/// <summary>
/// <see cref="IPCPlatformService"/> 的本地实现。
/// <para>Windows 为主要目标平台；其它平台的证书信任操作会尽力而为，失败时仅记录日志而不会崩溃。</para>
/// </summary>
sealed class IPCPlatformServiceImpl : IPCPlatformService
{
    const string TAG = "IPCPlatformService";

    static X509Certificate2? ToCertificate(byte[] certificate2)
    {
        try
        {
            var packable = Serializable.DMP2<X509CertificatePackable>(certificate2);
            return packable;
        }
        catch (Exception e)
        {
            Log.Error(TAG, e, "Deserialize X509CertificatePackable failed.");
            return null;
        }
    }

    public bool IsCertificateInstalled(byte[] certificate2)
    {
        var certificate = ToCertificate(certificate2);
        if (certificate == null || certificate.NotAfter <= DateTime.Now)
        {
            return false;
        }

        try
        {
            var location = OperatingSystem.IsWindows()
                ? StoreLocation.LocalMachine
                : StoreLocation.CurrentUser;
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
        }
        catch (Exception e)
        {
            Log.Error(TAG, e, nameof(IsCertificateInstalled));
            return false;
        }
    }

    public bool? TrustRootCertificateAsync(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Log.Error(TAG, $"Certificate file not found: {filePath}");
                return false;
            }

            if (OperatingSystem.IsWindows())
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);
                using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadWrite);
                if (store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count == 0)
                {
                    store.Add(certificate);
                }
                return true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                return RunProcess("security",
                    $"add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain \"{filePath}\"");
            }
            else if (OperatingSystem.IsLinux())
            {
                if (Environment.UserName != "root" && Environment.GetEnvironmentVariable("USER") != "root")
                {
                    Log.Error(TAG,
                        "Trusting the root certificate on Linux requires root privileges. " +
                        $"Please import it manually: {filePath}");
                    return false;
                }
                var fileName = Path.GetFileNameWithoutExtension(filePath) + ".crt";
                var dest = Path.Combine("/usr/local/share/ca-certificates", fileName);
                File.Copy(filePath, dest, overwrite: true);
                return RunProcess("update-ca-certificates", string.Empty);
            }
            return false;
        }
        catch (Exception e)
        {
            Log.Error(TAG, e, nameof(TrustRootCertificateAsync));
            return false;
        }
    }

    public void RemoveCertificate(byte[] certificate2)
    {
        var certificate = ToCertificate(certificate2);
        if (certificate == null)
        {
            return;
        }

        try
        {
            var location = OperatingSystem.IsWindows()
                ? StoreLocation.LocalMachine
                : StoreLocation.CurrentUser;
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            foreach (var item in store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false))
            {
                store.Remove(item);
            }
        }
        catch (Exception e)
        {
            Log.Error(TAG, e, nameof(RemoveCertificate));
        }
    }

    static bool RunProcess(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process == null)
            {
                return false;
            }
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                Log.Error(TAG, $"{fileName} exited with code {process.ExitCode}: {error}");
            }
            return process.ExitCode == 0;
        }
        catch (Exception e)
        {
            Log.Error(TAG, e, $"Run {fileName} failed.");
            return false;
        }
    }
}
