// SyncReceiver.cs - receives per-band EEG synchrony from synchrony_engine.py over OSC/UDP.
// No extra packages needed. Add to any GameObject.
//
//   command:  python synchrony_engine.py --stream-a UnicornA --stream-b UnicornB --osc 127.0.0.1:7000 127.0.0.1:9000
//   or:       python sync_hub.py UnicornA UnicornB 127.0.0.1:7000 127.0.0.1:9000
//                                                  (TouchDesigner)  (this script)
//
// From game code:
//   var sync = FindObjectOfType<SyncReceiver>();
//   float alpha = sync.Get("alpha");     // one band, mapped to 0..1 and smoothed
//   float all   = sync.Combined;         // weighted mix of all bands, 0..1
//   float score = sync.Score;            // the engine's own headline score, 0..1
//   float plv   = sync.GetPlv("gamma");  // raw phase-locking value as sent, 0..1
//   float aec   = sync.GetAec("gamma");  // raw envelope correlation as sent, -1..1
//
// On screen (toggle "Show Overlay"): PLV and AEC for alpha, gamma and theta,
// exactly as the engine sends them. The overlay does not depend on the Measure,
// weights or calibration below; those only shape Get() and Combined.
//
// Band names are the engine's (Unicorn Bandpower's): delta, theta, alpha,
// beta_low, beta_mid, beta_high, gamma.
// If this component was already in a scene, right-click it > Reset once so the
// Bands list picks up the new names (the old "beta" no longer exists).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Events;

public class SyncReceiver : MonoBehaviour
{
    // EnvelopeCorrelation      .../aec   do the rhythms grow and fade together? Robust to Bluetooth timing.
    // PhaseLocking             .../plv   raw phase locking. Never reaches 0 even for unrelated people.
    // PhaseLockingAboveChance  .../plvc  phase locking above the engine's chance floor. Already an
    //                                    absolute 0..1 scale, so it is NOT calibrated: 0 means
    //                                    "no more locked than chance", whoever the two people are.
    public enum Measure { EnvelopeCorrelation, PhaseLocking, PhaseLockingAboveChance }

    [Serializable]
    public class Band
    {
        [Tooltip("Must match an engine band: delta, theta, alpha, beta_low, beta_mid, beta_high, gamma.")]
        public string name;
        [Tooltip("How much this band counts towards Combined. 0 = ignore.")]
        public float weight = 1f;
        [Tooltip("Raw value that counts as 0. Set by calibration, or by hand.")]
        public float low = 0f;
        [Tooltip("Raw value that counts as 1. Set by calibration, or by hand.")]
        public float high = 0.5f;
        [Tooltip("Live output for this band, 0..1 (read-only, shown for debugging).")]
        [Range(0f, 1f)] public float value01;

        [NonSerialized] public volatile float plv;
        [NonSerialized] public volatile float aec;
        [NonSerialized] public volatile float plvc;
        [NonSerialized] public List<float> calSamples = new List<float>();

        public Band(string name, float weight) { this.name = name; this.weight = weight; }
    }

    [Header("Network")]
    [Tooltip("UDP port to listen on. Must match the port given to sync_hub.py for Unity.")]
    public int port = 9000;

    [Header("Signal")]
    [Tooltip("Which of the hub's two measures drives the game.")]
    public Measure measure = Measure.EnvelopeCorrelation;
    [Tooltip("Seconds of extra smoothing on top of the hub's. 0 = none.")]
    public float smoothTime = 0.5f;
    [Tooltip("If no packet arrives for this long, HasSignal turns false and values fade to 0.")]
    public float timeoutSeconds = 2f;

    [Header("Bands")]
    public Band[] bands =
    {
        // delta and gamma start at weight 0: delta is the band most polluted by eye and
        // head movement, gamma by muscle activity. Both are still received and readable.
        new Band("delta", 0f),
        new Band("theta", 1f),
        new Band("alpha", 1f),
        new Band("beta_low", 1f),
        new Band("beta_mid", 1f),
        new Band("beta_high", 1f),
        new Band("gamma", 0f),
    };

    [Header("Calibration")]
    [Tooltip("Start calibrating automatically as soon as the first packet arrives.")]
    public bool calibrateOnStart = true;
    [Tooltip("How long to observe the players before fixing each band's low/high range.")]
    public float calibrationSeconds = 60f;

    [Header("On-screen display")]
    [Tooltip("Draw PLV and AEC for the bands below in the top-left corner of the Game view.")]
    public bool showOverlay = true;
    [Tooltip("Bands shown in the overlay, in this order.")]
    public string[] displayBands = { "alpha", "gamma", "theta" };

    [Header("Output")]
    [Tooltip("Fires every frame with the Combined value while a signal is present.")]
    public UnityEvent<float> onCombinedChanged;

    /// <summary>Weighted average of all bands' calibrated values, 0..1.</summary>
    public float Combined { get; private set; }
    /// <summary>The engine's own headline score (/sync/score), 0..1, as sent. 0 until it arrives.</summary>
    public float Score => _score;
    public bool HasSignal { get; private set; }
    public bool IsCalibrating { get; private set; }
    /// <summary>0..1 progress of the running calibration.</summary>
    public float CalibrationProgress =>
        IsCalibrating ? Mathf.Clamp01((Time.time - _calStart) / calibrationSeconds) : 1f;

