# DrivingRangeTheater

Turns the driving-range leaderboard TV into a video player. Hit the cycle buttons (forward / back)
to flip between clips; each client controls their own playback volume from the pause menu.

This mod is a **framework** — it doesn't ship with any videos. Pair it with content mods, or drop
your own files into `Videos/` under any plugin folder.

## Install

Via [r2modman](https://thunderstore.io/c/super-battle-golf/) or Thunderstore Mod Manager —
search for **Cray-DrivingRangeTheater** and install, plus any content packs you want.

Manual install: drop `DrivingRangeTheater.dll` into
`<r2modman profile>/BepInEx/plugins/Cray-DrivingRangeTheater/`.

Requires [BepInExPack 5.4.2305](https://thunderstore.io/c/super-battle-golf/p/BepInEx/BepInExPack/).
Every player in a lobby needs the same mods installed (including the same video content) — cycle
state is server-authoritative and replicated via Mirror.

## Usage

- **Original button** → next video.
- **Cloned button (added next to it)** → previous video.
- **Pause menu** while on the driving range → volume slider (client-local).

## Adding videos

Drop video files into the `Videos/` folder of **any** plugin package. The framework scans
`<r2modman profile>/BepInEx/plugins/*/Videos/` at startup and sorts the files alphabetically.

Recommended format: **MP4 (H.264)** — most widely compatible with Unity's VideoPlayer. Also
accepted: `.webm`, `.mov`, `.ogv`, `.mkv`, `.m4v` (compatibility varies by platform).

Prefix filenames with `01-`, `02-`, … to control playback order.

## Authoring a content mod

Ship a Thunderstore package depending on `Cray-DrivingRangeTheater`:

```
Cray-MyVideos/
├── manifest.json
├── icon.png
├── README.md
└── Videos/
    ├── 01-intro.mp4
    ├── 02-highlights.mp4
    └── ...
```

The framework scans your `Videos/` folder on its next startup; no code needed.

## How it works

- **Video scan** — at plugin Awake, walks `BepInEx/plugins/*/Videos/*` for supported formats.
- **Screen takeover** — a Harmony prefix on `DrivingRangeStaticCameraManager.ApplyCurrentCameraIndex`
  swaps the screen's albedo/emission textures to a `VideoPlayer`'s `RenderTexture` instead of
  activating the vanilla in-scene cameras.
- **Forward button** — the vanilla `nextCameraButton` remains wired to `CmdCycleNextCameraForAllClients`.
  The SyncVar `currentCameraIndex` still drives playback — we just reinterpret the index against
  the video list instead of the camera list.
- **Back button** — at scene-start time we clone the forward button's GameObject, offset it, and
  swap its `DrivingRangeNextCameraButton` component for `DrivingRangeBackCameraButton`, which
  sends a custom `TheaterCycleMsg{direction=-1}` Mirror message. A server handler updates the
  SyncVar accordingly.
- **Volume slider** — a simple Unity UI overlay Canvas, shown while the pause menu is open and
  the theater is active. The slider writes to a BepInEx `ConfigEntry<float>` so the value
  persists; `VideoPlayer.SetDirectAudioVolume(0, v)` updates playback live.

## Building from source

Requirements: .NET SDK 7.0+, a Super Battle Golf install (Steam default path is auto-detected).

```
git clone https://github.com/Calen-Ray/SBG-driving-range-theater.git
cd SBG-driving-range-theater
dotnet build -c Release
```

The build copies the DLL into your r2modman `Default` profile automatically.

### Packaging for Thunderstore

```
pwsh tools/package.ps1
```

Produces `artifacts/Cray-DrivingRangeTheater-<version>.zip` ready to upload.

## License

MIT — see [LICENSE](LICENSE).
