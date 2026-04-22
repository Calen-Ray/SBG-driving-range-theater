# DrivingRangeTheater

Does your lobby host step away for a bathroom break between games? Do you and your friends often wait for someone to finish something AFK between furious 18-hole battles? This is the mod for you. Add a mod to r2-modman with a /videos folder and it's content will be displayed in beautiful big picture cinema to enjoy as a group while you wait for whoever to finish making dinner, using the bathroom, or having appendicitis (looking at you Baustin).

Don't know how to make a mod? drop a video file over at http://calenray.com/SBG-driving-range-theater-mod-maker/ and it will automatically turn the video file into a compatible mod to view from your very own golf drive-in. Be aware that it's not a quick process to re-encode video files and assemble mod-packages entirely client side (in browser). I am a busy fella so I may or may not get around to efficiency improvements, feel free to open a pull request for the site as it's open source to allow other developers to add improvements! (github for contributors: https://github.com/Calen-Ray/SBG-driving-range-theater-mod-maker)

Speaking of being a busy fella, here is chat gpt rewriting my three paragraph explaination of the mod framework into something more organized. Cheers. 

Turns the driving-range leaderboard TV into a video player. Hit the cycle buttons (forward / back)
to flip between clips; each client controls their own playback volume from the pause menu.

This mod is a framework. It does not ship with any videos. Pair it with content mods, or drop
your own files into `Videos/` under any plugin folder.

## Install

