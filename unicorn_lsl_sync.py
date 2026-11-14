#!/usr/bin/env python3
"""
unicorn_lsl_sync.py - receive two UnicornLSL raw-EEG streams on ONE shared clock.

Each acquisition laptop runs g.tec's UnicornLSL app (Unicorn Suite -> DevTools ->
LSL Interface) with a different stream name. This module runs on the analysis PC,
pulls both streams and returns them sample-aligned on a common 250 Hz time grid.

Used by synchrony_engine.py, or on its own to check the link and the clocks:

    python unicorn_lsl_sync.py --list                       (show visible streams)
    python unicorn_lsl_sync.py --stream-a NAME1 --stream-b NAME2

WHERE "EQUAL TIME" COMES FROM
-----------------------------
The LSL.cs you linked exposes the two functions LSL uses for this:

  local_clock()      each PC's own monotonic clock. UnicornLSL stamps every
                     sample with it. The two laptops' clocks are unrelated
                     (different zero points, slightly different speeds).

  time_correction()  the measured offset between a sender's clock and the
                     receiver's clock. LSL keeps re-measuring it in the
                     background with an NTP-style exchange (LSL.cs documents
                     it as empirically within +/-0.2 ms).

  remote timestamp + time_correction()  =  that moment on THIS PC's clock.

Doing that for both streams puts both headsets on this PC's clock, which is the
only clock the analysis ever uses. No Windows time settings need to match.

What LSL cannot correct: the Bluetooth delay between a headset and its laptop.
UnicornLSL stamps a sample when the laptop receives it, not when the electrode
measured it, so each stream carries an unknown delay (typically tens of ms)
that is roughly constant per session. A constant delay does not change phase
LOCKING, but it does shift the phase DIFFERENCE, so don't read "who leads" from
small lags.
"""

import argparse
import math
import time

import numpy as np
from pylsl import (StreamInlet, local_clock, proc_clocksync, proc_dejitter,
                   proc_monotonize, resolve_byprop, resolve_streams)

FS = 250.0          # Unicorn Hybrid Black sampling rate (fixed by the hardware)
N_EEG = 8           # UnicornLSL sends 17 channels; the first 8 are EEG (rest: accel, gyro, battery, counter, validation)
GAP_SECONDS = 0.25  # a hole this long in either stream is treated as a dropout (see _gap)


class _Stream:
    """One UnicornLSL inlet plus a small buffer of not-yet-aligned samples."""

    def __init__(self, name, timeout):
        found = resolve_byprop("name", name, timeout=timeout)
        if not found:
            raise RuntimeError(
                f"LSL stream '{name}' not found. Is UnicornLSL started on that laptop, "
                f"is the name exact, and is the firewall open? Try --list.")
        info = found[0]
        if abs(info.nominal_srate() - FS) > 1e-6:
            raise RuntimeError(f"'{name}' reports {info.nominal_srate()} Hz, expected {FS}. Is this the raw UnicornLSL stream?")
        if info.channel_count() < N_EEG:
            raise RuntimeError(f"'{name}' has {info.channel_count()} channels, need at least {N_EEG}.")
        self.name = name
        self.host = info.hostname()
        self.n_channels = info.channel_count()
        # DECISION - let liblsl apply the clock handling inside the inlet:
        #   proc_clocksync  adds time_correction() to every timestamp (the formula
        #                   in the header), using the continuously updated estimate.
        #   proc_dejitter   Bluetooth delivers samples in bursts, so raw timestamps
        #                   are bunched. A 250 Hz device really samples evenly, so
        #                   liblsl fits a smooth line through the timestamps. Without
        #                   this, burst jitter of ~10-30 ms would be a large phase
        #                   error at alpha/beta frequencies.
        #   proc_monotonize guarantees timestamps never step backwards (needed for
        #                   the interpolation below).
        self.inlet = StreamInlet(info, max_buflen=30,
                                 processing_flags=proc_clocksync | proc_dejitter | proc_monotonize)
        self.clear()

    def clear(self):
        self.ts = np.empty(0)
        self.data = np.empty((0, N_EEG))

    def pull(self):
        """Move everything waiting in the inlet into the buffer. True if a dropout was seen."""
        gap = False
        while True:
            chunk, ts = self.inlet.pull_chunk(timeout=0.0, max_samples=1024)
            if not ts:
                return gap
            ts = np.asarray(ts)
            if self.ts.size and ts[0] - self.ts[-1] > GAP_SECONDS:
                gap = True
            if ts.size > 1 and np.max(np.diff(ts)) > GAP_SECONDS:
                gap = True
            self.ts = np.concatenate([self.ts, ts])
            self.data = np.vstack([self.data, np.asarray(chunk, dtype=float)[:, :N_EEG]])

    def clock_offset(self):
        """time_correction(): seconds to add to that laptop's clock to get this PC's clock."""
        return self.inlet.time_correction(timeout=2.0)


