using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NMEA2000Analyzer
{
    public class RecentFileEntry
    {
        public required string FilePath { get; set; }
        public DateTime LastOpened { get; set; }
    }

    public static class RecentFilesManager
    {
        private const int MaxRecentFiles = 10;

        private static readonly string BaseFolder =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "NMEA2000Analyzer");

        private static readonly string FilePath =
            Path.Combine(BaseFolder, "recent-files.json");

        private static List<RecentFileEntry> _cache;

        public static List<RecentFileEntry> Load()
        {
            if (_cache != null)
                return _cache;

            if (!File.Exists(FilePath))
            {
                _cache = new List<RecentFileEntry>();
                return _cache;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                _cache = JsonSerializer.Deserialize<List<RecentFileEntry>>(json)
                         ?? new List<RecentFileEntry>();
            }
            catch
            {
                _cache = new List<RecentFileEntry>();
            }

            return _cache;
        }

        public static void RegisterFileOpen(string path)
        {
            var list = Load();

            var existing = list.FirstOrDefault(x =>
                x.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.LastOpened = DateTime.Now;
            }
            else
            {
                list.Insert(0, new RecentFileEntry
                {
                    FilePath = path,
                    LastOpened = DateTime.Now
                });
            }

            _cache = list
                .OrderByDescending(x => x.LastOpened)
                .Take(MaxRecentFiles)
                .ToList();

            Save();
        }

        private static void Save()
        {
            Directory.CreateDirectory(BaseFolder);

            var json = JsonSerializer.Serialize(_cache,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(FilePath, json);
        }
    }
}