Via [r2modman](https://thunderstore.io/c/super-battle-golf/) or Thunderstore Mod Manager:
search for **Cray-DrivingRangeTheater** and install, plus any content packs you want.

Manual install: drop `DrivingRangeTheater.dll` into
`<r2modman profile>/BepInEx/plugins/Cray-DrivingRangeTheater/`.

Requires [BepInExPack 5.4.2305](https://thunderstore.io/c/super-battle-golf/p/BepInEx/BepInExPack/).
Every player in a lobby needs the same mods installed, including the same video content, because
cycle state is server-authoritative and replicated via Mirror.

## Usage

- Original button: next video.
- Cloned button beside it: previous video.
- Pause menu while on the driving range: client-local `Screen res`, `Theater volume`, and
  `Screen fit` controls.
- If the screen trims the edges of a clip, adjust `OverscanCompensation` in
  `BepInEx/config/cray.drivingrangetheater.cfg`.

## Adding videos

Drop video files into **any** of the following locations under a plugin package — the framework
scans all of them at startup and sorts across all matches alphabetically, deduping by path:

- `<plugin>/Videos/` — canonical.
- `<plugin>/Video/` — also accepted (singular).
- `<plugin>/` (plugin root) — **only if** a same-basename audio sidecar
  (`.ogg`/`.wav`/`.mp3`/`.m4a`/…) sits next to it. The sidecar requirement keeps the scanner
  from picking up unrelated mp4s a plugin ships for other reasons.

### Video

Recommended format: **MP4 (H.264 8-bit / yuv420p)**. This is the safest format for Unity's
VideoPlayer backend on Windows. Also accepted by extension: `.webm`, `.mov`, `.ogv`, `.mkv`,
`.m4v`, but decode support varies. H.264 10-bit (`High 10`, `yuv420p10le`) will fail with
`VideoPlayer cannot play url`.

Transcode problematic files with:

```bash
ffmpeg -y -i input.mp4 -c:v libx264 -profile:v high -pix_fmt yuv420p -crf 22 -preset veryfast \
       -c:a copy output.mp4
```

### Audio

Super Battle Golf has Unity's native audio disabled project-wide, so the `VideoPlayer`'s built-in
audio paths are silent. The framework plays audio from a sidecar file next to each video through
FMOD.

Ship an audio file alongside every video with the same basename, for example
`1 Gwimbly.mp4` -> `1 Gwimbly.ogg`. Accepted extensions, first match wins:
`.ogg`, `.wav`, `.mp3`, `.m4a`, `.aac`, `.flac`. If no sidecar is found, the video still plays
silently.

Extract `.ogg` audio from an existing mp4 with:

```bash
ffmpeg -y -i input.mp4 -vn -c:a libvorbis -q:a 4 input.ogg
```

Audio is played as a **3D FMOD sound** positioned at the theater screen, not as a global 2D mix.
Sync is maintained by a 2-second drift check against `VideoPlayer.time`; the framework corrects
mismatches greater than 250 ms with `channel.setPosition()`.

Prefix filenames with `01-`, `02-`, and so on to control playback order.

## Authoring a content mod

Ship a Thunderstore package depending on `Cray-DrivingRangeTheater`:

```text
Cray-MyVideos/
|-- manifest.json
|-- icon.png
|-- README.md
`-- Videos/
    |-- 01-intro.mp4
    |-- 02-highlights.mp4
    `-- ...
```

The framework scans your `Videos/` folder on next startup. No code is needed.

## How it works

- Video scan: at plugin `Awake`, walks `BepInEx/plugins/*/Videos/*` for supported formats.
- Screen takeover: a Harmony prefix on `DrivingRangeStaticCameraManager.ApplyCurrentCameraIndex`
  suppresses vanilla camera activation and drives the theater manually. Active video decodes into
  an offscreen RT, then gets composited into the vanilla driving-range screen RT with a configurable
  overscan-safe margin so the existing in-scene display path keeps working.
- Screen resolution: the mod upgrades the original shared
  `match_setup_camera_render_texture` in place and rebuilds the six vanilla driving-range camera
  caches against the chosen size. Supported client-local options are `1024`, `1536`, and `2048`.
  This keeps the hidden screen binding intact while improving final display sharpness.
- Forward button: the vanilla `nextCameraButton` remains wired to
  `CmdCycleNextCameraForAllClients`. The SyncVar `currentCameraIndex` still drives playback; the
  mod just reinterprets the index against the video list instead of the camera list.
- Back button: at scene-start time the mod clones the forward button's GameObject, offsets it, and
  swaps its `DrivingRangeNextCameraButton` component for `DrivingRangeBackCameraButton`, which
  sends `TheaterCycleMsg { direction = -1 }`. A server handler updates the SyncVar accordingly.
- Volume slider: a small pause-menu child panel shown while the pause menu is open and the theater
  is active. The sliders write to BepInEx `ConfigEntry<float>` values so volume and screen-fit
  changes persist.
- Audio playback: FMOD sidecar audio starts when the active clip actually begins video playback,
  then stays synced against the `VideoPlayer` clock with periodic drift correction.

## Building from source

Requirements: .NET SDK 7.0+, a Super Battle Golf install.

```bash
git clone https://github.com/Calen-Ray/SBG-driving-range-theater.git
cd SBG-driving-range-theater
dotnet build -c Release
```

The build copies the DLL into your r2modman `Default` profile automatically.

### Packaging for Thunderstore

```bash
pwsh tools/package.ps1
```

Produces `artifacts/Cray-DrivingRangeTheater-<version>.zip` ready to upload.

## Releasing

Automated via [`.github/workflows/release.yml`](.github/workflows/release.yml) — publishing a
GitHub Release uploads the attached zip to Thunderstore.

**One-time setup.** Add a `THUNDERSTORE_TOKEN` repository secret (Settings -> Secrets and
variables -> Actions). The token comes from
[thunderstore.io/settings/teams/](https://thunderstore.io/settings/teams/) under the `Cray` team.

**Cut a release:**

```bash
# 1. Bump version_number in manifest.json and add a CHANGELOG.md entry.
# 2. Commit + tag + push.
git commit -am "Release v0.2.0"
git tag v0.2.0
git push --follow-tags

# 3. Build the zip locally (CI can't build — hosted runners don't have the game DLLs).
pwsh tools/package.ps1

# 4. Create the GitHub Release with the zip attached; the workflow publishes on release.published.
gh release create v0.2.0 artifacts/Cray-*-*.zip --notes-file CHANGELOG.md
```

## License

MIT, see [LICENSE](LICENSE).
