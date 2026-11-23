using System.Diagnostics;

namespace CameraFinder.Core.Health;

/// <summary>One complete video frame as it arrived: when its last packet came in (monotonic seconds), its RTP time and size.</summary>
public readonly record struct FrameArrival(double At, double RtpSeconds, int Bytes, bool Key);

/// <summary>Where a live view's delay comes from, as far as it can be measured from the laptop.</summary>
public sealed class DelayReport
{
    /// <summary>Connect + RTSP set-up, seconds.</summary>
    public double? ConnectSeconds { get; set; }
    /// <summary>From connecting to the first complete key frame: how long a viewer waits for a picture.</summary>
    public double? TimeToPicture { get; set; }
    /// <summary>How much a player must buffer to play smoothly, because frames arrive unevenly (95th percentile), ms.</summary>
    public double? BufferNeededMs { get; set; }
    /// <summary>How fast the stream falls behind real time, seconds per minute (0 = keeping up).</summary>
    public double? FallingBehindPerMinute { get; set; }
    public double SecondsWatched { get; set; }
    /// <summary>Camera + network delay from the flash test (median), seconds; null until it is run.</summary>
    public double? CameraDelay { get; set; }
    public double? CameraDelayMin { get; set; }
    public double? CameraDelayMax { get; set; }
    public int FlashesSeen { get; set; }
    public int FlashesShown { get; set; }
}

/// <summary>
/// Measures live view delay from the frames of a camera's stream:
/// - time to picture (connect to first key frame) - what makes a live view slow to open;
/// - arrival burstiness: each frame's arrival compared with its RTP capture time; the spread is what a player has to
///   buffer, and buffering is delay;
/// - falling behind: if the smallest transit time keeps growing, the stream cannot keep up and delay builds up;
/// - the flash test: the laptop screen flips between a pattern and its inverse while the camera looks at it. Each flip
///   changes the whole picture, so the camera sends one much bigger frame; the time from the flip to that frame
///   arriving is the camera + network delay, measured directly without the NVR. No decoding is needed.
/// All times are monotonic (Stopwatch), so the laptop clock and the camera clock do not need to agree.
/// </summary>
public sealed class DelayTracker
{
    private readonly List<FrameArrival> _frames = new();
    private readonly List<double> _flashes = new();
    private double? _started, _playing, _firstPicture;
    private double? _rtpBase;
    private uint _lastRtp;
    private long _rtpWraps;

    public static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public void Started(double at) { lock (_frames) _started = at; }
    public void Playing(double at) { lock (_frames) _playing = at; }

    /// <summary>A frame finished arriving. <paramref name="rtp"/> is its 32-bit RTP timestamp at <paramref name="clockRate"/> Hz.</summary>
    public void AddFrame(double at, uint rtp, int clockRate, int bytes, bool key)
    {
        lock (_frames)
        {
            if (_frames.Count > 0 && rtp < _lastRtp && _lastRtp - rtp > 0x80000000u) _rtpWraps++;
            _lastRtp = rtp;
            double rtpSec = (_rtpWraps * 4294967296.0 + rtp) / clockRate;
            _rtpBase ??= rtpSec;
            if (key && _firstPicture == null) _firstPicture = at;
            _frames.Add(new FrameArrival(at, rtpSec - _rtpBase.Value, bytes, key));
            if (_frames.Count > 9000) _frames.RemoveRange(0, 1000); // ~5 minutes at 30 fps
        }
    }

    /// <summary>
    /// Average seconds between the last key frames by the camera's timestamps (capture time), or null until two have
    /// been seen. Independent of when they arrived: jitter and bursts on the network do not change it.
    /// </summary>
    public double? KeyframeInterval()
    {
        lock (_frames)
        {
            var keys = new List<double>();
            for (int i = _frames.Count - 1; i >= 0 && keys.Count < 61; i--)
                if (_frames[i].Key && (keys.Count == 0 || keys[^1] - _frames[i].RtpSeconds > 0.001)) keys.Add(_frames[i].RtpSeconds);
            return keys.Count < 2 ? null : (keys[0] - keys[^1]) / (keys.Count - 1);
        }
    }

