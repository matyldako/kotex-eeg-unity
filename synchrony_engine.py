#!/usr/bin/env python3
"""
synchrony_engine.py - two raw Unicorn EEG streams in, one small synchrony stream out.

Run on the analysis PC (the Unity PC is fine). Needs unicorn_lsl_sync.py beside it.

    pip install pylsl numpy scipy python-osc
    python synchrony_engine.py --stream-a NAME1 --stream-b NAME2 --osc 127.0.0.1:7000 127.0.0.1:9000
                                                                 (TouchDesigner)  (Unity SyncReceiver.cs)

Pipeline, 25 times per second:
    aligned raw EEG (unicorn_lsl_sync)  ->  broadband filter  ->  signal-quality check
      ->  band power per band            (like Unicorn Bandpower)
      ->  narrow-band filter -> phase    (Hilbert transform)
      ->  synchrony between the two people: phase locking + envelope / band-power correlation
      ->  OSC over UDP to any number of targets (TouchDesigner, Unity SyncReceiver.cs)
      ->  LSL outlet "UnicornSynchrony" (LabRecorder logging, Unity SynchronyReceiver.cs)

OSC addresses, sent as one bundle 25 times per second (<band> = delta, theta, alpha,
beta_low, beta_mid, beta_high, gamma):
    /sync/score            headline score, 0..1
    /sync/<band>/plv       phase-locking value, 0..1
    /sync/<band>/plvc      phase locking above the chance floor, 0..1
    /sync/<band>/aec       amplitude-envelope correlation, -1..1
    /sync/<band>/power_a   mean band power of person A, uV^2   (power_b likewise)
    /sync/good_a           number of usable electrodes on person A   (good_b likewise)
A value that is not available yet (warm-up, no usable electrodes) is simply not sent.

Settings marked [BANDPOWER] are copied from g.tec's Unicorn Bandpower app
(github.com/unicorn-bi/Unicorn-Bandpower-Hybrid-Black, README). Settings marked
[MY CHOICE] are not documented by g.tec; the reasoning is given so you can change them.
"""

import argparse
import time

import numpy as np
from pylsl import StreamInfo, StreamOutlet, local_clock
from scipy.signal import butter, hilbert, iirnotch, sosfilt, sosfilt_zi, tf2sos

from unicorn_lsl_sync import FS, N_EEG, SyncedPair

# ----------------------------------------------------------------------------
# Settings
# ----------------------------------------------------------------------------

# 7 frequency bands, as in Bandpower
BANDS = {
    "delta": (1, 4), "theta": (4, 8), "alpha": (8, 12), "beta_low": (12, 16),
    "beta_mid": (16, 20), "beta_high": (20, 30), "gamma": (30, 50),
}
BAND_NAMES = list(BANDS)

# Default buffer sizes and rates. The Bandpower app uses 1 s buffers, updated 25 times per second.
BUFFER = 250
OVERLAP = 240
HOP = BUFFER - OVERLAP
OUT_RATE = FS / HOP

# Butterworth band-pass for broadband filtering, 0.5-50 Hz. 
BROADBAND = (0.5, 50.0)
BROADBAND_ORDER = 2

# Notch filter for mains hum. The app uses 50 Hz.
NOTCH_Q = 30.0   # width of about 50/30 = 1.7 Hz, narrow enough to leave gamma alone

# [BANDPOWER, thresholds MY CHOICE] The app judges each electrode by the RMS of
# its signal and reports NaN for unusable channels. g.tec does not publish the
# thresholds, so these are conservative guesses in microvolts, measured on the
# broadband-filtered 1 s buffer. Tune them against the app's green/red display.
RMS_FLAT = 0.5      # below this the electrode is flat-lined (no signal at all)
RMS_FLOATING = 100  # above this it is floating, or a blink/movement artifact is in the buffer

# [MY CHOICE] Phase needs a narrow band, so each band gets its own band-pass:
# Butterworth, order 2 (4 poles as a band-pass). Low order keeps the delay short.
# The filter's phase shift does not bias synchrony because both people pass
# through the identical filter, so it cancels in the phase difference.
NARROW_ORDER = 2