    /// <summary>Calibrated, smoothed synchrony for one band, 0..1. Returns 0 for unknown names.</summary>
    public float Get(string bandName)
    {
        Band b = Find(bandName);
        return b != null ? b.value01 : 0f;
    }

    /// <summary>Phase-locking value for any engine band, exactly as sent (0..1). 0 until it arrives.</summary>
    public float GetPlv(string bandName) => _raw.TryGetValue(bandName + "/plv", out float v) ? v : 0f;

    /// <summary>Amplitude-envelope correlation for any engine band, exactly as sent (-1..1). 0 until it arrives.</summary>
    public float GetAec(string bandName) => _raw.TryGetValue(bandName + "/aec", out float v) ? v : 0f;

    /// <summary>Latest unmapped value for one band, exactly as the hub sent it.</summary>
    public float GetRaw(string bandName)
    {
        Band b = Find(bandName);
        return b != null ? RawOf(b) : 0f;
    }

    UdpClient _udp;
    Thread _thread;
    volatile bool _running;
    long _lastPacketTicks;                 // written by the network thread
    bool _everCalibrated;
    float _calStart;
    volatile float _score;
    // Every /sync/<band>/<measure> value as received, keyed "band/measure". Filled by the
    // network thread, so the overlay and GetPlv/GetAec work for any band the engine sends,
    // whether or not it is in the Bands list above.
    readonly ConcurrentDictionary<string, float> _raw = new ConcurrentDictionary<string, float>();

    // The above-chance measure has a fixed meaning, so stretching it to the
    // range seen in the first minute would throw that meaning away.
    bool UsesCalibration => measure != Measure.PhaseLockingAboveChance;

    float RawOf(Band b)
    {
        switch (measure)
        {
            case Measure.EnvelopeCorrelation: return b.aec;
            case Measure.PhaseLocking: return b.plv;
            default: return b.plvc;
        }
    }

    Band Find(string bandName)
    {
        Band[] list = bands;
        for (int i = 0; i < list.Length; i++)
            if (list[i].name == bandName) return list[i];
        return null;
    }

    void OnEnable()
    {
        try
        {
            _udp = new UdpClient(port);
        }
        catch (Exception e)
        {
            Debug.LogError($"SyncReceiver: cannot open UDP port {port} ({e.Message}). " +
                           "Is another program already using it?");
            return;
        }
        _running = true;
        _thread = new Thread(Listen) { IsBackground = true, Name = "SyncReceiver" };
        _thread.Start();
    }

    void OnDisable()
    {
        _running = false;
        _udp?.Close();                     // unblocks Receive() in the thread
        _udp = null;
        _thread = null;
    }

    [ContextMenu("Start Calibration")]
    public void StartCalibration()
    {
        foreach (Band b in bands)
        {
            if (b.calSamples == null) b.calSamples = new List<float>();
            b.calSamples.Clear();
        }
        _calStart = Time.time;
        IsCalibrating = true;
    }

