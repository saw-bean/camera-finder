using System.Security.Cryptography;
using System.Text;

namespace CameraFinder.Core.Health;

/// <summary>The parts of a camera that each check a login on their own.</summary>
public enum LoginService
{
    /// <summary>ONVIF (identity, log, profiles, stream address).</summary>
    Onvif,
    /// <summary>RTSP video (live bandwidth, delay, ffmpeg).</summary>
    Video,
}

/// <summary>
/// Remembers, for this session, which logins a camera refused - per service, because many cameras keep separate user
/// lists: Hikvision, Dahua and Axis have ONVIF users apart from the web/RTSP users, so a login ONVIF refuses can still
/// play the video. A refused login is never tried again on that service automatically (cameras lock accounts after a
/// few failures); a login typed in by the user clears the refusals on both. Holds only hashes of passwords.
/// </summary>
public sealed class LoginBook
{
    private readonly HashSet<string> _refused = new();

    public void Refuse(string device, LoginService service, Credentials login)
    {
        lock (_refused) _refused.Add(Key(device, service, login));
    }

    public bool IsRefused(string device, LoginService service, Credentials login)
    {
        lock (_refused) return _refused.Contains(Key(device, service, login));
    }

    /// <summary>The user typed this login in: let every service try it again.</summary>
    public void Allow(string device, Credentials login)
    {
        lock (_refused)
            foreach (var s in Enum.GetValues<LoginService>()) _refused.Remove(Key(device, s, login));
    }

    /// <summary>The first candidate (in order of preference) this service has not refused, or null.</summary>
    public Credentials? Pick(string device, LoginService service, IEnumerable<Credentials?> candidates) =>
        candidates.FirstOrDefault(c => c is { IsEmpty: false } && !IsRefused(device, service, c));

    private static string Key(string device, LoginService service, Credentials c) =>
        $"{device}|{service}|{c.User}|{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.Password)))}";
}
