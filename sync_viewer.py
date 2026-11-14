#!/usr/bin/env python3
"""
sync_viewer.py - live scientific-style figure of two-person EEG synchrony.

Needs synchrony_engine.py and unicorn_lsl_sync.py in the same folder, and
sync_hub.py (or synchrony_engine.py) already running for the lower two panels.

    pip install matplotlib
    python sync_viewer.py UnicornA UnicornB
    python sync_viewer.py UnicornA UnicornB --band theta --electrode 5

Press S in the window to save the current figure as a PNG.
Headless:  --snapshot figure.png --after 60   (save after 60 s, then exit)

The six panels, and what each one shows:

  A  Filtered traces      Both people's EEG, band-pass filtered to one rhythm, same
                          electrode, on one time axis. When locked, peaks keep a
                          fixed spacing; when not, they slide past each other.
  B  Phase difference     Where person A's rhythm sits relative to B's at every
                          sample of the analysis window, as a circular histogram.
                          Locked = bars pile up in one direction. Unrelated = bars
                          spread evenly round the circle (the dashed ring). The
                          arrow is the mean direction; its length is the PLV.
  C  PLV over time        The phase-locking value from the engine against its
                          chance floor (grey). Only PLV ABOVE the grey area is
                          evidence of synchrony.
  D  PLV by band          The same comparison for all seven bands at once.
  E  Amplitude envelopes  How strong each person's rhythm is over the last 10 s
                          (Hilbert envelope, same electrode). AEC asks whether
                          these two curves rise and fall together; r is their
                          correlation for this electrode.
  F  AEC by band          Envelope correlation for all seven bands, -1..1.

Panels A, B and E are computed here from the raw streams, with the engine's own
filters, for ONE electrode. Panels C, D and F show exactly the numbers the
engine sends to TouchDesigner and Unity (all usable electrodes averaged).

Under panel B: the two control measures (phase lag index, circular correlation)
for the same window. In the header: an exploratory Granger-causality estimate,
shown here only and never sent on (see granger_pair in synchrony_engine.py).
"""

import argparse
import time
import warnings
from collections import deque

import numpy as np

from scipy.signal import hilbert

from synchrony_engine import (AEC_WINDOW, BAND_NAMES, BANDS, GRANGER_WINDOW, MAX_TRIM, PLAN, SETTLE, Person,
                              good_channels, granger_pair, phase_controls, unit_phasors)
from unicorn_lsl_sync import FS, SyncedPair

ELECTRODES = ["Fz", "C3", "Cz", "C4", "Pz", "PO7", "Oz", "PO8"]

