using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CameraFinder.Core.Health;

namespace CameraFinder.App;

/// <summary>
/// Remembers camera logins between sessions, encrypted with Windows DPAPI for the current Windows user (another user
/// of the laptop, or a copy of the file on another PC, cannot read them). Keyed by MAC address, so a login follows the
/// camera when its IP changes. Stored in %LOCALAPPDATA%\CameraFinder\logins.dat.
/// </summary>
internal sealed class CredentialStore
{
    private static readonly byte[] Entropy = "CameraFinder camera logins v1"u8.ToArray();
    private readonly string _path;
    private Dictionary<string, string>? _cache; // key -> base64(DPAPI(json))

    public CredentialStore(string? path = null) =>
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraFinder", "logins.dat");

    /// <summary>Last login used for any camera: a site usually has one password for all cameras.</summary>
    public const string LastUsedKey = "*";

    public static string KeyFor(string? mac, string ip) =>
        mac != null ? "mac:" + new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant() : "ip:" + ip;

    public Credentials? Get(string key)
    {
        if (!Load().TryGetValue(key, out var blob)) return null;
        try
        {
            var json = Unprotect(Convert.FromBase64String(blob));
            var pair = JsonSerializer.Deserialize<string[]>(Encoding.UTF8.GetString(json));
            return pair is { Length: 2 } ? new Credentials(pair[0], pair[1]) : null;
        }
        catch (Exception) { return null; } // file from another user/PC, or damaged
    }

    public void Save(string key, Credentials login)
    {
        var data = Load();
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { login.User, login.Password }));
        data[key] = Convert.ToBase64String(Protect(json));
        data[LastUsedKey] = data[key];
        Write(data);
    }

    public void Forget(string key)
    {
        var data = Load();
        if (data.Remove(key)) Write(data);
    }

    private Dictionary<string, string> Load()
    {
        if (_cache != null) return _cache;
        try { _cache = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path)) : null; }
        catch (Exception) { _cache = null; }
        return _cache ??= new Dictionary<string, string>();
    }

    private void Write(Dictionary<string, string> data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception) { /* not saved; the login still works this session */ }
    }

    // ---- DPAPI (CryptProtectData / CryptUnprotectData), current user scope

    private static byte[] Protect(byte[] plain) => Crypt(plain, protect: true);
    private static byte[] Unprotect(byte[] cipher) => Crypt(cipher, protect: false);

    private static byte[] Crypt(byte[] input, bool protect)
    {
        var inBlob = new DataBlob();
        var entropy = new DataBlob();
        var outBlob = new DataBlob();
        try
        {
            inBlob = DataBlob.From(input);
            entropy = DataBlob.From(Entropy);
            const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
            bool ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob);
            if (!ok) throw new InvalidOperationException("DPAPI error " + Marshal.GetLastWin32Error());
            var result = new byte[outBlob.Size];
            Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
            return result;
        }
        finally
        {
            inBlob.Free();
            entropy.Free();
            if (outBlob.Data != IntPtr.Zero) LocalFree(outBlob.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob From(byte[] bytes)
        {
            var b = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(Math.Max(1, bytes.Length)) };
            Marshal.Copy(bytes, 0, b.Data, bytes.Length);
            return b;
        }

        public void Free()
        {
            if (Data == IntPtr.Zero) return;
            for (int i = 0; i < Size; i++) Marshal.WriteByte(Data, i, 0); // do not leave the password in freed memory
            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved,
        IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
