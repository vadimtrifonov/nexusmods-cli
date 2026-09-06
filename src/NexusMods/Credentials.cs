using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NexusMods;

internal static class Credentials
{
    internal const string Target = "skill-nexus";
    internal const string Source = "windows-credential-manager";
    private const uint Generic = 1;
    private const int NotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        internal uint Flags, Type;
        internal string? TargetName, Comment;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        internal uint CredentialBlobSize;
        internal nint CredentialBlob;
        internal uint Persist, AttributeCount;
        internal nint Attributes;
        internal string? TargetAlias, UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out nint credential);
    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("Advapi32.dll")]
    private static extern void CredFree(nint buffer);

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new CliException("WINDOWS_REQUIRED", "Credential storage is supported only on Windows.", Source);
    }
    private static CliException Failed() => new("CREDENTIAL_MANAGER_FAILED", "Windows Credential Manager could not be accessed.", Source);
    private static nint ReadPointer(string target)
    {
        RequireWindows();
        if (CredRead(target, Generic, 0, out var pointer)) return pointer;
        if (Marshal.GetLastWin32Error() == NotFound) return 0;
        throw Failed();
    }
    internal static bool Exists(string target = Target)
    {
        var pointer = ReadPointer(target);
        if (pointer == 0) return false;
        CredFree(pointer);
        return true;
    }
    internal static string Load(string target = Target)
    {
        var pointer = ReadPointer(target);
        if (pointer == 0) throw new CliException("AUTH_NOT_CONFIGURED",
            "No Nexus API key is configured. Run `nexusmods auth set` in an interactive terminal.", Source);
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0)
                throw new CliException("CREDENTIAL_READ_FAILED", "The stored Nexus credential is empty.", Source);
            if (credential.CredentialBlobSize > 2560 || credential.CredentialBlobSize % 2 != 0) throw Failed();
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / 2))!;
        }
        finally { CredFree(pointer); }
    }
    internal static void Store(string key, string target = Target)
    {
        RequireWindows();
        var bytes = Encoding.Unicode.GetBytes(key);
        if (bytes.Length is 0 or > 2560)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw Failed();
        }
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new Credential
            {
                Type = Generic, TargetName = target, Comment = "Nexus Mods personal API key", UserName = "default",
                CredentialBlobSize = (uint)bytes.Length, CredentialBlob = buffer, Persist = 2
            };
            if (!CredWrite(ref credential, 0)) throw Failed();
        }
        finally
        {
            for (var i = 0; i < bytes.Length; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.FreeHGlobal(buffer);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    internal static bool Remove(string target = Target)
    {
        RequireWindows();
        if (CredDelete(target, Generic, 0)) return true;
        if (Marshal.GetLastWin32Error() == NotFound) return false;
        throw Failed();
    }
    internal static string Prompt()
    {
        if (Console.IsInputRedirected || Console.IsErrorRedirected)
            throw new CliException("INTERACTIVE_TERMINAL_REQUIRED", "`auth set` requires an interactive terminal.");
        Console.Error.Write("Nexus personal API key: ");
        var previous = Console.TreatControlCAsInput;
        Console.TreatControlCAsInput = true;
        try
        {
            var value = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.KeyChar == '\u0003') throw new CliException("CANCELLED", "Credential setup was cancelled.");
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (value.Length > 0) value.Length--; }
                else if (key.KeyChar >= ' ' && value.Length < 1024) value.Append(key.KeyChar);
            }
            if (value.Length == 0) throw new CliException("EMPTY_API_KEY", "The API key cannot be empty.");
            return value.ToString();
        }
        finally { Console.TreatControlCAsInput = previous; Console.Error.WriteLine(); }
    }
}
