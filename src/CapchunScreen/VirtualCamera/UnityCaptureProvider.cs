using Microsoft.Win32;

namespace CapchunScreen.VirtualCamera;

public sealed record UnityCaptureDevice(int Index, string Name, bool Registered64Bit, bool Registered32Bit);
public sealed record UnityCaptureProviderStatus(bool Available, string Message, IReadOnlyList<UnityCaptureDevice> Devices);

public static class UnityCaptureProvider
{
    public const string ProjectUrl = "https://github.com/schellingb/UnityCapture";

    /// <summary>既存のDirectShow登録と参照DLLだけを調べる。登録・DLLロード・カメラ接続は行わない。</summary>
    public static UnityCaptureProviderStatus Probe()
    {
        try
        {
            var x64 = ReadRegistration(RegistryView.Registry64, "{5C2CD55C-92AD-4999-8666-912BD3E70010}");
            var x86 = ReadRegistration(RegistryView.Registry32, "{5C2CD55C-92AD-4999-8666-912BD3E70020}");
            if (!x64.Valid && !x86.Valid)
            {
                string reason = x64.Registered || x86.Registered
                    ? "Unity Captureの登録はありますが、登録先のドライバーDLLを確認できません。既存の導入状態を確認してください。"
                    : "Unity Captureが導入されていません。仮想カメラ配信にはUnity Video Captureのドライバーが必要です。";
                return new(false, reason + " アプリはドライバーを自動導入しません。", []);
            }
            var device = new UnityCaptureDevice(0, x64.Valid ? x64.Name : x86.Name, x64.Valid, x86.Valid);
            return new(true, "Unity Captureを確認しました。受信アプリで「" + device.Name + "」をカメラとして選択してください。", [device]);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return new(false, "Unity Captureの登録を確認できません: " + error.Message, []);
        }
    }

    private static (bool Registered, bool Valid, string Name) ReadRegistration(RegistryView view, string classId)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, view);
        using var registration = root.OpenSubKey(@"CLSID\" + classId);
        if (registration is null) return (false, false, "");
        using var server = registration.OpenSubKey("InprocServer32");
        string path = Environment.ExpandEnvironmentVariables((server?.GetValue(null) as string ?? "").Trim().Trim('"'));
        string name = (registration.GetValue(null) as string ?? "Unity Video Capture").Trim();
        return (true, Path.IsPathFullyQualified(path) && File.Exists(path), name);
    }
}
