using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeHomeServer.DeviceAgent.Cli;

/// <summary>
/// Проверка платформенной подписи скачанного бинаря — второй слой поверх подписанного
/// манифеста. Бросает <see cref="CliIntegrityException"/>, если подпись не годится.
/// </summary>
public interface IExecutableSignatureCheck
{
    void Verify(string executablePath);
}

public static class ExecutableSignatureCheck
{
    /// <summary>Подписант Windows-сборок CLI по официальной документации (раздел «Platform code signatures»).</summary>
    public const string AnthropicWindowsSigner = "Anthropic, PBC";

    /// <summary>
    /// Боевой выбор: на Windows — Authenticode с подписантом Anthropic; на Linux бинари не
    /// подписываются вовсе (по документации), там доверие держит только подпись манифеста.
    /// </summary>
    public static IExecutableSignatureCheck ForCurrentOs() =>
        OperatingSystem.IsWindows() ? new AuthenticodeCheck(AnthropicWindowsSigner) : None;

    public static IExecutableSignatureCheck None { get; } = new NoCheck();

    private sealed class NoCheck : IExecutableSignatureCheck
    {
        public void Verify(string executablePath) { }
    }
}

/// <summary>
/// Authenticode через <c>WinVerifyTrust</c> (то же, что <c>Get-AuthenticodeSignature</c> из
/// документации) плюс сверка CN подписанта. Отзыв сертификата не проверяется намеренно:
/// точный SHA256 бинаря уже заверен GPG-подписью манифеста, а недоступный CRL в корпоративной
/// сети навсегда клал бы харнес с невнятной причиной.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AuthenticodeCheck(string expectedSigner) : IExecutableSignatureCheck
{
    public void Verify(string executablePath)
    {
        var status = WinVerifyTrustFile(executablePath);
        if (status != 0)
            throw new CliIntegrityException(
                $"Authenticode-подпись {Path.GetFileName(executablePath)} не прошла проверку (WinVerifyTrust 0x{status:X8})");

        string? signer;
        try
        {
#pragma warning disable SYSLIB0057 // Нужен именно сертификат подписи файла, другого API в BCL нет.
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(executablePath));
#pragma warning restore SYSLIB0057
            signer = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException)
        {
            throw new CliIntegrityException($"не удалось прочитать подписанта {Path.GetFileName(executablePath)}: {e.Message}");
        }

        if (!string.Equals(signer, expectedSigner, StringComparison.Ordinal))
            throw new CliIntegrityException(
                $"{Path.GetFileName(executablePath)} подписан «{signer}», ожидался «{expectedSigner}»");
    }

    private static int WinVerifyTrustFile(string path)
    {
        var action = WintrustActionGenericVerifyV2;
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = filePath,
        };
        var fileInfoPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPtr,
                dwStateAction = WtdStateActionVerify,
            };
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            // Состояние проверки обязательно закрывается вторым вызовом.
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileInfoPtr);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionId, ref WintrustData pWvtData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