# Colour carries identity for the two PEOPLE only. Statistics are drawn in ink
# and chance in grey, so no colour means two different things.
COLOR_A, COLOR_B = "#2a78d6", "#eb6834"
INK, MUTED, GRID, CHANCE = "#2b2b2a", "#6e6d66", "#e4e3dd", "#c9c8c0"
N_BINS = 24                      # 15-degree bins on the phase circle


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("stream_a")
    ap.add_argument("stream_b")
    ap.add_argument("--band", default="alpha", choices=BAND_NAMES)
    ap.add_argument("--electrode", type=int, default=7,
                    help="electrode for panels A and B: 1 Fz, 2 C3, 3 Cz, 4 C4, 5 Pz, 6 PO7, 7 Oz, 8 PO8 "
                         "(default 7 = Oz, where alpha is strongest)")
    ap.add_argument("--seconds", type=float, default=2.0, help="length of the trace panel (default 2 s)")
    ap.add_argument("--history", type=float, default=120.0, help="length of the PLV timeline (default 120 s)")
    ap.add_argument("--sync-stream", default="UnicornSynchrony", help="the engine's LSL stream name")
    ap.add_argument("--notch", type=float, default=50.0)
    ap.add_argument("--snapshot", help="save a PNG to this path and exit (no window)")
    ap.add_argument("--after", type=float, default=60.0, help="seconds to run before --snapshot (default 60)")
    args = ap.parse_args()

    import matplotlib
    if args.snapshot:
        matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from matplotlib import patheffects
    from matplotlib.animation import FuncAnimation
    from pylsl import StreamInlet, local_clock, proc_clocksync, resolve_byprop

    band, el = args.band, args.electrode - 1
    lo, hi = BANDS[band]
    window, trim = PLAN[band]
    need = window + 2 * trim
    n_trace = int(args.seconds * FS)

    pair = SyncedPair(args.stream_a, args.stream_b)
    pa, pb = Person(args.notch), Person(args.notch)
    state = {"filled": 0, "inlet": None, "labels": {}, "next_try": 0.0, "next_granger": 0.0}
    need_env = AEC_WINDOW + 2 * MAX_TRIM            # same window the engine uses for AEC
    history = deque(maxlen=int(args.history * 25))     # (timestamp, engine sample)

    # ------------------------------------------------------------ figure ----
    plt.rcParams.update({
        "font.size": 9, "font.family": "DejaVu Sans", "axes.edgecolor": MUTED, "axes.labelcolor": INK,
        "xtick.color": MUTED, "ytick.color": MUTED, "text.color": INK, "axes.linewidth": 0.8,
        "axes.spines.top": False, "axes.spines.right": False, "figure.facecolor": "white",
    })
    fig = plt.figure(figsize=(12, 10.5))
    grid = fig.add_gridspec(3, 3, hspace=0.62, wspace=0.35, left=0.07, right=0.97, top=0.885, bottom=0.06)
    ax_trace = fig.add_subplot(grid[0, :2])
    ax_phase = fig.add_subplot(grid[0, 2], projection="polar")
    ax_time = fig.add_subplot(grid[1, :2])
    ax_bands = fig.add_subplot(grid[1, 2])
    ax_env = fig.add_subplot(grid[2, :2])
    ax_aec = fig.add_subplot(grid[2, 2])
    fig.suptitle(f"Inter-brain synchrony: {args.stream_a} (A) and {args.stream_b} (B)",
                 x=0.07, ha="left", fontsize=13, fontweight="bold")
    status = fig.text(0.07, 0.945, "", fontsize=9, color=MUTED)
    explore = fig.text(0.07, 0.925, "", fontsize=9, color=MUTED)

    def panel_title(ax, letter, text):
        ax.set_title(f"{letter}   {text}", loc="left", fontsize=10, fontweight="bold", pad=10)

    # A - filtered traces
    panel_title(ax_trace, "A", f"{band} rhythm ({lo}-{hi} Hz) at {ELECTRODES[el]}, both people")
    t_trace = (np.arange(n_trace) - n_trace) / FS
    line_a, = ax_trace.plot(t_trace, np.zeros(n_trace), color=COLOR_A, lw=1.6, label="Person A")
    line_b, = ax_trace.plot(t_trace, np.zeros(n_trace), color=COLOR_B, lw=1.6, label="Person B")
    ax_trace.set_xlim(-args.seconds, 0)
    ax_trace.set_xlabel("time before now (s)")
    ax_trace.set_ylabel("amplitude (µV)")
    ax_trace.axhline(0, color=GRID, lw=0.8, zorder=0)
    ax_trace.legend(loc="upper left", ncol=2, frameon=False, bbox_to_anchor=(0.0, 1.02))

    # B - phase difference histogram
    panel_title(ax_phase, "B", "phase difference A − B")
    edges = np.linspace(-np.pi, np.pi, N_BINS + 1)
    centers = (edges[:-1] + edges[1:]) / 2
    bars = ax_phase.bar(centers, np.zeros(N_BINS), width=2 * np.pi / N_BINS * 0.9, color=MUTED,
                        edgecolor="white", linewidth=0.6)
    uniform = 1.0 / N_BINS
    ring, = ax_phase.plot(np.linspace(0, 2 * np.pi, 200), np.full(200, uniform), color=INK, lw=0.9, ls="--")
    arrow, = ax_phase.plot([0, 0], [0, 0], color=INK, lw=2.4, solid_capstyle="round")
    ax_phase.set_yticklabels([])
    ax_phase.set_xticks(np.deg2rad([0, 90, 180, 270]))
    ax_phase.set_xticklabels(["0°\nin phase", "90°", "180°\nopposite", "−90°"])
    ax_phase.tick_params(axis="x", pad=10)          # keep the angle labels clear of the circle
    ax_phase.grid(color=GRID, lw=0.6)
    ax_phase.spines["polar"].set_color(GRID)
    phase_note = ax_phase.text(0.5, -0.40, "", transform=ax_phase.transAxes, ha="center", fontsize=9)

    # C - PLV over time
    panel_title(ax_time, "C", f"{band} phase-locking value over time (all usable electrodes)")
    chance_fill = [ax_time.fill_between([0], [0], [0], color=CHANCE)]
    line_chance, = ax_time.plot([], [], color=MUTED, lw=1.0, label="chance floor")
    line_plv, = ax_time.plot([], [], color=INK, lw=1.8, label="PLV")
    ax_time.set_xlim(-args.history, 0)
    ax_time.set_ylim(0, 1)
    ax_time.set_xlabel("time before now (s)")
    ax_time.set_ylabel("PLV (0 = unrelated, 1 = locked)")
    ax_time.grid(axis="y", color=GRID, lw=0.6)
    ax_time.legend(loc="upper left", ncol=2, frameon=False, bbox_to_anchor=(0.0, 1.02))
    time_note = ax_time.text(0.5, 0.5, f"waiting for LSL stream '{args.sync_stream}'\n(start sync_hub.py)",
                             transform=ax_time.transAxes, ha="center", va="center", color=MUTED)

    # D - PLV by band
    panel_title(ax_bands, "D", "PLV by band, last 2 s")
    xs = np.arange(len(BAND_NAMES))
    band_bars = ax_bands.bar(xs, np.zeros(len(xs)), width=0.55, color=INK)
    band_bars[BAND_NAMES.index(band)].set_color(INK)
    # white outline so the chance tick stays readable where it crosses a bar
    outline = [patheffects.Stroke(linewidth=4.5, foreground="white"), patheffects.Normal()]
    chance_marks = [ax_bands.plot([x - 0.38, x + 0.38], [0, 0], color=MUTED, lw=2.0, path_effects=outline)[0]
                    for x in xs]
    chance_marks[0].set_label("chance floor")
    ax_bands.set_xticks(xs)
    ax_bands.set_xticklabels([b.replace("_", "\n") for b in BAND_NAMES], fontsize=8)
    ax_bands.set_ylim(0, 1)
    ax_bands.grid(axis="y", color=GRID, lw=0.6)
    ax_bands.set_axisbelow(True)
    ax_bands.legend(loc="upper right", frameon=False)

    # E - amplitude envelopes (what AEC correlates)
    panel_title(ax_env, "E", f"{band} amplitude envelope at {ELECTRODES[el]}, both people (Hilbert)")
    t_env = (np.arange(AEC_WINDOW) - AEC_WINDOW - MAX_TRIM) / FS
    env_a, = ax_env.plot(t_env, np.full(AEC_WINDOW, np.nan), color=COLOR_A, lw=1.6, label="Person A")
    env_b, = ax_env.plot(t_env, np.full(AEC_WINDOW, np.nan), color=COLOR_B, lw=1.6, label="Person B")
    ax_env.set_xlim(t_env[0], 0)
    ax_env.set_ylim(0, 1)
    ax_env.set_xlabel("time before now (s)")
    ax_env.set_ylabel("envelope (µV)")
    ax_env.grid(axis="y", color=GRID, lw=0.6)
    ax_env.legend(loc="upper left", ncol=2, frameon=False, bbox_to_anchor=(0.0, 1.02))
    env_note = ax_env.text(0.995, 0.97, "", transform=ax_env.transAxes, ha="right", va="top", fontsize=9)

    # F - AEC by band
    panel_title(ax_aec, "F", "AEC by band, last 2 s")
    aec_bars = ax_aec.bar(xs, np.zeros(len(xs)), width=0.55, color=INK)
    ax_aec.axhline(0, color=MUTED, lw=0.8)
    ax_aec.set_xticks(xs)
    ax_aec.set_xticklabels([b.replace("_", "\n") for b in BAND_NAMES], fontsize=8)
    ax_aec.set_ylim(-1, 1)
    ax_aec.set_ylabel("correlation")
    ax_aec.grid(axis="y", color=GRID, lw=0.6)
    ax_aec.set_axisbelow(True)

    # ------------------------------------------------------------ update ----
    def connect_engine():
        """Look for the engine's output stream without blocking the display for long."""
        if state["inlet"] is not None or time.time() < state["next_try"]:
            return
        state["next_try"] = time.time() + 3
        found = resolve_byprop("name", args.sync_stream, timeout=0.3)
        if not found:
            return
        inlet = StreamInlet(found[0], max_buflen=int(args.history), processing_flags=proc_clocksync)
        channel = inlet.info().desc().child("channels").child("channel")
        index = 0
        while not channel.empty():
            state["labels"][channel.child_value("label")] = index
            channel = channel.next_sibling()
            index += 1
        state["inlet"] = inlet
        time_note.set_text("")

    def update(_frame=None):
        # ---- raw streams -> the engine's own filters ------------------------
        t, xa, xb, reset = pair.read()
        if reset:                                   # dropout: start the filters again
            pa.__init__(args.notch)
            pb.__init__(args.notch)
            state["filled"] = 0
        for i in range(0, len(t), 50):              # small slices keep the 1 s buffers valid
            pa.push(xa[i:i + 50])
            pb.push(xb[i:i + 50])
        state["filled"] += len(t)
        usable = state["filled"] - SETTLE

        if usable >= n_trace:
            # Panel A - both signals went through the identical causal filter, so
            # its delay is the same for both and their relative timing is preserved.
            ya, yb = pa.narrow[band][-n_trace:, el], pb.narrow[band][-n_trace:, el]
            line_a.set_ydata(ya)
            line_b.set_ydata(yb)
            peak = max(np.percentile(np.abs(np.concatenate([ya, yb])), 99) * 1.5, 1.0)   # headroom for the legend
            ax_trace.set_ylim(-peak, peak)

        if usable >= need:
            # Panel B - instantaneous phase of each person (Hilbert transform),
            # then the difference at every sample of the engine's analysis window.
            ua = unit_phasors(pa.narrow[band][-need:, el:el + 1], trim)[:, 0]
            ub = unit_phasors(pb.narrow[band][-need:, el:el + 1], trim)[:, 0]
            cross = ua * np.conj(ub)
            share = np.histogram(np.angle(cross), bins=edges)[0] / len(cross)
            for bar, h in zip(bars, share):
                bar.set_height(h)
            top = max(share.max() * 1.15, 3 * uniform)
            ax_phase.set_ylim(0, top)
            mean = cross.mean()                     # mean resultant vector; |mean| is the PLV
            arrow.set_data([np.angle(mean)] * 2, [0, np.abs(mean) * top])
            one = np.ones(1, dtype=bool)
            lag_index, circular = phase_controls(ua[:, None], ub[:, None], one, one)
            phase_note.set_text(f"{ELECTRODES[el]}, last {window / FS:g} s:  PLV = {np.abs(mean):.2f}\n"
                                f"controls:  PLI = {lag_index:.2f}   circular corr. = {circular:+.2f}\n"
                                "dashed ring = no relationship")

        if usable >= need_env:
            # Panel E - the envelope is the absolute value of the Hilbert signal,
            # i.e. the size of the rhythm with its oscillation removed.
            ea = np.abs(hilbert(pa.narrow[band][-need_env:, el]))[MAX_TRIM:-MAX_TRIM]
            eb = np.abs(hilbert(pb.narrow[band][-need_env:, el]))[MAX_TRIM:-MAX_TRIM]
            env_a.set_ydata(ea)
            env_b.set_ydata(eb)
            ax_env.set_ylim(0, max(ea.max(), eb.max()) * 1.3)
            env_note.set_text(f"r = {np.corrcoef(ea, eb)[0, 1]:+.2f}  (this electrode, 10 s)")

        if usable >= GRANGER_WINDOW and time.time() >= state["next_granger"]:
            # Exploratory and display-only: nothing here is sent anywhere.
            state["next_granger"] = time.time() + 5
            a_to_b, b_to_a = granger_pair(pa.wide, pb.wide, good_channels(pa.broad), good_channels(pb.broad))
            explore.set_text(f"exploratory, display only:  Granger causality (10 s)  "
                             f"A→B {a_to_b:.3f}   B→A {b_to_a:.3f}   direction unreliable")

        # ---- engine output ---------------------------------------------------
        connect_engine()
        if state["inlet"] is not None:
            chunk, stamps = state["inlet"].pull_chunk(timeout=0.0, max_samples=4096)
            history.extend(zip(stamps, chunk))
        if history:
            lab = state["labels"]
            ts = np.array([h[0] for h in history]) - local_clock()
            data = np.array([h[1] for h in history])
            plv = data[:, lab[f"plv_{band}"]]
            floor = data[:, lab[f"plv_chance_{band}"]]
            line_plv.set_data(ts, plv)
            line_chance.set_data(ts, floor)
            chance_fill[0].remove()
            chance_fill[0] = ax_time.fill_between(ts, 0, np.nan_to_num(floor), color=CHANCE, lw=0, zorder=0)

            recent = data[-50:]                      # 50 values = 2 s at 25 Hz
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", RuntimeWarning)   # all-NaN during warm-up is expected
                for i, b in enumerate(BAND_NAMES):
                    value = np.nanmean(recent[:, lab[f"plv_{b}"]])
                    level = np.nanmean(recent[:, lab[f"plv_chance_{b}"]])
                    band_bars[i].set_height(0 if np.isnan(value) else value)
                    corr = np.nanmean(recent[:, lab[f"aec_{b}"]])
                    aec_bars[i].set_height(0 if np.isnan(corr) else corr)
                    chance_marks[i].set_ydata([np.nan if np.isnan(level) else level] * 2)
            last = data[-1]
            status.set_text(f"usable electrodes  A {last[lab['good_channels_a']]:.0f}/8   "
                            f"B {last[lab['good_channels_b']]:.0f}/8      "
                            f"engine score {last[lab['score']]:.2f}      "
                            f"data delay {last[lab['latency_s']] * 1000:.0f} ms")

    def on_key(event):
        if event.key == "s":
            name = time.strftime("synchrony_%Y%m%d_%H%M%S.png")
            fig.savefig(name, dpi=200)
            print("saved", name)

    if args.snapshot:
        end = time.time() + args.after
        while time.time() < end:
            update()
            time.sleep(0.05)
        fig.savefig(args.snapshot, dpi=150)
        print("saved", args.snapshot)
        return

    fig.canvas.mpl_connect("key_press_event", on_key)
    _animation = FuncAnimation(fig, update, interval=80, cache_frame_data=False)
    plt.show()


if __name__ == "__main__":
    main()
