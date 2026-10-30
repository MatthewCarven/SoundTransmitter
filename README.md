# Sound Transmitter

Plays the desktop's sound on the laptop (and so on the stereo) over the LAN.

- **st-send** runs on the desktop. It records whatever a playback device is
  playing (WASAPI loopback, like OBS's "desktop audio") and streams it as UDP.
- **st-recv** runs on the laptop. It buffers the stream, corrects for the two
  machines' clocks drifting apart, and plays it on a local device.

Phase 0 of [PLAN.md](PLAN.md). There's no driver yet: the sender captures an
existing device. Phase 1 onwards adds a proper "Sound Transmitter" virtual device.

## Use

**Desktop:** set the default playback device to *Speakers (3- High Definition
Audio Device)*, the dead rear jack, so the desktop itself stays silent. Then run
`C:\Users\Admin\SoundTransmitter\send.cmd`.

**Laptop:** run `listen.cmd` in this folder.

Either can start first. The receiver says hello once a second; the sender only
streams while it hears one. Stop either with Ctrl+C.

The sender captures the default device as it was when it started. If you change
the default, restart it.

## Options

```
st-send --to <host[:port]> [--device <name>] [--format s16|f32] [--port 47801]
        [--tone <hz>] [--tone-db -30] [--seconds <n>] [--log <file>]
st-send --list

st-recv --from <host[:port]> [--device <name>] [--latency 100] [--max-latency 300]
        [--port 47800] [--mute] [--check-tone <hz>] [--seconds <n>] [--log <file>]
st-recv --list
```

`--device` takes any unique part of a device's name, as shown by `--list`.

`--latency` is the receiver's starting buffer. Each underrun raises it by 30 ms,
up to `--max-latency`, so the target settles at whatever the network needs.

The default is 100 ms because of the laptop's Wi-Fi. It's on 2.4 GHz, and every
minute or two Windows has the card scan the other channels, which goes silent
for ~55 ms per channel; the router holds the packets meanwhile (measured
2026-10-30: 27 silences of 42–68 ms in 10 minutes, nothing lost). If the laptop
goes onto Ethernet or 5 GHz, or the scanning is tamed, try `--latency 40`.

## Reading the receiver's log

```
playing | buffer 100.1 / 100 ms | drift +57 ppm | 44100 Hz in | packets 10465 (+924) lost 0 late 0 | underruns 0 resyncs 0
```

- **buffer**: the smoothed amount held, then the current target. The first
  should sit on the second.
- **drift**: how much faster (+) or slower than its nominal rate the playback is
  being run, to keep the buffer on target. That is the two clocks' disagreement,
  typically tens of ppm.
- **lost**: packets that never came.
- **late**: packets that came after their moment had been played as silence.
- **underruns**: times the buffer ran dry, which you hear as a short dropout.
  Playback pauses until it's back at target, and the target goes up 30 ms.
- **resyncs**: times it was far over target and jumped forward.

Between the 5-second lines, the receiver logs `network gap: N ms` whenever no
packets came for over 40 ms. The sender logs `capture gap` when the desktop
itself paused, and `capture overflow` if Windows ever dropped audio before the
sender read it. Comparing the two logs tells you which end a dropout came from.
The desktop's clock runs about 3.5 minutes fast, so allow for that when matching
times.

## Testing without anyone listening

```
desktop>  st-send --to ID10TError-Laptop1 --device Speakers --tone 440 --seconds 700
laptop>   st-recv --from id10terror-desktop --mute --check-tone 440 --seconds 600
```

The sender plays a quiet tone into the dead jack (silent in the room). The
receiver plays silence but checks every 10 ms of the tone it would have played,
and counts any block that isn't a clean sine as a glitch.

## Build

```
dotnet test SoundTransmitter.slnx
dotnet publish src/SoundTransmitter.Send -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish/st-send
dotnet publish src/SoundTransmitter.Recv -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=none -o publish/st-recv
```

The sender is self-contained because the desktop has no .NET. Copy
`publish/st-send/st-send.exe` and `desktop/send.cmd` to
`C:\Users\Admin\SoundTransmitter\` on the desktop
(`scp <file> Admin@id10terror-desktop:SoundTransmitter/`).

## Layout

- `src/SoundTransmitter.Core`: the packet format, sample conversion, packetizer,
  jitter buffer (`FrameRing`, `Playout`), resampler (64-tap windowed sinc), drift
  controller (PI), tone checker, device and network helpers.
- `src/SoundTransmitter.Send`, `src/SoundTransmitter.Recv`: the two programs.
- `tests/`: unit tests, plus a simulated network (jitter, reordering, loss,
  stalls, ±100 ppm drift) that runs the receiver for ten simulated minutes.