    void Update()
    {
        long last = Interlocked.Read(ref _lastPacketTicks);
        HasSignal = last != 0 &&
                    (DateTime.UtcNow.Ticks - last) < (long)(timeoutSeconds * TimeSpan.TicksPerSecond);

        if (HasSignal && UsesCalibration && calibrateOnStart && !_everCalibrated && !IsCalibrating)
        {
            _everCalibrated = true;
            StartCalibration();
        }

        if (IsCalibrating)
        {
            if (HasSignal)
                foreach (Band b in bands) b.calSamples.Add(RawOf(b));
            if (Time.time - _calStart >= calibrationSeconds) FinishCalibration();
        }

        float k = smoothTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / smoothTime);
        float sum = 0f, weightSum = 0f;
        foreach (Band b in bands)
        {
            float mapped = UsesCalibration ? Mathf.InverseLerp(b.low, b.high, RawOf(b))
                                           : Mathf.Clamp01(RawOf(b));
            float target = HasSignal ? mapped : 0f;
            b.value01 = Mathf.Lerp(b.value01, target, k);
            if (b.weight > 0f)
            {
                sum += b.value01 * b.weight;
                weightSum += b.weight;
            }
        }
        Combined = weightSum > 0f ? sum / weightSum : 0f;
        if (HasSignal) onCombinedChanged?.Invoke(Combined);
    }

    // ---------- on-screen display ----------

    void OnGUI()
    {
        if (!showOverlay || displayBands == null) return;
        const float width = 340f, row = 24f;
        float height = 40f + displayBands.Length * 2 * row + 6f;

        GUI.color = new Color(0f, 0f, 0f, 0.7f);
        GUI.DrawTexture(new Rect(12f, 12f, width, height), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(22f, 18f, width, 22f), HasSignal ? "EEG synchrony" : "EEG synchrony - no signal");

        float y = 46f;
        foreach (string band in displayBands)
        {
            DrawRow(y, band + " PLV", band + "/plv", 0f, 1f);     // 0 = unrelated, 1 = locked
            y += row;
            DrawRow(y, band + " AEC", band + "/aec", -1f, 1f);    // bar grows left or right of the centre
            y += row;
        }
    }

    void DrawRow(float y, string label, string key, float min, float max)
    {
        bool has = _raw.TryGetValue(key, out float value);
        GUI.color = Color.white;
        GUI.Label(new Rect(22f, y, 110f, 22f), label);
        // "--" until the engine has sent this value (AEC needs about 12 s of data)
        GUI.Label(new Rect(134f, y, 52f, 22f), has ? value.ToString(min < 0f ? "+0.00;-0.00" : "0.00") : "--");

        var track = new Rect(190f, y + 7f, 148f, 8f);
        GUI.color = new Color(1f, 1f, 1f, 0.2f);
        GUI.DrawTexture(track, Texture2D.whiteTexture);
        if (!has) return;
        float zero = Mathf.InverseLerp(min, max, 0f);
        float at = Mathf.InverseLerp(min, max, value);
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(track.x + track.width * Mathf.Min(zero, at), track.y,
                                 track.width * Mathf.Abs(at - zero), track.height), Texture2D.whiteTexture);
    }

    void FinishCalibration()
    {
        IsCalibrating = false;
        foreach (Band b in bands)
        {
            List<float> s = b.calSamples;
            if (s.Count < 20)
            {
                Debug.LogWarning($"SyncReceiver: too little calibration data for {b.name}; keeping old range.");
                continue;
            }
            s.Sort();
            // 10th..90th percentile of what this pair actually produced becomes 0..1
            float lo = s[(int)(s.Count * 0.10f)];
            float hi = s[(int)(s.Count * 0.90f)];
            if (hi - lo < 0.01f)
            {
                Debug.LogWarning($"SyncReceiver: {b.name} barely moved during calibration " +
                                 "(is the hub sending this band?); keeping old range.");
                continue;
            }
            b.low = lo;
            b.high = hi;
            Debug.Log($"SyncReceiver: calibrated {b.name}: low={lo:F3} high={hi:F3}");
        }
    }

    // ---------- network thread ----------

    void Listen()
    {
        var from = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                byte[] packet = _udp.Receive(ref from);
                ParseOsc(packet, 0, packet.Length);
            }
            catch (SocketException) { if (!_running) break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception e) { Debug.LogWarning("SyncReceiver: bad packet: " + e.Message); }
        }
    }

    // Minimal OSC reader: plain messages and bundles, first argument float/double/int.
    void ParseOsc(byte[] b, int start, int end)
    {
        if (end - start >= 16 && b[start] == (byte)'#')          // "#bundle"
        {
            int i = start + 16;                                   // skip "#bundle\0" + timetag
            while (i + 4 <= end)
            {
                int size = (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
                i += 4;
                if (size <= 0 || i + size > end) return;
                ParseOsc(b, i, i + size);
                i += size;
            }
            return;
        }

        int pos = start;
        string address = ReadString(b, ref pos, end);
        string tags = ReadString(b, ref pos, end);
        if (tags.Length < 2 || tags[0] != ',') return;

        float value;
        switch (tags[1])
        {
            case 'f':
                if (pos + 4 > end) return;
                value = BitConverter.ToSingle(BigEndian(b, pos, 4), 0);
                break;
            case 'd':
                if (pos + 8 > end) return;
                value = (float)BitConverter.ToDouble(BigEndian(b, pos, 8), 0);
                break;
            case 'i':
                if (pos + 4 > end) return;
                value = BitConverter.ToInt32(BigEndian(b, pos, 4), 0);
                break;
            default:
                return;
        }
        if (float.IsNaN(value) || float.IsInfinity(value)) return;

        // expected addresses: /sync/score and /sync/<band>/<plv|plvc|aec>
        // (the engine also sends power and electrode counts, which are ignored here)
        string[] parts = address.Split('/');
        if (parts.Length < 3 || parts[1] != "sync") return;
        if (parts.Length == 3)
        {
            if (parts[2] != "score") return;
            _score = value;
        }
        else
        {
            if (parts.Length != 4) return;
            _raw[parts[2] + "/" + parts[3]] = value;
            Band band = Find(parts[2]);
            if (band != null)
            {
                if (parts[3] == "plv") band.plv = value;
                else if (parts[3] == "plvc") band.plvc = value;
                else if (parts[3] == "aec") band.aec = value;
            }
        }
        Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
    }

    static string ReadString(byte[] b, ref int pos, int end)
    {
        int z = pos;
        while (z < end && b[z] != 0) z++;
        string s = Encoding.ASCII.GetString(b, pos, z - pos);
        pos = (z + 4) & ~3;                // OSC strings are null-padded to a multiple of 4
        return s;
    }

    static byte[] BigEndian(byte[] b, int pos, int n)
    {
        var tmp = new byte[n];
        Buffer.BlockCopy(b, pos, tmp, 0, n);
        if (BitConverter.IsLittleEndian) Array.Reverse(tmp);
        return tmp;
    }
}
