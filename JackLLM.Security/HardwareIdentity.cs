using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace JackLLM.Security;

public static class HardwareIdentity {
    public static string Compute(string helloPublicKey) {
        string machineGuid = "";
        using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
            machineGuid = key?.GetValue("MachineGuid") as string ?? "";
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        GetVolumeInformation(systemRoot, null, 0, out uint volumeSerial, out _, out _, null, 0);
        string material = string.Join("|", machineGuid, volumeSerial.ToString("X8"),
            Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "", helloPublicKey);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder? volumeNameBuffer,
        int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength,
        out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int nFileSystemNameSize);
}
