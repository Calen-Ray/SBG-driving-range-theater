using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Logging;

namespace DrivingRangeTheater
{
    // One playable entry — the mp4 plus (optional) sidecar audio file with the same basename
    // but an audio extension. Sidecar audio is played through FMOD since the game has Unity's
    // native audio disabled; the mp4's embedded audio track is ignored at playback time.
    internal sealed class VideoEntry
    {
        public string VideoPath;
        public string AudioPath; // null if no sidecar found

        public string Name => Path.GetFileName(VideoPath);
    }

    // Scans every BepInEx plugin folder and collects playable videos. Three locations are
    // accepted, in priority order:
    //
    //   1. <plugin>/Videos/   (canonical — case-insensitive via filesystem)
    //   2. <plugin>/Video/    (accepted spelling; same treatment as #1)
    //   3. <plugin>/          (plugin root) — ONLY when the video has a same-basename audio
    //                          sidecar alongside it. The sidecar requirement keeps us from
    //                          grabbing incidental mp4 assets shipped for unrelated reasons.
    //
    // Each video is paired with its sidecar audio file if one exists. Dedupes by absolute path
    // so a content mod that happens to have both a Videos/ dir and root-level files doesn't
    // load the same clip twice. Sort order is alphabetical by filename — prefix with 01-, 02-,
    // ... to enforce sequence.
    internal static class VideoLibrary
    {
        // Formats Unity's VideoPlayer accepts on Windows (via Media Foundation).
        private static readonly string[] VideoExtensions =
            { ".mp4", ".webm", ".mov", ".ogv", ".mkv", ".m4v" };

        // Formats FMOD's createSound decodes reliably. Sidecar audio is matched by basename
        // (case-insensitive); first match wins in this order.
        private static readonly string[] AudioExtensions =
            { ".ogg", ".wav", ".mp3", ".m4a", ".aac", ".flac" };

        // Subfolder names we treat as "dedicated video dirs". Files inside are always picked up,
        // even without a sidecar. Windows filesystems are case-insensitive so "video" also
        // matches — the list here is for the benefit of case-sensitive filesystems if anyone
        // ever runs this cross-platform.
        private static readonly string[] DedicatedSubfolders = { "Videos", "Video", "video" };

        public static List<VideoEntry> Entries { get; private set; } = new List<VideoEntry>();

        // Convenience: just the video paths, preserved for callers that only want the list size.
        public static List<string> Videos => Entries.ConvertAll(e => e.VideoPath);

        public static void ScanAll(string pluginsRoot, ManualLogSource log)
        {
            Entries.Clear();
            if (!Directory.Exists(pluginsRoot))
            {
                log.LogWarning($"Plugins root '{pluginsRoot}' does not exist.");
                return;
            }

            // Dedupe by absolute (case-insensitive) path. Entries added first (in the order
            // the loops below visit them) win if a file is somehow reachable via two scans.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var pluginDir in Directory.GetDirectories(pluginsRoot))
            {
                // 1-2. Dedicated subfolder — any video file counts.
                foreach (var sub in DedicatedSubfolders)
                {
                    var dir = Path.Combine(pluginDir, sub);
                    if (!Directory.Exists(dir)) continue;

                    foreach (var file in Directory.GetFiles(dir))
                    {
                        if (!IsVideoFile(file)) continue;
                        AddEntry(file, requireSidecar: false, seen, log);
                    }
                }

                // 3. Plugin root — only pick up videos with a sidecar audio file. This keeps us
                // from grabbing mp4s shipped for other reasons (e.g. intro splash assets).
                foreach (var file in Directory.GetFiles(pluginDir))
                {
                    if (!IsVideoFile(file)) continue;
                    AddEntry(file, requireSidecar: true, seen, log);
                }
            }

            Entries = Entries.OrderBy(e => Path.GetFileName(e.VideoPath),
                                      System.StringComparer.OrdinalIgnoreCase).ToList();

            if (Entries.Count == 0)
            {
                log.LogInfo("No video files found (looked in plugin Videos/, Video/, and plugin roots with sidecar audio).");
                return;
            }

            int withAudio = Entries.Count(e => e.AudioPath != null);
            log.LogInfo($"Theater loaded {Entries.Count} video(s) ({withAudio} with sidecar audio):");
            foreach (var e in Entries)
            {
                var aud = e.AudioPath != null ? $" + {Path.GetFileName(e.AudioPath)}" : "  (silent — no sidecar)";
                log.LogInfo($"  - {e.Name}{aud}");
            }
        }

        private static void AddEntry(string file, bool requireSidecar, HashSet<string> seen, ManualLogSource log)
        {
            if (!seen.Add(Path.GetFullPath(file))) return;

            var audio = FindSidecarAudio(file);
            if (requireSidecar && audio == null)
            {
                // Silently skip — a random mp4 at the plugin root without an ogg sidecar is
                // almost certainly not a theater asset.
                return;
            }
            Entries.Add(new VideoEntry { VideoPath = file, AudioPath = audio });
        }

        private static bool IsVideoFile(string path)
            => System.Array.IndexOf(VideoExtensions, Path.GetExtension(path).ToLowerInvariant()) >= 0;

        private static string FindSidecarAudio(string videoPath)
        {
            var dir = Path.GetDirectoryName(videoPath);
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            foreach (var ext in AudioExtensions)
            {
                var candidate = Path.Combine(dir, baseName + ext);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}