class SyncedPair:
    """Two Unicorn streams resampled onto one shared, evenly spaced 250 Hz grid."""

    def __init__(self, name_a, name_b, timeout=15.0):
        self.a = _Stream(name_a, timeout)
        self.b = _Stream(name_b, timeout)
        self._next = None   # index of the next grid sample to emit; grid time = index / FS

    def read(self):
        """
        Returns (t, xa, xb, reset):
          t      (n,)    grid times in THIS PC's local_clock() seconds
          xa/xb  (n, 8)  EEG of participant A / B at exactly those times, in uV
          reset  True if a dropout occurred: everything before it is discarded
                 and the caller must restart its filters and buffers.
        n is 0 when no new complete data is available yet.
        """
        gap = self.a.pull() | self.b.pull()
        if gap:
            # DECISION - on a dropout, start again rather than interpolate across
            # the hole. Interpolating would invent EEG, and a jump would ring
            # through every filter for seconds.
            self.a.clear()
            self.b.clear()
            self._next = None
            return np.empty(0), np.empty((0, N_EEG)), np.empty((0, N_EEG)), True

        empty = (np.empty(0), np.empty((0, N_EEG)), np.empty((0, N_EEG)), False)
        if self.a.ts.size < 2 or self.b.ts.size < 2:
            return empty

        if self._next is None:
            # Start the grid at the first instant covered by BOTH streams.
            self._next = math.ceil(max(self.a.ts[0], self.b.ts[0]) * FS)

        # We can only align up to the newest moment that both streams have reached.
        last = math.floor(min(self.a.ts[-1], self.b.ts[-1]) * FS)
        if last < self._next:
            return empty

        # DECISION - resample both streams onto one grid instead of pairing
        # "sample n of A" with "sample n of B". The two headsets were started at
        # different moments and their crystals run at very slightly different
        # rates, so their samples never fall on the same instants and drift apart
        # over a session. Linear interpolation is accurate here because EEG
        # content of interest (< 50 Hz) changes slowly relative to the 4 ms
        # sample spacing.
        t = np.arange(self._next, last + 1) / FS
        xa = np.column_stack([np.interp(t, self.a.ts, self.a.data[:, c]) for c in range(N_EEG)])
        xb = np.column_stack([np.interp(t, self.b.ts, self.b.data[:, c]) for c in range(N_EEG)])
        self._next = last + 1

        # Keep one sample before the next grid point so interpolation can continue.
        for s in (self.a, self.b):
            keep = max(int(np.searchsorted(s.ts, t[-1], side="right")) - 1, 0)
            s.ts, s.data = s.ts[keep:], s.data[keep:]
        return t, xa, xb, False

    def clock_report(self):
        """One line describing how far each laptop's clock is from this PC's clock."""
        parts = []
        for s in (self.a, self.b):
            try:
                parts.append(f"{s.name}@{s.host}: {s.clock_offset() * 1000:+.3f} ms")
            except Exception as err:   # stream lost or no estimate yet
                parts.append(f"{s.name}: no estimate ({type(err).__name__})")
        return "clock offsets to this PC -> " + " | ".join(parts)


def list_streams(wait=2.0):
    for info in resolve_streams(wait):
        print(f"  name='{info.name()}'  type='{info.type()}'  {info.channel_count()} ch  "
              f"{info.nominal_srate():g} Hz  host={info.hostname()}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="Check alignment of two UnicornLSL streams.")
    ap.add_argument("--list", action="store_true", help="list every LSL stream visible on the network")
    ap.add_argument("--stream-a")
    ap.add_argument("--stream-b")
    args = ap.parse_args()
    if args.list or not (args.stream_a and args.stream_b):
        print("Visible LSL streams:")
        list_streams()
        raise SystemExit(0)

    pair = SyncedPair(args.stream_a, args.stream_b)
    print(pair.clock_report())
    total, started = 0, local_clock()
    while True:
        t, xa, xb, reset = pair.read()
        total += len(t)
        if reset:
            print("dropout - realigning")
        if len(t) and local_clock() - started > 2.0:
            # lag = how far behind real time the aligned data is (network + buffering)
            print(f"{total} aligned samples | lag {(local_clock() - t[-1]) * 1000:.0f} ms | {pair.clock_report()}")
            started = local_clock()
        time.sleep(0.01)
