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

    // Scans every BepInEx plugin folder for a `Videos/` subdirectory, collects playable
    // videos, and pairs each with its audio sidecar if one exists next to it. Sort order
    // is alphabetical — prefix filenames with 01-, 02-, … to enforce sequence.
    internal static class VideoLibrary
    {
        // Formats Unity's VideoPlayer accepts on Windows (via Media Foundation).
        private static readonly string[] VideoExtensions =
            { ".mp4", ".webm", ".mov", ".ogv", ".mkv", ".m4v" };

        // Formats FMOD's createSound decodes reliably. Sidecar audio is matched by basename
        // (case-insensitive); first match wins in this order.
        private static readonly string[] AudioExtensions =
            { ".ogg", ".wav", ".mp3", ".m4a", ".aac", ".flac" };

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

            foreach (var pluginDir in Directory.GetDirectories(pluginsRoot))
            {
                var videosDir = Path.Combine(pluginDir, "Videos");
                if (!Directory.Exists(videosDir)) continue;

                foreach (var file in Directory.GetFiles(videosDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (System.Array.IndexOf(VideoExtensions, ext) < 0) continue;
                    Entries.Add(new VideoEntry
                    {
                        VideoPath = file,
                        AudioPath = FindSidecarAudio(file),
                    });
                }
            }

            Entries = Entries.OrderBy(e => Path.GetFileName(e.VideoPath),
                                      System.StringComparer.OrdinalIgnoreCase).ToList();

            if (Entries.Count == 0)
            {
                log.LogInfo("No video files found under any plugin's Videos/ folder.");
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
