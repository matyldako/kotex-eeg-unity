#!/usr/bin/env python3
"""
fake_unicorn_lsl.py - two simulated UnicornLSL streams for testing without headsets.

    python fake_unicorn_lsl.py            (streams "FakeUnicorn_A" and "FakeUnicorn_B")
    python synchrony_engine.py --stream-a FakeUnicorn_A --stream-b FakeUnicorn_B

Each stream looks like UnicornLSL output: 17 channels at 250 Hz, sent in bursts.
Every 30 s the two "brains" switch between a shared 10 Hz alpha rhythm (score
should rise) and independent alpha rhythms (score should fall to about 0).
"""

import time

import numpy as np
from pylsl import StreamInfo, StreamOutlet, local_clock

FS, N_CH, N_EEG, BLOCK = 250, 17, 8, 30.0
rng = np.random.default_rng()


def main():
    outlets = [StreamOutlet(StreamInfo(f"FakeUnicorn_{p}", "Data", N_CH, FS, "float32", f"fake_unicorn_{p}"))
               for p in "AB"]
    offsets = rng.uniform(-3000, 3000, (2, N_EEG))      # electrode DC offsets, like real hardware
    phase = np.zeros(3)                                  # shared, private A, private B
    freq = np.array([10.0, 9.6, 10.5])
    start = local_clock()
    sent = 0
    print("streaming FakeUnicorn_A and FakeUnicorn_B - Ctrl+C to stop")
    while True:
        due = int((local_clock() - start) * FS) - sent
        if due < 8:                                      # emulate Bluetooth bursts
            time.sleep(0.01)
            continue
        coupled = int((local_clock() - start) // BLOCK) % 2 == 1
        chunk = np.zeros((2, due, N_CH))
        for n in range(due):
            # each rhythm's phase wanders a little, as real alpha does
            phase += 2 * np.pi * freq / FS + rng.normal(0, 0.05, 3)
            for p in range(2):
                alpha = 12 * np.sin(phase[0] + 0.6 * p) if coupled else 12 * np.sin(phase[1 + p])
                chunk[p, n, :N_EEG] = alpha + rng.normal(0, 8, N_EEG) + offsets[p]
        stamp = local_clock()
        for p in range(2):
            outlets[p].push_chunk(chunk[p].tolist(), stamp)
        sent += due
        if sent % (FS * 5) < due:
            print("coupled" if coupled else "independent")


if __name__ == "__main__":
    main()
