# Sound Transmitter — plan

A virtual sound card on the desktop, listed in Windows as **Sound Transmitter**.
Anything played to it goes across the LAN to a small receiver app on the laptop,
which plays it through the laptop's speakers.

**Why:** the desktop's rear speaker jack is physically dead (2026-10-30). Windows
still lists the onboard "Speakers" device as working and mixes audio into it, but
nothing comes out of the port. The stereo is hooked up to the laptop, so the goal is
desktop audio, through the laptop, to the stereo. HDMI to the TV would give the
desktop sound, but not on the stereo.

## Is it possible?

Yes. It's well-trodden ground: virtual audio cables (VB-Cable) and the open-source
Scream project both do the driver half, and Microsoft ships a sample virtual audio
driver (`simpleaudiosample`, and its bigger sibling SYSVAD) to build on instead of
writing one from scratch.

The hard parts aren't the driver code. They are:

1. **Kernel driver signing.** 64-bit Windows won't load an unsigned driver.
2. **Clock drift.** Two machines' clocks never agree exactly, so the receiver's
   buffer slowly fills or drains.
3. **Latency.** Every stage adds a few milliseconds.

## The two machines (checked 2026-10-30)

|              | Desktop (`id10terror-desktop`): sender | Laptop: receiver + build machine |
|--------------|-----------------------------------------|----------------------------------|
| OS           | Windows 11 Home 23H2 (22631)            | Windows 11 Home 23H2 (22631)     |
| Audio        | ASRock Z77 Pro4-M, Realtek ALC892 on Microsoft's generic HDA driver. Rear "Speakers" endpoint active, jack dead. Headphones, S/PDIF, LG TV and Dell (HDMI) endpoints also active | Working speakers |
| Secure Boot  | None: boots legacy BIOS, so test-signing needs no BIOS change | Off |
| Tools        | No Visual Studio, no WDK, no .NET       | VS Community 2026 (18.x), Windows SDK 10.0.26100, .NET 10 SDK. **No WDK yet** |
| Reach        | SSH from the laptop works               | (n/a)                            |

So everything gets built on the laptop and copied to the desktop, and I drive the
desktop over SSH. A blue screen on the desktop while testing doesn't take the build
machine (or this Claude session) down with it.

## Architecture

```
DESKTOP                                               LAPTOP

 apps --> Windows audio engine --> [Sound Transmitter]
                                    virtual driver
                                    (kernel; just a sink)
                  |
                  | WASAPI loopback tap
                  v
            st-send.exe  -- UDP, PCM 48 kHz -->  st-recv.exe --> jitter buffer --> speakers
            (user mode)                          (user mode)      + drift fix
```

Three pieces:

1. **The driver** (C++, kernel mode). A WaveRT render-only device with no hardware
   behind it. Windows writes audio into its ring buffer, and the driver reports the
   play position moving at real-time speed; otherwise the data is thrown away. It's
   Microsoft's `simpleaudiosample` (from the Windows-driver-samples repo) cut down to
   one speaker endpoint and renamed. **It does no networking.**
