// Reads the "UnicornSynchrony" LSL stream published by synchrony_engine.py.
// Requires LSL4Unity (https://github.com/labstreaminglayer/LSL4Unity).
// Attach to any GameObject, then read `score` or call Get("plv_alpha") etc.
//
// OPTIONAL. SyncReceiver.cs (OSC, no packages, weights and calibration) is the
// normal way to drive a game. Use this one instead only when Unity needs
// everything the engine computes, or the LSL timestamp of each value
// (`lastTimestamp`), e.g. to line game events up with a LabRecorder file.
// Both can run in the same scene: they use different transports.
//
// Channel labels sent by the engine (74 in total):
//   score
//   plv_<band>            phase-locking value, 0..1
//   plv_chance_<band>     chance floor for that PLV
//   plv_corrected_<band>  PLV above the chance floor, 0..1
//   pli_<band>            control: phase lag index, 0..1
//   ccorr_<band>          control: circular correlation, -1..1
//   phase_diff_<band>     mean phase difference A-B, radians
//   aec_<band>            amplitude-envelope correlation, -1..1
//   power_corr_<band>     band-power correlation, -1..1
//   power_a_<band>, power_b_<band>   mean band power per person, uV^2
//   good_channels_a, good_channels_b, latency_s
// <band> is delta, theta, alpha, beta_low, beta_mid, beta_high or gamma.

using System.Collections.Generic;
using UnityEngine;
using LSL;

public class SynchronyReceiver : MonoBehaviour
{
    public string streamName = "UnicornSynchrony";

    [Range(0f, 1f)] public float score;      // headline synchrony, 0..1
    public bool connected;
    public double lastTimestamp;             // LSL time of the EEG the latest values describe

    StreamInlet inlet;
    float[] sample;
    float[] latest;
    readonly Dictionary<string, int> index = new Dictionary<string, int>();

    void Update()
    {
        if (inlet == null && !TryConnect()) return;

        // Take everything that arrived since the last frame; keep the newest.
        double ts;
        while ((ts = inlet.pull_sample(sample, 0.0)) != 0.0)
        {
            lastTimestamp = ts;
            for (int i = 0; i < sample.Length; i++)
                // The engine sends NaN while a measure is warming up or has no
                // usable channels. Keep the previous value so NaN never reaches
                // transforms, shaders or audio.
                if (!float.IsNaN(sample[i])) latest[i] = sample[i];
        }
        score = Get("score");
    }

    /// Latest value of a channel by label, e.g. Get("plv_alpha"). Returns 0 if unknown.
    public float Get(string label)
    {
        return latest != null && index.TryGetValue(label, out int i) ? latest[i] : 0f;
    }

    bool TryConnect()
    {
        // Timeout 0 = do not block the frame; retry each Update until the stream exists.
        StreamInfo[] found = LSL.LSL.resolve_stream("name", streamName, 1, 0.0);
        if (found.Length == 0) return false;

        inlet = new StreamInlet(found[0], 10);
        StreamInfo info = inlet.info();      // full info, including the channel labels
        int n = info.channel_count();
        sample = new float[n];
        latest = new float[n];

        XMLElement ch = info.desc().child("channels").child("channel");
        for (int i = 0; i < n && !ch.empty(); i++, ch = ch.next_sibling())
            index[ch.child_value("label")] = i;

        connected = true;
        Debug.Log($"Connected to {streamName}: {n} channels, {index.Count} labels");
        return true;
    }

    void OnDestroy()
    {
        inlet?.close_stream();
    }
}
