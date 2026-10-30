# TODO

See PLAN.md for the why.

## Matthew
- [x] `git init` this folder (github.com/MatthewCarven/SoundTransmitter, public; pushing is OK'd)
- [ ] Desktop: set default playback device to "Speakers (3- High Definition Audio Device)" (dead jack) for listening
- [ ] Phase 1: install the WDK on the laptop (VS 2026 support? if not, VS 2022 Build Tools alongside)
- [ ] Phase 1: desktop restore point, `bcdedit /set testsigning on`, reboot

## Phase 0: pipe, no driver
- [x] Packet format (header + PCM), shared by both ends
- [x] st-send: WASAPI loopback on a named endpoint, UDP out, self-contained publish
- [x] st-recv: UDP in, reorder, jitter buffer, WASAPI out
- [x] Drift handling: 64-tap sinc resampler steered by a PI loop (not just drop/repeat)
- [x] First real run: 90 s, 0 lost / late / underruns, 0 tone glitches, drift ~+50 ppm
- [x] Wi-Fi scan dropouts found and absorbed: adaptive target, 100 ms default, skip-on-resume, own MMCSS capture loop. 10 min clean
- [ ] Longer unattended tone run (an hour or overnight)
- [x] Matthew listens to real music on the stereo: "Sounds good" (2026-10-30, LG TV output captured at 48 kHz)

## Surfaced in Phase 0
- [ ] Laptop Wi-Fi is on 2.4 GHz ch 6 and background-scans every minute or two (~55 ms silences). Matthew's call: Ethernet, 5 GHz, or taming scans (Location services, Killer software, `netsh wlan set autoconfig`). Then try `--latency 40`
- [ ] Desktop clock 3 min 26 s fast (see Claude-Notebook "Surfaced")
- [ ] Concealment: a lost or late packet plays as a hard cut to silence; fade instead
- [ ] Sender follows the default device only at start; follow default-device changes (IMMNotificationClient)
- [ ] A sender started over SSH runs hidden in the desktop's Services session and survives the SSH session ending (stop it with taskkill). Phase 3's logon task replaces that
- [ ] Receiver: rebuild the output when its device goes away or changes format
- [ ] Receiver: the adaptive target only grows. After a long clean stretch (say 10 min with no gap near the buffer), step it back down towards `--latency`. Seen 2026-10-30: one 25 s Wi-Fi outage left it at 250 ms for the rest of the session

## Phase 1: toolchain + stock sample
- [ ] Build unmodified simpleaudiosample on the laptop
- [ ] Install on desktop, see it in Sound settings, uninstall cleanly

## Phase 2: Sound Transmitter driver
- [ ] Render-only, renamed endpoint/INF/hardware ID, chosen formats
- [ ] Own test cert, install/uninstall scripts

## Phase 3: join up
- [ ] Sender targets Sound Transmitter, starts at logon
- [ ] Survives receiver off, format change, reinstall, sleep/wake
- [ ] Receiver tray app

## Phase 4: polish (as wanted)
- [ ] Settings, discovery, multi-receiver, Opus, stats