2. **The sender** (C#, .NET 10, user mode, on the desktop). Opens the Sound
   Transmitter endpoint in WASAPI *loopback* mode (the same tap OBS uses to record
   "desktop audio") and sends what it hears as UDP packets.
3. **The receiver** (C#, .NET 10, on the laptop). Takes the packets, smooths out
   network jitter, corrects clock drift, and plays the audio through WASAPI on the
   default output.

### Why the driver doesn't send the packets itself

It could. Scream does, through Winsock Kernel. But every line of network code in the
kernel is a potential blue screen. It's also awkward to configure (the target IP
lives in the registry), and debugging it needs a kernel debugger. Loopback capture
gets the same samples in user mode for free, so the driver stays about 95%
Microsoft's tested sample.

If loopback ever falls short, the fallback is for the driver to hand its ring buffer
to the sender directly (an IOCTL plus shared memory). That still keeps networking
out of the kernel.

### Wire format (first version)

- UDP, unicast to the laptop's IP. Multicast later if there's ever more than one
  receiver.
- Each packet: a small header (magic, version, sequence number, sample index of the
  first frame, rate, channels, sample format) followed by PCM.
- 48 kHz stereo 16-bit is 1.5 Mbit/s, trivial on a LAN. At 5 ms per packet the
  payload is 960 bytes, which fits one Ethernet frame unfragmented, at 200 packets
  a second.
- Opus compression is optional and only for later. It matters only on bad Wi-Fi or
  over the internet.

### The two real problems

- **Jitter.** Packets arrive bunched, especially if the laptop is on Wi-Fi. The
  receiver holds a buffer (start at ~40 ms, tunable) and reorders by sequence
  number. A lost packet becomes a short fade to silence, not a stall.
- **Clock drift.** The virtual device runs off the desktop's CPU timer, and the
  laptop's sound card runs off its own crystal. They disagree by tens of parts per
  million. At 50 ppm the receiver's buffer gains or loses ~100 ms every half hour,
  so without a fix you get a click, a dropout or creeping lag every so often. The
  fix: the receiver watches its buffer level and resamples by a tiny ratio
  (±0.01–0.1%, inaudible) to hold it on target. The first version can drop or repeat
  single samples. The proper fix is a small adaptive resampler.

### Latency budget

Audio engine ~10 ms, loopback period ~10, packetising 5, network 1–5 wired (5–30 on
Wi-Fi), jitter buffer ~40, laptop output 10–20. That's roughly **80–130 ms** in all:
fine for music, borderline for lip-sync on video, noticeable in games. On a wired
link it can be tuned down.

## Signing: the catch

- **Test-signing mode** (the route for us). Run `bcdedit /set testsigning on` on the
  desktop and reboot, then sign the driver with a self-made certificate. That works
  on the desktop as it is (no Secure Boot). The costs:
  - a "Test Mode" watermark in the corner of the screen;
  - some games' anti-cheat (Valorant/Vanguard, FACEIT, some Easy Anti-Cheat titles)
    won't run while it's on.

  `bcdedit /set testsigning off` undoes it.
- **Properly signed** (only if it's ever given to other people). Needs an EV
  code-signing certificate (a few hundred pounds a year) plus Microsoft attestation
  signing through Partner Center. Not worth it for one desktop.
- **Don't write the driver at all.** Install VB-Cable (free and already signed) on
  the desktop as the virtual device, and point the sender at it. No test mode. The
  sender and receiver are the same either way, so this fallback wastes nothing.

## Phases (one session each)

**Phase 0: the pipe, no driver.** Laptop, plus the desktop over SSH. The sender
loopback-captures the desktop's dead-but-active "Speakers" endpoint, sends it over
UDP, and the receiver plays it on the laptop. Covers the packet format, the jitter
buffer and basic drift handling. The sender is published self-contained, so nothing
needs installing on the desktop. **At the end of this phase the desktop's sound
already plays on the laptop**, before any kernel code exists.

**Phase 1: toolchain and the stock sample.** Matthew does the installs first, then a
short session.
- Install the WDK on the laptop. Check it supports VS 2026; if it doesn't, install
  VS 2022 Build Tools alongside.
- Build the unmodified `simpleaudiosample`.
- On the desktop: make a restore point, turn test signing on, reboot.
- Install the sample, see it in Sound settings, play into it, and uninstall it
  cleanly.

**Phase 2: make it Sound Transmitter.** Strip the microphone endpoint and rename the
endpoints, the INF and the hardware ID. Pick the formats (48 / 44.1 kHz; 16-bit,
24-bit and float; stereo). Make our own test certificate. Write install and
uninstall scripts. Set Sound Transmitter as the desktop's default output.

**Phase 3: join it up.** Point the sender at Sound Transmitter by name and start it
at logon (scheduled task). It has to survive the receiver being off, the endpoint
format changing, the driver being reinstalled, and sleep/wake. The receiver becomes
a tray app on the laptop.

**Phase 4: polish, as wanted.**
- A proper adaptive resampler.
- Latency and volume settings.
- Auto-discovery, so nobody types IP addresses.
- More than one receiver.
- Opus.
- A stats readout (buffer level, packet loss, drift in ppm).

## Matthew's jobs (admin, reboots, security settings)

- `git init` this folder, ideally before Phase 0's code lands.
- Phase 1: install the WDK on the laptop.
- Phase 1, on the desktop: system restore point, `bcdedit /set testsigning on`,
  reboot.
- Allow `st-recv` through the laptop's Windows Firewall (inbound UDP, Private
  network) when Windows asks.
- The first driver install on the desktop. It can go over SSH if you'd rather, with
  you near the desktop in case it needs a hard reset.

## Stopgaps while it's built

The desktop's front **Headphones** jack (same chip, separately amplified) shows as a
working output, if it's in reach of a speaker. The LG TV over HDMI works but isn't
the stereo. A ~£10 USB sound adapter also works. None of that stops this project
being the fun way. Phase 0 alone gets the desktop onto the stereo.

## Reference

- Microsoft Windows-driver-samples: `audio/simpleaudiosample`, `audio/sysvad`.
- Scream (open source): a virtual network sound card for Windows that does this
  exact job with the networking in the kernel. Worth reading for its driver changes.
- NAudio (.NET): `WasapiLoopbackCapture` and `WasapiOut` for the sender and receiver.