# [MY CHOICE] Window for phase locking. The Bandpower 1 s buffer is the minimum,
# but phase locking is only meaningful over several oscillation cycles: 1 s of
# delta is 1-4 cycles, which would look "locked" by chance. So each band uses at
# least 8 cycles of its lowest frequency:
#   delta 8 s, theta 2 s, alpha / beta / gamma 1 s (the Bandpower buffer).
MIN_CYCLES = 8

# [MY CHOICE] The Hilbert transform is unreliable at the ends of a window, so a
# short piece (half a cycle of the band's lowest frequency, at most 0.25 s) is
# discarded at each end. This adds that much latency to the phase measures.
MAX_TRIM = int(FS // 4)

# [MY CHOICE] Chance level. Two unrelated alpha rhythms still give a phase-locking
# value well above 0 over a short window. To estimate that floor, person A's
# current window is compared with person B's window from 10 s EARLIER: same
# rhythms, but no possible moment-to-moment coupling. 10 s is longer than the
# longest window (8 s), so the two segments never overlap.
SURROGATE_LAG = int(10 * FS)
CHANCE_TAU = 10.0   # the floor is averaged over ~10 s because a single estimate is noisy

# [MY CHOICE] Band-power correlation window. Successive Bandpower-style values
# share 240 of 250 samples, so they are heavily autocorrelated: 15 s holds 375
# values but only about 15 independent ones. Shorter windows give correlations
# that swing to +/-1 by chance.
POWER_CORR_WINDOW = int(15 * OUT_RATE)

# [MY CHOICE] Amplitude-envelope correlation (AEC) window: 10 s. An envelope
# changes slowly (its speed is set by the band's width, a few Hz), so 2-3 s holds
# only a handful of independent points and the correlation swings to +/-1 by
# chance. 10 s is the longest window that still fits the history kept for the
# chance floor, and gives roughly 40 independent points in alpha.
AEC_WINDOW = int(10 * FS)

# [MY CHOICE] Discard the first 2 s after start/dropout while filters settle.
SETTLE = int(2 * FS)

# [MY CHOICE] Smoothing of the headline score for display (time constant, seconds).
SCORE_TAU = 2.0


def band_plan():
    """Per band: (phase window, trim) in samples."""
    plan = {}
    for name, (lo, _hi) in BANDS.items():
        window = max(BUFFER, int(np.ceil(MIN_CYCLES / lo * FS)))
        trim = min(int(0.5 * FS / lo), MAX_TRIM)
        plan[name] = (window, trim)
    return plan


PLAN = band_plan()
HISTORY = max(max(w + 2 * t for w, t in PLAN.values()) + SURROGATE_LAG,   # samples kept per person
              AEC_WINDOW + 2 * MAX_TRIM)


# ----------------------------------------------------------------------------
# Filtering
# ----------------------------------------------------------------------------

class LiveFilter:
    """
    Causal IIR filter that carries its state from chunk to chunk.

    DECISION - causal (one-pass) filtering, not zero-phase filtfilt: filtfilt needs
    future samples, which a live system does not have. The state is initialised
    to the first sample's steady state so the electrode DC offset (which can be
    thousands of uV on a Unicorn) does not cause a huge start-up transient.
    """

    def __init__(self, sos):
        self.sos = sos
        self.zi = None

    def __call__(self, x):                      # x: (samples, channels)
        if self.zi is None:
            self.zi = sosfilt_zi(self.sos)[:, :, None] * x[0][None, None, :]
        y, self.zi = sosfilt(self.sos, x, axis=0, zi=self.zi)
        return y


def broadband_sos(notch_hz):
    sos = butter(BROADBAND_ORDER, BROADBAND, btype="bandpass", fs=FS, output="sos")
    if notch_hz:
        sos = np.vstack([sos, tf2sos(*iirnotch(notch_hz, NOTCH_Q, fs=FS))])
    return sos


class Person:
    """Filters and rolling buffers for one participant."""

    def __init__(self, notch_hz):
        self.broad_filter = LiveFilter(broadband_sos(notch_hz))
        self.narrow_filter = {b: LiveFilter(butter(NARROW_ORDER, rng, btype="bandpass", fs=FS, output="sos"))
                              for b, rng in BANDS.items()}
        self.broad = np.zeros((BUFFER, N_EEG))                         # last 1 s, broadband
        self.narrow = {b: np.zeros((HISTORY, N_EEG)) for b in BANDS}   # longer history per band
        self.power_log = {b: [] for b in BANDS}                        # recent log band power (for correlation)

    def push(self, raw):
        broad = self.broad_filter(raw)
        self.broad = _shift_in(self.broad, broad)
        for b in BANDS:
            # Narrow-band filters are fed the broadband output so drift and mains
            # are already gone before the phase is estimated.
            self.narrow[b] = _shift_in(self.narrow[b], self.narrow_filter[b](broad))


def _shift_in(buffer, new):
    n = len(new)
    buffer[:-n] = buffer[n:]
    buffer[-n:] = new
    return buffer


# ----------------------------------------------------------------------------
# Features
# ----------------------------------------------------------------------------

_HANN = np.hanning(BUFFER)
_FREQS = np.fft.rfftfreq(BUFFER, 1 / FS)
_PSD_SCALE = 1.0 / (FS * np.sum(_HANN ** 2))


def good_channels(broad):
    """[BANDPOWER] RMS-based signal quality per channel: True = usable."""
    rms = np.sqrt(np.mean(broad ** 2, axis=0))
    return (rms > RMS_FLAT) & (rms < RMS_FLOATING)


def band_power(broad, good):
    """
    Power in each band for each channel, in uV^2. Returns (7 bands, 8 channels).

    [MY CHOICE] g.tec does not document its estimator, so this uses the standard
    one: remove the mean, apply a Hann window (reduces leakage between
    neighbouring bands), FFT, and sum the power spectral density over the band.
    Lower edge inclusive, upper edge exclusive, so the 4 Hz bin counts once (in
    theta), not in both delta and theta. Absolute values will not equal the
    app's numbers (unknown scaling) but they move the same way.
    [BANDPOWER] Bad channels are returned as NaN, as the app does.
    """
    segment = broad - broad.mean(axis=0)
    psd = np.abs(np.fft.rfft(segment * _HANN[:, None], axis=0)) ** 2 * _PSD_SCALE
    psd[1:-1] *= 2                                    # one-sided spectrum
    df = _FREQS[1] - _FREQS[0]
    out = np.empty((len(BANDS), N_EEG))
    for i, (lo, hi) in enumerate(BANDS.values()):
        out[i] = psd[(_FREQS >= lo) & (_FREQS < hi)].sum(axis=0) * df
    out[:, ~good] = np.nan
    return out


def unit_phasors(narrow_segment, trim):
    """
    Instantaneous phase of each channel as a unit-length complex number e^(i*phase).

    The Hilbert transform turns the narrow-band signal into amplitude and phase.
    Dividing by the amplitude keeps phase only, so a person with stronger alpha
    does not dominate the synchrony value.
    """
    analytic = hilbert(narrow_segment, axis=0)[trim:-trim]
    return analytic / np.maximum(np.abs(analytic), 1e-12)


def phase_locking(ua, ub, good_a, good_b, all_pairs):
    """
    Phase-locking value (PLV) and mean phase difference between two people.

    PLV = | average over time of e^(i * (phase_A - phase_B)) |
      1 = the phase difference stays constant across the window (locked),
      0 = the phase difference wanders over all angles (unrelated).
    [MY CHOICE] PLV over alternatives such as coherence because it ignores
    amplitude, is bounded 0-1, and is the most widely used inter-brain measure.

    [MY CHOICE] Pairing. Default: the same electrode on both heads (Fz-Fz, ...,
    8 pairs), averaged. This is the easiest to interpret. --pairs all averages
    all 64 combinations instead.
    [BANDPOWER] Like the app's "average" outputs, only good channels contribute.
    """
    n = len(ua)
    if all_pairs:
        cross = ua.T @ np.conj(ub) / n                # (8, 8) mean phasor per pair
        mask = np.outer(good_a, good_b)
    else:
        cross = np.mean(ua * np.conj(ub), axis=0)     # (8,) mean phasor per electrode
        mask = good_a & good_b
    if not mask.any():
        return np.nan, np.nan
    plv = float(np.mean(np.abs(cross[mask])))
    # Mean phase difference in radians (A minus B). See the Bluetooth caveat in
    # unicorn_lsl_sync.py before interpreting its sign.
    diff = float(np.angle(np.mean(cross[mask])))
    return plv, diff


def envelope_correlation(narrow_a, narrow_b, good_a, good_b):
    """
    Amplitude-envelope correlation (AEC): do the two people's rhythms get
    stronger and weaker together? -1..1, averaged over same-electrode pairs.

    The envelope is the amplitude of the Hilbert signal, i.e. band power followed
    sample by sample instead of once per 1 s buffer. Unlike phase locking it
    does not need millisecond timing, so it tolerates Bluetooth delay and jitter.
    [MY CHOICE] No "orthogonalisation" step: that correction exists to remove
    signal shared through one skull, which cannot happen between two heads.
    """
    mask = good_a & good_b
    if not mask.any():
        return np.nan
    ea = np.abs(hilbert(narrow_a, axis=0))[MAX_TRIM:-MAX_TRIM]
    eb = np.abs(hilbert(narrow_b, axis=0))[MAX_TRIM:-MAX_TRIM]
    ea = ea - ea.mean(axis=0)
    eb = eb - eb.mean(axis=0)
    denom = np.sqrt(np.sum(ea * ea, axis=0) * np.sum(eb * eb, axis=0))
    r = np.sum(ea * eb, axis=0) / np.maximum(denom, 1e-12)
    return float(np.mean(r[mask]))


def correlation(x, y):
    """Pearson correlation of two equally long lists, ignoring NaN pairs."""
    x, y = np.asarray(x), np.asarray(y)
    ok = np.isfinite(x) & np.isfinite(y)
    if ok.sum() < POWER_CORR_WINDOW // 2:
        return np.nan
    x, y = x[ok] - x[ok].mean(), y[ok] - y[ok].mean()
    denom = np.sqrt(np.sum(x * x) * np.sum(y * y))
    return float(np.sum(x * y) / denom) if denom > 0 else np.nan


class Smoother:
    """Exponential moving average that skips NaN."""

    def __init__(self, tau):
        self.k = 1 - np.exp(-1 / (tau * OUT_RATE))
        self.value = np.nan

    def __call__(self, x):
        if np.isfinite(x):
            self.value = x if not np.isfinite(self.value) else self.value + self.k * (x - self.value)
        return self.value


# ----------------------------------------------------------------------------
# Output stream
# ----------------------------------------------------------------------------

def channel_labels():
    labels = ["score"]
    for prefix in ("plv", "plv_chance", "plv_corrected", "phase_diff", "aec", "power_corr",
                   "power_a", "power_b"):
        labels += [f"{prefix}_{b}" for b in BAND_NAMES]
    return labels + ["good_channels_a", "good_channels_b", "latency_s"]


def make_outlet(name):
    labels = channel_labels()
    # DECISION - one small stream (60 numbers at 25 Hz) instead of forwarding raw
    # EEG to Unity: Unity only needs results, and 25 Hz matches Bandpower's rate.
    info = StreamInfo(name, "Synchrony", len(labels), OUT_RATE, "float32", "unicorn_synchrony_engine")
    channels = info.desc().append_child("channels")
    for label in labels:
        channels.append_child("channel").append_child_value("label", label)
    return StreamOutlet(info)


class OscSender:
    """
    Sends each update as ONE OSC bundle to every target.

    DECISION - OSC alongside LSL: TouchDesigner reads OSC natively (OSC In CHOP)
    and Unity can read it without any package (SyncReceiver.cs), while LSL stays
    available for time-stamped recording. A bundle keeps all values of one update
    together in a single UDP packet, so a receiver never sees half an update.
    """

    def __init__(self, targets):
        from pythonosc.udp_client import SimpleUDPClient   # only needed when --osc is used
        self.clients = []
        for target in targets:
            ip, port = target.rsplit(":", 1)
            self.clients.append(SimpleUDPClient(ip, int(port)))

    def send(self, values):
        from pythonosc.osc_bundle_builder import IMMEDIATELY, OscBundleBuilder
        from pythonosc.osc_message_builder import OscMessageBuilder
        bundle = OscBundleBuilder(IMMEDIATELY)
        for address, value in values.items():
            if not np.isfinite(value):
                continue          # "not available" is sent as silence, never as NaN
            message = OscMessageBuilder(address=address)
            message.add_arg(float(value), "f")
            bundle.add_content(message.build())
        packet = bundle.build()
        for client in self.clients:
            client.send(packet)


# ----------------------------------------------------------------------------
# Main loop
# ----------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--stream-a", required=True, help="UnicornLSL stream name on laptop 1")
    ap.add_argument("--stream-b", required=True, help="UnicornLSL stream name on laptop 2")
    ap.add_argument("--notch", type=float, default=50.0, help="mains frequency in Hz, 0 = off (default 50)")
    ap.add_argument("--score-bands", default="alpha",
                    help="comma-separated bands averaged into the score (default alpha)")
    ap.add_argument("--score-mode", choices=["corrected", "raw"], default="corrected",
                    help="corrected = PLV above the chance floor, rescaled to 0-1 (default); raw = plain PLV")
    ap.add_argument("--pairs", choices=["same", "all"], default="same", help="electrode pairing (default same)")
    ap.add_argument("--out-name", default="UnicornSynchrony")
    ap.add_argument("--osc", nargs="*", default=[], metavar="IP:PORT",
                    help="OSC targets, e.g. 127.0.0.1:7000 (TouchDesigner) 127.0.0.1:9000 (Unity)")
    ap.add_argument("--channels", default="1,2,3,4,5,6,7,8",
                    help="electrodes to use: 1 Fz, 2 C3, 3 Cz, 4 C4, 5 Pz, 6 PO7, 7 Oz, 8 PO8 (default all)")
    args = ap.parse_args(argv)

    # Electrodes left out here are treated exactly like bad-contact electrodes.
    # Usually unnecessary (bad contacts are detected automatically each second),
    # but useful to restrict the analysis to one region, e.g. --channels 5,6,7,8.
    selected = np.zeros(N_EEG, dtype=bool)
    selected[[int(c) - 1 for c in args.channels.split(",")]] = True

    # [MY CHOICE] The headline score defaults to alpha (8-12 Hz): it is the
    # strongest, cleanest rhythm in scalp EEG, 1 s holds 8+ cycles of it, and it
    # is less contaminated than delta (eye/movement) or gamma (muscle). All bands
    # are still sent individually, so Unity can use any of them.
    score_bands = [b.strip() for b in args.score_bands.split(",")]
    for b in score_bands:
        if b not in BANDS:
            ap.error(f"unknown band '{b}'; choose from {', '.join(BAND_NAMES)}")

    pair = SyncedPair(args.stream_a, args.stream_b)
    print(pair.clock_report())
    outlet = make_outlet(args.out_name)
    osc = OscSender(args.osc) if args.osc else None
    print(f"LSL stream '{args.out_name}' is live ({len(channel_labels())} channels, {OUT_RATE:g} Hz). Ctrl+C to stop.")

    def fresh():
        return (Person(args.notch), Person(args.notch), 0,
                {b: Smoother(CHANCE_TAU) for b in BANDS}, Smoother(SCORE_TAU))

    pa, pb, filled, chance, score_smooth = fresh()
    wait_t = np.empty(0)
    wait_a = np.empty((0, N_EEG))
    wait_b = np.empty((0, N_EEG))
    last_print = last_clock = local_clock()

    try:
        while True:
            t, xa, xb, reset = pair.read()
            if reset:
                print("dropout in a stream - restarting filters and buffers")
                pa, pb, filled, chance, score_smooth = fresh()
                wait_t, wait_a, wait_b = np.empty(0), np.empty((0, N_EEG)), np.empty((0, N_EEG))
                continue
            if len(t):
                wait_t = np.concatenate([wait_t, t])
                wait_a = np.vstack([wait_a, xa])
                wait_b = np.vstack([wait_b, xb])

            # Process in exact 10-sample steps so the output is a true 25 Hz series
            # in data time, even though samples arrive from the network in bursts.
            while len(wait_t) >= HOP:
                stamp = wait_t[HOP - 1]             # time of the newest sample in this step
                pa.push(wait_a[:HOP])
                pb.push(wait_b[:HOP])
                wait_t, wait_a, wait_b = wait_t[HOP:], wait_a[HOP:], wait_b[HOP:]
                filled += HOP
                usable = filled - SETTLE            # samples since the filters settled
                if usable < BUFFER:
                    continue

                good_a = good_channels(pa.broad) & selected
                good_b = good_channels(pb.broad) & selected
                power_a, power_b = band_power(pa.broad, good_a), band_power(pb.broad, good_b)

                plv, plv_chance, plv_corrected, phase_diff = [], [], [], []
                aec, power_corr, avg_a, avg_b = [], [], [], []
                for i, b in enumerate(BAND_NAMES):
                    window, trim = PLAN[b]
                    need = window + 2 * trim

                    # --- phase synchrony -------------------------------------
                    value = floor = diff = np.nan
                    if usable >= need:
                        ua = unit_phasors(pa.narrow[b][-need:], trim)
                        ub = unit_phasors(pb.narrow[b][-need:], trim)
                        value, diff = phase_locking(ua, ub, good_a, good_b, args.pairs == "all")
                        if usable >= need + SURROGATE_LAG:
                            past = pb.narrow[b][-need - SURROGATE_LAG:-SURROGATE_LAG]
                            floor, _ = phase_locking(ua, unit_phasors(past, trim), good_a, good_b,
                                                     args.pairs == "all")
                    plv.append(value)
                    plv_chance.append(chance[b](floor))
                    # How far the PLV sits above the chance floor, as a fraction
                    # of the room available: 0 = at chance, 1 = perfect locking.
                    # NaN until the floor exists (about 10 s after start).
                    plv_corrected.append(np.clip((value - plv_chance[-1]) / (1 - plv_chance[-1]), 0, 1))
                    phase_diff.append(diff)

                    # --- amplitude-envelope correlation ----------------------
                    need = AEC_WINDOW + 2 * MAX_TRIM
                    aec.append(envelope_correlation(pa.narrow[b][-need:], pb.narrow[b][-need:], good_a, good_b)
                               if usable >= need else np.nan)

                    # --- band power and its correlation ----------------------
                    # [BANDPOWER] Average over the good channels, like the app's
                    # "channel 1-8 averaged" outputs (values 57-63 of its payload).
                    mean_a = np.nanmean(power_a[i]) if good_a.any() else np.nan
                    mean_b = np.nanmean(power_b[i]) if good_b.any() else np.nan
                    avg_a.append(mean_a)
                    avg_b.append(mean_b)
                    # [MY CHOICE] Correlate log power: raw power is strongly
                    # skewed, so a single burst would dominate the correlation.
                    for person, mean in ((pa, mean_a), (pb, mean_b)):
                        log = person.power_log[b]
                        log.append(np.log10(mean) if mean > 0 else np.nan)
                        del log[:-POWER_CORR_WINDOW]
                    power_corr.append(correlation(pa.power_log[b], pb.power_log[b]))

                # --- headline score ------------------------------------------
                source = plv if args.score_mode == "raw" else plv_corrected
                parts = [source[BAND_NAMES.index(b)] for b in score_bands]
                score = score_smooth(np.mean(parts)) if np.all(np.isfinite(parts)) else score_smooth.value

                sample = ([score] + plv + plv_chance + plv_corrected + phase_diff + aec + power_corr
                          + avg_a + avg_b + [good_a.sum(), good_b.sum(), local_clock() - stamp])
                # Stamped with the time of the EEG it describes (this PC's LSL
                # clock), not the time it was computed, so Unity can line it up
                # with its own events via LSL.
                outlet.push_sample([float(v) for v in sample], stamp)

                if osc:
                    values = {"/sync/score": score, "/sync/good_a": good_a.sum(), "/sync/good_b": good_b.sum()}
                    for i, b in enumerate(BAND_NAMES):
                        values[f"/sync/{b}/plv"] = plv[i]
                        values[f"/sync/{b}/plvc"] = plv_corrected[i]
                        values[f"/sync/{b}/aec"] = aec[i]
                        values[f"/sync/{b}/power_a"] = avg_a[i]
                        values[f"/sync/{b}/power_b"] = avg_b[i]
                    osc.send(values)

                now = local_clock()
                if now - last_print > 2.0:
                    i = BAND_NAMES.index(score_bands[0])
                    print(f"score {score:5.2f} | {score_bands[0]} plv {plv[i]:.2f} chance {plv_chance[i]:.2f} "
                          f"aec {aec[i]:+.2f} | good ch {good_a.sum()}/{good_b.sum()} "
                          f"| lag {(now - stamp) * 1000:.0f} ms")
                    last_print = now
                if now - last_clock > 30.0:
                    print(pair.clock_report())
                    last_clock = now

            time.sleep(0.005)
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
