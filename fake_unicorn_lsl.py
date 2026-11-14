#!/usr/bin/env python3
"""
fake_unicorn_lsl.py - two simulated UnicornLSL streams for testing without headsets.

    python fake_unicorn_lsl.py
    python sync_hub.py FakeUnicorn_A FakeUnicorn_B 127.0.0.1:7000 127.0.0.1:9000
    python sync_viewer.py FakeUnicorn_A FakeUnicorn_B

Each stream looks like UnicornLSL output: 17 channels at 250 Hz, sent in bursts,
with large electrode offsets. Both "brains" have theta (~6 Hz), alpha (~10 Hz)
and gamma (~40 Hz) rhythms - the three bands shown in the Unity overlay - plus
noise. Like real people, the two have slightly different frequencies, and each
person's alpha frequency wanders a little, so the alpha PLV swings from second to
second as it does in a real two-headset recording (see FREQ_WANDER).

The simulation steps through four states, 30 s each, so that every measure in
synchrony_engine.py has a state where it should respond and states where it
should not. The console prints the current state.

  state         what the two brains share                 expected in the engine / viewer
  -----------   ----------------------------------------  --------------------------------------
  independent   nothing                                   PLV ~0.2, at its chance floor; PLI ~0.2;
                                                          AEC near 0; Granger small both ways.
                                                          Alpha PLV is higher and more varied: about
                                                          0.33 on average, 0.1 to 0.7+ second to second
  phase         the rhythms' PHASE; B runs 60 degrees     PLV and PLI ~0.7-0.9 (gamma lowest),
                behind A. Strengths stay independent.     circular corr. clearly positive;
                                                          AEC stays low
  amplitude     the rhythms' STRENGTH rises and falls     AEC ~0.8 alpha, ~0.5 theta, ~0.3 gamma;
                together. Phases stay independent.        PLV and PLI stay ~0.2
  directed      B's background activity copies A's        Granger A->B roughly 10x B->A;
                60 ms later. Rhythms stay independent.    PLV and AEC stay near chance

Those figures are from one test run and vary from run to run. Values are below
1 even when the coupling is perfect because every electrode also has its own
noise. AEC in particular swings by +/-0.2 by chance in any state.

Allow for the engine's windows when watching a change: PLV reacts within about
1 s (2 s for theta), the chance floor and AEC take about 10 s, Granger is
reported every 10 s. During a long "phase" state the chance floor climbs, so the
engine's above-chance score falls again even though the PLV stays high.

    --state phase       hold one state instead of cycling
    --block 60          seconds per state when cycling (default 30)
"""

import argparse
import time

import numpy as np
from pylsl import StreamInfo, StreamOutlet, local_clock
from scipy.signal import lfilter

FS, N_CH, N_EEG = 250, 17, 8
STATES = ["independent", "phase", "amplitude", "directed"]

# rhythm: (frequency of A in Hz, frequency of B in Hz, mean amplitude in uV).
# The two people differ slightly in frequency, as real people do. With identical
# frequencies, unrelated rhythms would stay in step for seconds at a time and the
# "independent" state would look locked.
RHYTHMS = {"theta": (5.6, 6.4, 8.0), "alpha": (9.6, 10.5, 12.0), "gamma": (38.0, 41.0, 8.0)}
NOISE_UV = 6.0          # independent sensor/background noise on every electrode
PHASE_WANDER = 0.05     # rad per sample; makes each rhythm drift like real EEG
# Extra slow wander of each person's rhythm FREQUENCY, per rhythm: (size in Hz, seconds it takes to change).
# Only alpha has it. In a real two-headset recording the alpha PLV swings between about 0.1 and 0.7 from
# one second to the next without the two people being coupled, because each person's alpha frequency is
# never steady: in some seconds the two happen to run at the same speed (high PLV), in others they drift
# apart quickly (low PLV). Theta and gamma are left steady.
# Size: 0.2 Hz puts the mean 1 s alpha PLV near 0.33, as in the recording; larger = wider swings and a
# higher mean. Seconds: 0.3 leaves one second unrelated to the next, as in the recording; larger = each
# high or low spell lasts longer.
FREQ_WANDER = {"theta": (0.0, 1.0), "alpha": (0.2, 0.3), "gamma": (0.0, 1.0)}
PHASE_LAG = np.pi / 3   # in the "phase" state B runs 60 degrees behind A.
                        # Not 0: the phase lag index ignores locking at exactly 0 or 180 degrees.
ENVELOPE_TAU = 1.0      # seconds; how slowly a rhythm's strength rises and falls
ENVELOPE_DEPTH = 0.5    # strength varies by about +/-50 % around its mean
DRIVE_UV = 7.0          # size of the background activity used for the "directed" state
DRIVE_DELAY = 15        # samples = 60 ms: B copies A's background this much later


