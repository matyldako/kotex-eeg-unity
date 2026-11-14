"""
LSL check: are both headset streams visible on THIS computer and delivering good data?

Install:  pip install pylsl numpy
Run:      python lsl_check.py UnicornA UnicornB     (check these two stream names)
          python lsl_check.py                       (check every stream it can find)

Run it on the main PC (the one that will run sync_hub.py), with both Unicorn LSL
apps streaming. It lists every LSL stream on the network, then records 10 seconds
from each and reports lost samples (from the headset's own sample counter), sample
rate, clock offset, delay and dead channels.
"""
import sys
import time

import numpy as np
from pylsl import StreamInlet, resolve_streams, local_clock, proc_clocksync

DURATION = 10.0  # seconds of data to record per test
SETTLE = 1.5     # seconds discarded first, so a start-up backlog is not counted
COUNTER_CH = 15  # standard Unicorn layout: 8 EEG, 3 accel, 3 gyro, battery, counter, validation
BATTERY_CH = 14
N_EEG = 8        # first 8 Unicorn channels are EEG
RATE_TOL = 0.05  # allowed deviation from nominal sample rate (5 %)
MAX_DELAY = 0.5  # seconds; newest sample older than this = stream is lagging


def main():
    wanted = sys.argv[1:]
    print("Searching the network for LSL streams (3 s) ...")
    streams = resolve_streams(wait_time=3.0)
    if not streams:
        sys.exit("FAIL: no LSL streams found at all.\n"
                 "  - Is the Unicorn LSL app started AND streaming (not just open)?\n"
                 "  - For the stream from the other PC: same network? Windows Firewall?")

    print(f"\nFound {len(streams)} stream(s):")
    for s in streams:
        print(f"  name='{s.name()}'  type='{s.type()}'  channels={s.channel_count()}  "
              f"rate={s.nominal_srate():g} Hz  from PC '{s.hostname()}'")

    if wanted:
        chosen = []
        for name in wanted:
            match = [s for s in streams if s.name() == name]
            if not match:
                print(f"\nFAIL: stream '{name}' not found (names are case-sensitive).")
            elif len(match) > 1:
                print(f"\nFAIL: {len(match)} streams are both called '{name}'. "
                      "Give each headset its own stream name.")
            else:
                chosen.append(match[0])
        if len(chosen) != len(wanted):
            sys.exit(1)
    else:
        chosen = [s for s in streams if s.nominal_srate() > 0 and s.name() != "Synchrony"]

    names = [s.name() for s in chosen]
    if len(set(names)) != len(names):
        print("\nWARNING: two streams share a name; sync_hub.py needs distinct names.")

    inlets = [StreamInlet(s, max_buflen=10, processing_flags=proc_clocksync) for s in chosen]
    offsets = [inlet.time_correction(timeout=5.0) for inlet in inlets]
    data = [[] for _ in inlets]
    stamps = [[] for _ in inlets]

    print(f"\nRecording {DURATION:g} s from {len(inlets)} stream(s) ...")
    t_settle = local_clock()                  # throw away the start-up backlog
    while local_clock() - t_settle < SETTLE:
        for inlet in inlets:
            inlet.pull_chunk(timeout=0.0)
        time.sleep(0.02)
    t0 = local_clock()
    while local_clock() - t0 < DURATION:
        for i, inlet in enumerate(inlets):
            chunk, ts = inlet.pull_chunk(timeout=0.0)
            data[i].extend(chunk)
            stamps[i].extend(ts)
        time.sleep(0.02)
    t_end = local_clock()

    all_ok = True
    for i, s in enumerate(chosen):
        problems = []
        n = len(stamps[i])
        print(f"\n--- {s.name()} (from PC '{s.hostname()}') ---")
        if n == 0:
            print("  FAIL: stream is visible but sent no samples. "
                  "Headset off, out of range, or app not streaming.")
            all_ok = False
            continue

        x = np.asarray(data[i], dtype=float)
        rate = n / DURATION
        delay = t_end - stamps[i][-1]
        print(f"  samples received : {n}  (~{rate:.1f} Hz, nominal {s.nominal_srate():g} Hz)")
        print(f"  clock offset     : {offsets[i] * 1000:+.2f} ms  (LSL corrects this automatically)")
        print(f"  newest sample age: {delay * 1000:.0f} ms")

        nominal = s.nominal_srate()
        if x.shape[1] == 17:
            # the headset numbers every sample, so gaps in the counter = truly lost samples
            steps = np.diff(x[:, COUNTER_CH])
            lost = int(np.sum(steps[steps > 1] - 1))
            print(f"  lost samples     : {lost}  (from the headset's sample counter)")
            print(f"  battery          : {x[-1, BATTERY_CH]:.0f} %")
            if lost > 0.01 * n:
                problems.append(f"{lost} samples lost (weak Bluetooth link; move the dongle closer)")
            if abs(rate - nominal) > RATE_TOL * nominal:
                print("  note: measured rate differs from nominal, but the counter shows what was "
                      "really lost.\n        Bluetooth delivers in bursts, so a short count can run high or low.")
        elif rate < (1 - RATE_TOL) * nominal:
            problems.append("sample rate is low: samples are being lost")
        elif rate > (1 + RATE_TOL) * nominal:
            print("  note: rate reads high; data is arriving in bursts (normal for Bluetooth).")
        if delay > MAX_DELAY:
            problems.append("data is arriving late (weak Bluetooth link or network trouble)")
        if x.shape[1] < N_EEG:
            problems.append(f"only {x.shape[1]} channels; sync_hub.py expects at least {N_EEG}")
        else:
            eeg = x[:, :N_EEG]
            if not np.isfinite(eeg).all():
                problems.append("EEG contains NaN/inf values")
            sd = eeg.std(axis=0)
            print("  EEG channel std  : " + "  ".join(f"{v:.1f}" for v in sd))
            flat = [str(c + 1) for c in range(N_EEG) if sd[c] == 0]
            if flat:
                problems.append("flat (dead) EEG channel(s): " + ", ".join(flat))

        if problems:
            all_ok = False
            for p in problems:
                print("  FAIL: " + p)
        else:
            print("  OK")

    if len(chosen) < 2:
        print("\nNOTE: fewer than two streams tested; synchrony needs two.")
        all_ok = False

    print("\nRESULT: " + ("PASS - both streams are usable from this computer."
                          if all_ok else "FAIL - see messages above."))
    sys.exit(0 if all_ok else 1)


if __name__ == "__main__":
    main()
