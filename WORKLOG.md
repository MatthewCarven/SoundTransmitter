# WORKLOG

## 2026-10-30: plan

Matthew asked whether a Windows output driver that sends audio over the network to
an app on another machine is doable, plan only. Motivation: the desktop's rear
speaker jack is physically dead. The endpoint still shows as active, but no sound
comes out.

Checked both machines (read-only; the desktop over SSH):
- Desktop: Win 11 Home 22631, ASRock Z77 Pro4-M, Realtek ALC892 (HDAUDIO VEN_10EC
  DEV_0892) on Microsoft's generic HDA driver. Active outputs: Speakers (dead jack),
  Headphones, S/PDIF, LG TV + Dell S2740L over NVIDIA HDMI. `Confirm-SecureBootUEFI`
  gives "Variable is currently undefined", so it's a legacy BIOS boot and test
  signing needs no BIOS change. No VS, WDK or .NET.
- Laptop: VS Community 2026 18.11, Windows SDK 10.0.26100 (no `km\portcls.h`, so no
  WDK), .NET 10.0.402, Secure Boot off.

Decisions (see PLAN.md):
- The driver is a dumb WaveRT sink from Microsoft's simpleaudiosample. No kernel
  networking.
- A user-mode sender captures through WASAPI loopback and sends UDP PCM.
- The receiver on the laptop does the jitter buffer and drift correction.
- C# .NET 10 for both apps.
- Phase 0 needs no driver: it loopback-captures the dead-but-active Speakers
  endpoint.

## 2026-10-30: Phase 0 built

Solution `SoundTransmitter.slnx`, .NET 10, NAudio.Wasapi 2.2.1:
- Core holds the packet format (32-byte header: stream id, sequence, first
  frame index, rate, channels, format), sample conversion (TPDF-dithered s16 or
  f32, downmix to stereo), `Packetizer`, `FrameRing` (frames by absolute index,
  so reordering is free and holes read as silence), `Resampler` (64-tap Kaiser
  sinc, 256 phases), `DriftController` (PI on the smoothed buffer level, ~0.01 Hz
  loop, clamped ±0.1%), `Playout`, and `ToneChecker` (least-squares sine fit per
  10 ms block).
- The resampler does the 44.1 to 48 kHz conversion and the drift correction in
  one pass. That conversion was needed from day one: the desktop's dead Speakers
  run at 44.1 kHz, the laptop's Headphones (the default) at 48.
- st-send subclasses NAudio's `WasapiCapture` with the loopback flag and a 30 ms
  buffer. NAudio's own loopback class polls every 50 ms, which makes lumpy
  packets. It calls `timeBeginPeriod(1)` so its 15 ms sleeps are 15 ms. It also
  keeps a silent render stream open on the device, because loopback stops
  delivering when nothing plays. `--tone` makes that stream a test tone.
- Firewalls: both machines are on "Public" with the firewall on. No rule is
  needed. The receiver sends a Hello to the sender every second; the sender sends
  Beacons to the receiver until it hears one, then streams; stateful filtering
  lets each side's replies in. Both sockets turn off SIO_UDP_CONNRESET, or an
  ICMP unreachable from the other end not running yet kills the next receive.

Tests: 28 passing. The important ones run `Playout` against a simulated network
(1 ms ticks; jitter up to 10 ms, 1% of packets 20 ms later; loss; a 300 ms
stall; a sender restart) for ten simulated minutes at +100, 0 and -100 ppm: no
underruns, no glitches, buffer within 2 ms of target, drift measured to ±10 ppm.

Two findings on the way:
- The first sim run had one glitch in 60,000 blocks at -100 ppm. It was the sim
  being harsher than the buffer: 25 ms extra delay on top of 10 ms of jitter can
  beat a 40 ms buffer at the bottom of its packet sawtooth. Rule of thumb: target
  >= worst jitter + ~10 ms. Hence the 60 ms default for the Wi-Fi laptop.
- 16-bit encode scaled by 32767 but decoded by 32768: a 0.9 LSB gain error, which
  the round-trip test caught.

