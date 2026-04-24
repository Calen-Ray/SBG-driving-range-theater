# Changelog

## v0.1.2
- Audio drop-off extended from 32 m to 45 m. Full volume still starts at 6 m from the screen
  and falls off linearly to silent at 45 m — you can now hear the clip from further out on
  the range without it bleeding across the whole course.

## v0.1.1
- Scan both `Videos/` and `Video/` subfolders under each plugin package.
- Also accept videos placed directly in a plugin's root folder, provided each has a
  same-basename audio sidecar (`.ogg`/`.wav`/`.mp3`/`.m4a`/`.aac`/`.flac`). The sidecar
  requirement keeps the scanner from picking up unrelated mp4s shipped by other plugins.
- Dedupe by path so a mod with both a `Videos/` dir and root-level sidecared videos doesn't
  produce duplicate playlist entries.

## v0.1.0
- Initial release.
- Replaces the driving-range leaderboard TV with a video player.
- Next / back / volume slider UI.
- Content mods drop video files into a sibling `Videos/` folder to extend the playlist.
