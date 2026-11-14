"""
sync_hub.py - same command as before, now a thin launcher for synchrony_engine.py.

Run:      python sync_hub.py UnicornA UnicornB 127.0.0.1:7000 127.0.0.1:9000
          (two LSL stream names, then any number of OSC targets as ip:port)

Install:  pip install pylsl numpy scipy python-osc
Needs synchrony_engine.py and unicorn_lsl_sync.py in the same folder.

Why a launcher: the old hub and synchrony_engine.py did the same job (two
Unicorn LSL streams in, synchrony values out) with different code. Keeping two
implementations would mean TouchDesigner, Unity and recordings could disagree,
so the processing now lives in one place. Compared with the old hub the engine:
  - restarts cleanly after a headset dropout instead of interpolating over it
  - ignores bad-contact electrodes automatically, every second
  - stamps the LSL output with the time of the EEG, not the time of computing
  - uses a 10 s window for envelope correlation (the old 3 s was mostly chance)
  - adds /sync/<band>/plvc (phase locking above chance) and /sync/score

Changes you will notice:
  - band names follow Unicorn Bandpower: delta, theta, alpha, beta_low,
    beta_mid, beta_high, gamma   ("beta" is now three bands; alpha is 8-12 Hz)
  - updates arrive 25 times per second instead of 10
  - the LSL stream is called "UnicornSynchrony" (was "Synchrony")
  - to drop electrodes, add e.g.  --channels 1,2,3,5,6,7,8  to the command

Any extra options are passed straight to the engine (see
python synchrony_engine.py --help).
"""
import sys

from synchrony_engine import main

if __name__ == "__main__":
    positional = []
    rest = sys.argv[1:]
    while rest and not rest[0].startswith("--"):
        positional.append(rest.pop(0))
    if len(positional) < 2:
        sys.exit(__doc__)
    main(["--stream-a", positional[0], "--stream-b", positional[1], "--osc", *positional[2:], *rest]
         if positional[2:] else ["--stream-a", positional[0], "--stream-b", positional[1], *rest])