Real runs, desktop (over SSH) to laptop, receiver muted, tone checked:
- 90 s: 15,882 packets, 0 lost / late / underruns / resyncs, 0 glitches in
  8,626 blocks. Drift settled at about +50 ppm (the desktop's codec runs fast of
  the laptop's) within ~30 s, buffer 60.0 ± 0.5 ms. The sender went idle 5 s
  after the receiver stopped, as designed.
- 10 min (a): 0 lost, 0 late, but 6 underruns and 20 glitch blocks, every
  glitch belonging to an underrun. After each one the backlog arrived in a
  burst, and the buffer sat at 110–130 ms for a minute while the drift loop
  slewed it down at its 0.1% limit.
  - Fix: on resume from rebuffering, skip straight to target. Nothing is playing
    at that moment, so it costs nothing. The stall sim test now requires the
    level to be back on target within 3 s.
- 10 min (b), with gap logging on both ends: 27 network silences of 42–68 ms,
  nothing ever lost. Twelve came back to back in about a second at 12:23:18,
  which is a Wi-Fi background scan: the laptop's Killer AX1650i is on
  2.4 GHz channel 6, 802.11n, and visits ~13 channels at ~55 ms each while the
  router holds its packets. The power plan is High performance and Wi-Fi power
  saving is at maximum performance, so it isn't power saving. Each silence over
  ~55 ms emptied the 60 ms buffer (6 underruns).
  - Separately, the desktop's sender paused once for 42 ms, at the same moment
    as the first underrun (desktop 12:25:54 = laptop 12:22:28; the desktop
    clock runs 3 min 26 s fast). With NAudio's 30 ms capture buffer that can
    lose audio at the source.
  - Fixes:
    - `Playout` raises its target 30 ms per underrun, up to `--max-latency`
      (300). Rebuffering refills to the new target, so a burst of scan gaps
      raises it once or twice, not a dozen times. Sim test: a 70 ms stall every
      20 s from a 40 ms start ends at 70–130 ms, with no underruns after the
      first minute.
    - The default `--latency` is now 100.
    - The sender's own capture loop replaces NAudio's `WasapiCapture`: a 200 ms
      buffer, polled every 5 ms, on an MMCSS "Pro Audio" thread, counting the
      engine's DataDiscontinuity flags as "capture overflow".
- 10 min (c), defaults (100 ms, may grow to 300): **clean**. 109,601 packets,
  0 lost / late / underruns / resyncs, 0 glitches in 59,625 blocks, buffer
  98.5–101.2 ms. 23 network silences of 41–60 ms, all absorbed: a single ~55 ms
  one almost exactly every 61 s, plus a full scan burst (12 in a row) at
  12:43:20. The new capture delivered every ~10 ms, its worst pause 29 ms, with 0
  overflows.

Live, 12:51: the sender captured the desktop's default output (the LG TV, 48 kHz,
so no rate conversion) and the receiver played it unmuted on the laptop's
Headphones jack into the stereo. Matthew: "Sounds good." Phase 0 is done.

Not done: an hour-long or overnight run.

Matthew ran `git init` and made github.com/MatthewCarven/SoundTransmitter
(public). Phase 0 is committed as 46fe2d5 on `main`, renamed from git's default
`master` to match his other repos, and pushed, with his go-ahead to push.

Scripts live, 13:00 onwards: `startserver.cmd` on the desktop (started over
SSH), `startclient.cmd` on the laptop.
- 13:11 (laptop time): the laptop's Wi-Fi dropped out for about 25 s, in two
  goes (~14 s, then 11.3 s). 5,064 packets lost, 2 underruns, target up to
  160 ms. My SSH session to the desktop died at the same moment
  (`client_loop: send disconnect: Connection reset`, exit 255), but st-send
  carried on streaming, as before when ssh was killed.
- 13:14: a fresh `startclient.cmd` window took over (started from Explorer, so
  probably Matthew). It underran 4 times in its first 13 s, settling at 220 ms.
- Since then the Wi-Fi has been choppier than this morning: signal 72% (was
  92%), still 2.4 GHz ch 6, with 40–70 ms silences every few seconds rather
  than once a minute. One more underrun at 14:34 took the target to 250 ms.
  Playing cleanly at 15:11.
- The target only ever grows, so one bad patch leaves 250 ms of delay for the
  rest of the session. Added to the TODO: let it shrink back after a long clean
  stretch.

20:50, after Matthew's singing: he couldn't find either program in Task Manager
(the desktop's runs hidden in the Services session) and asked me to stop both.
Stopped the laptop's client window and st-recv, and over SSH the desktop's
startserver.cmd and then st-send (the script first, or it restarts the
program). Nothing of Sound Transmitter is running on either machine.

The target now comes back down. `Playout` tracks the least spare buffer (beyond
the 10 ms about to play) since playback last started. After 10 minutes of play
(`Playout.ShrinkAfterSeconds`), if that never fell below a step plus a margin
(30 + 15 ms), the target drops a step, never below `--latency`; either way the
next stretch starts. The drift controller slews the level down at its 0.1%
limit, about 30 s a step, so nothing is skipped. st-recv logs each step down.
- New sim test: two 300 ms stalls take a 100 ms target to 160; with 60 s
  stretches it's back to exactly 100 by 300 s, with no further underruns or
  tone glitches, the level within 2 ms of target.
- The repeated-stalls test (70 ms every 20 s) now runs 600 s with 60 s
  stretches: every stretch has a close call, so the target stays up (70–130)
  with no underruns after minute 2, rather than see-sawing.
- 31 tests pass. st-recv republished. The sender is unchanged, so the desktop
  needs nothing new.