class SlowNoise:
    """Smooth random signal (unit variance) - used for envelopes and background activity."""

    def __init__(self, rng, shape, tau_seconds):
        self.rng, self.shape = rng, shape
        self.rho = np.exp(-1.0 / (FS * tau_seconds))
        self.state = np.zeros((1,) + shape)

    def next(self, n):
        white = self.rng.normal(0, np.sqrt(1 - self.rho ** 2), (n,) + self.shape)
        out, self.state = lfilter([1.0], [1.0, -self.rho], white, axis=0, zi=self.state)
        return out


class TwoBrains:
    def __init__(self):
        self.rng = np.random.default_rng()
        k = len(RHYTHMS)
        self.freq = np.array([[fa for fa, _, _ in RHYTHMS.values()],
                              [fb for _, fb, _ in RHYTHMS.values()]])    # (person, rhythm)
        self.amp = np.array([a for _, _, a in RHYTHMS.values()])
        self.phase = self.rng.uniform(0, 2 * np.pi, (2, k))          # current phase, per person and rhythm
        self.freq_wander_hz = np.array([FREQ_WANDER[name][0] for name in RHYTHMS])
        self.freq_wander = [SlowNoise(self.rng, (2,), FREQ_WANDER[name][1]) for name in RHYTHMS]   # per rhythm
        self.env_own = SlowNoise(self.rng, (2, k), ENVELOPE_TAU)      # each person's own strength changes
        self.env_shared = SlowNoise(self.rng, (k,), ENVELOPE_TAU)     # strength changes common to both
        self.drive = SlowNoise(self.rng, (2,), 0.006)                 # broadband background activity per person
        self.drive_past = np.zeros(DRIVE_DELAY)                       # A's last 60 ms of background
        self.offsets = self.rng.uniform(-3000, 3000, (2, N_EEG))      # electrode DC offsets, like real hardware

    def next(self, n, state):
        """n new samples for both people: array (2, n, 17)."""
        k = len(RHYTHMS)

        # --- phase: every rhythm advances at its frequency (alpha's own frequency wanders) plus a random wander
        freq = self.freq + self.freq_wander_hz * np.stack([w.next(n) for w in self.freq_wander], axis=2)
        steps = 2 * np.pi * freq / FS + self.rng.normal(0, PHASE_WANDER, (n, 2, k))
        phases = self.phase + np.cumsum(steps, axis=0)                # (n, person, rhythm)
        if state == "phase":
            phases[:, 1, :] = phases[:, 0, :] - PHASE_LAG             # B follows A's phase exactly, 60 deg behind
        self.phase = phases[-1] % (2 * np.pi)

        # --- strength: slow rise and fall, own or shared
        own = self.env_own.next(n)                                    # always advanced, so states switch smoothly
        shared = self.env_shared.next(n)
        if state == "amplitude":
            wobble = np.repeat(shared[:, None, :], 2, axis=1)         # the same curve for both people
        else:
            wobble = own
        envelope = self.amp * np.clip(1 + ENVELOPE_DEPTH * wobble, 0.2, None)
        rhythms = (envelope * np.sin(phases)).sum(axis=2)             # (n, person)

        # --- background activity: B copies A's with a delay in the "directed" state
        drive = self.drive.next(n) * DRIVE_UV                         # (n, person)
        a_history = np.concatenate([self.drive_past, drive[:, 0]])
        self.drive_past = a_history[-DRIVE_DELAY:]
        if state == "directed":
            drive[:, 1] = 0.9 * a_history[:n] + 0.3 * drive[:, 1]     # a_history[:n] is A, 60 ms ago

        out = np.zeros((2, n, N_CH))
        for p in range(2):
            signal = rhythms[:, p] + drive[:, p]
            out[p, :, :N_EEG] = (signal[:, None] + self.rng.normal(0, NOISE_UV, (n, N_EEG))
                                 + self.offsets[p])
        return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--state", choices=STATES + ["cycle"], default="cycle",
                    help="hold one state, or cycle through all four (default)")
    ap.add_argument("--block", type=float, default=30.0, help="seconds per state when cycling (default 30)")
    args = ap.parse_args()

    outlets = [StreamOutlet(StreamInfo(f"FakeUnicorn_{p}", "Data", N_CH, FS, "float32", f"fake_unicorn_{p}"))
               for p in "AB"]
    brains = TwoBrains()
    start = local_clock()
    sent = 0
    shown = None
    print("streaming FakeUnicorn_A and FakeUnicorn_B - Ctrl+C to stop")
    try:
        while True:
            elapsed = local_clock() - start
            due = int(elapsed * FS) - sent
            if due < 8:                                  # emulate Bluetooth: samples arrive in bursts
                time.sleep(0.01)
                continue
            state = args.state if args.state != "cycle" else STATES[int(elapsed // args.block) % len(STATES)]
            if state != shown:
                print(f"{elapsed:6.0f} s  state: {state}")
                shown = state
            chunk = brains.next(due, state)
            stamp = local_clock()
            for p in range(2):
                outlets[p].push_chunk(chunk[p].tolist(), stamp)
            sent += due
    except KeyboardInterrupt:
        print("\nstopped")


if __name__ == "__main__":
    main()