    /// <summary>The screen just flipped (flash test).</summary>
    public void AddFlash(double at) { lock (_frames) _flashes.Add(at); }
    public void ClearFlashes() { lock (_frames) _flashes.Clear(); }
    public IReadOnlyList<FrameArrival> Frames { get { lock (_frames) return _frames.ToList(); } }
    public IReadOnlyList<double> FlashTimes { get { lock (_frames) return _flashes.ToList(); } }

    public DelayReport Report()
    {
        lock (_frames)
        {
            var r = new DelayReport();
            if (_started is { } s)
            {
                if (_playing is { } p) r.ConnectSeconds = p - s;
                if (_firstPicture is { } f) r.TimeToPicture = f - s;
            }
            if (_frames.Count >= 2) r.SecondsWatched = _frames[^1].At - _frames[0].At;

            // Transit = arrival - capture time (+ an unknown constant: the two clocks differ). Only its spread and
            // its trend matter, and both are free of the constant.
            if (_frames.Count >= 50)
            {
                var transit = _frames.Select(x => x.At - x.RtpSeconds).ToList();
                double min = transit.Min();
                var spread = transit.Select(t => (t - min) * 1000).OrderBy(x => x).ToList();
                r.BufferNeededMs = spread[(int)(spread.Count * 0.95)];
                if (r.SecondsWatched >= 40)
                {
                    double t0 = _frames[0].At;
                    double early = _frames.Where(x => x.At - t0 < 15).Min(x => x.At - x.RtpSeconds);
                    double late = _frames.Where(x => _frames[^1].At - x.At < 15).Min(x => x.At - x.RtpSeconds);
                    double minutes = (r.SecondsWatched - 15) / 60;
                    r.FallingBehindPerMinute = minutes > 0 ? Math.Max(0, (late - early) / minutes) : null;
                }
            }

            if (_flashes.Count > 0)
            {
                var delays = FlashDelays(_frames, _flashes);
                r.FlashesShown = _flashes.Count;
                r.FlashesSeen = delays.Count;
                if (delays.Count > 0)
                {
                    delays.Sort();
                    r.CameraDelay = delays[delays.Count / 2];
                    r.CameraDelayMin = delays[0];
                    r.CameraDelayMax = delays[^1];
                }
            }
            return r;
        }
    }

    /// <summary>
    /// For each flip, the first frame after it that is much bigger than the frames just before it (and not simply the
    /// camera's regular key frame). Returns the delays in seconds of the flips that were seen.
    /// </summary>
    public static List<double> FlashDelays(IReadOnlyList<FrameArrival> frames, IReadOnlyList<double> flashes)
    {
        var result = new List<double>();
        // The camera's regular key frames come on a fixed schedule; a key frame well before its turn was caused by
        // the picture changing (scene cut), which is exactly what a flip does.
        var keyTimes = frames.Where(f => f.Key).Select(f => f.At).ToList();
        var gaps = keyTimes.Zip(keyTimes.Skip(1), (a, b) => b - a).Where(g => g > 0.2).OrderBy(g => g).ToList();
        double keyInterval = gaps.Count == 0 ? double.MaxValue : gaps[gaps.Count / 2];
        bool Scheduled(FrameArrival f)
        {
            if (!f.Key) return false;
            double prev = keyTimes.LastOrDefault(t => t < f.At - 0.001, double.NegativeInfinity);
            return f.At - prev >= keyInterval * 0.8;
        }
        for (int i = 0; i < flashes.Count; i++)
        {
            double flip = flashes[i];
            double until = i + 1 < flashes.Count ? flashes[i + 1] : flip + 3;
            var before = frames.Where(f => f.At > flip - 1.5 && f.At <= flip && !f.Key).Select(f => f.Bytes).OrderBy(b => b).ToList();
            if (before.Count < 3) continue;
            double normal = before[before.Count / 2];
            foreach (var f in frames.Where(f => f.At > flip && f.At < until))
            {
                bool big = f.Bytes > Math.Max(normal * 4, 6000);
                if (big && !Scheduled(f)) { result.Add(f.At - flip); break; }
            }
        }
        return result;
    }
}
