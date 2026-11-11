using System.Text;

namespace LazerReplayCompare;

/// <summary>
/// osu!stable's osu!.db (the beatmap cache) and collection.db, read as laid out in
/// osu!'s "Legacy database file structure". Only mania difficulties are kept.
/// </summary>
public static class StableDatabase
{
    private sealed class Reader(Stream stream) : BinaryReader(stream, Encoding.UTF8)
    {
        /// <summary>0x00 for none, or 0x0b, a ULEB128 length and UTF-8 bytes</summary>
        public string Str()
        {
            var tag = ReadByte();
            if (tag == 0x00) return "";
            if (tag != 0x0b) throw new InvalidDataException($"bad string tag 0x{tag:x2} at {BaseStream.Position - 1}");
            var length = 0;
            var shift = 0;
            byte b;
            do
            {
                b = ReadByte();
                length |= (b & 0x7f) << shift;
                shift += 7;
            } while ((b & 0x80) != 0);
            return Encoding.UTF8.GetString(ReadBytes(length));
        }
    }

    /// <summary>The whole file at once, opened alongside osu! (which may hold it or write it)</summary>
    private static MemoryStream Load(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var copy = new MemoryStream((int)Math.Min(f.Length, int.MaxValue));
        f.CopyTo(copy);
        copy.Position = 0;
        return copy;
    }

    public static List<MapInfo> ReadMaps(string dir)
    {
        var maps = new List<MapInfo>();
        var songs = SongsFolder(dir);
        using var r = new Reader(Load(Path.Combine(dir, "osu!.db")));
        var version = r.ReadInt32();
        r.ReadInt32(); // folders
        r.ReadBoolean(); // account unlocked
        r.ReadInt64(); // unlock date
        r.Str(); // player
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            if (version < 20191106) r.ReadInt32(); // entry size
            var m = new MapInfo
            {
                Artist = r.Str(),
                ArtistUnicode = r.Str(),
                Title = r.Str(),
                TitleUnicode = r.Str(),
                Mapper = r.Str(),
                Version = r.Str(),
                Clients = "stable",
            };
            r.Str(); // audio
            m.Md5 = r.Str();
            var file = r.Str();
            m.Status = MapStatus.FromStable(r.ReadByte());
            var circles = r.ReadInt16();
            var sliders = r.ReadInt16();
            r.ReadInt16(); // spinners
            r.ReadInt64(); // modified
            float Diff() => version < 20140609 ? r.ReadByte() : r.ReadSingle();
            Diff(); // AR
            var cs = Diff();
            m.Hp = Diff();
            m.Od = Diff();
            r.ReadDouble(); // slider velocity
            if (version >= 20140609)
            {
                for (var mode = 0; mode < 4; mode++)
                {
                    var pairs = r.ReadInt32();
                    for (var p = 0; p < pairs; p++)
                    {
                        r.ReadByte(); // 0x08
                        var mods = r.ReadInt32();
                        // 0x0c: a float (since 20250107), 0x0d: a double
                        var stars = r.ReadByte() == 0x0c ? r.ReadSingle() : r.ReadDouble();
                        if (mode == 3 && mods == 0) m.Stars = stars;
                    }
                }
            }
            r.ReadInt32(); // drain time (s)
            m.LengthMs = r.ReadInt32();
            r.ReadInt32(); // preview
            var points = r.ReadInt32();
            var timing = new List<(double BeatLength, double Offset)>();
            for (var p = 0; p < points; p++)
            {
                var beatLength = r.ReadDouble();
                var offset = r.ReadDouble();
                if (r.ReadBoolean() && beatLength > 0) timing.Add((beatLength, offset));
            }
            m.BeatmapId = r.ReadInt32();
            m.SetId = r.ReadInt32();
            r.ReadInt32(); // thread
            r.ReadBytes(4); // grades
            r.ReadInt16(); // local offset
            r.ReadSingle(); // stack leniency
            var ruleset = r.ReadByte();
            m.Source = r.Str();
            m.Tags = r.Str();
            r.ReadInt16(); // online offset
            r.Str(); // title font
            r.ReadBoolean(); // unplayed
            r.ReadInt64(); // last played
            r.ReadBoolean(); // osz2
            var folder = r.Str();
            r.ReadInt64(); // last checked
            r.ReadBytes(5); // ignore sound / skin, disable storyboard / video, visual override
            if (version < 20140609) r.ReadInt16();
            r.ReadInt32(); // last modification
            r.ReadByte(); // mania scroll speed

            if (ruleset != 3 || m.Md5.Length == 0) continue;
            m.Keys = (int)Math.Round(cs);
            m.Objects = circles + sliders;
            m.Holds = sliders;
            m.Bpm = MainBpm(timing, m.LengthMs);
            m.ChartPath = songs.Length > 0 && file.Length > 0 ? Path.Combine(songs, folder, file) : "";
            maps.Add(m);
        }
        return maps;
    }

    /// <summary>The BPM held longest over the song (timing points give milliseconds per beat)</summary>
    private static double MainBpm(List<(double BeatLength, double Offset)> timing, double length)
    {
        if (timing.Count == 0) return 0;
        var held = new Dictionary<double, double>();
        for (var i = 0; i < timing.Count; i++)
        {
            var bpm = Math.Round(60000 / timing[i].BeatLength, 1);
            var end = i + 1 < timing.Count ? timing[i + 1].Offset : Math.Max(length, timing[i].Offset + 1);
            held[bpm] = held.GetValueOrDefault(bpm) + Math.Max(0, end - timing[i].Offset);
        }
        return held.MaxBy(p => p.Value).Key;
    }

    public static List<OsuCollection> ReadCollections(string dir)
    {
        var list = new List<OsuCollection>();
        var path = Path.Combine(dir, "collection.db");
        if (!File.Exists(path)) return list;
        using var r = new Reader(Load(path));
        r.ReadInt32(); // version
        var count = r.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var name = r.Str();
            var size = r.ReadInt32();
            var md5s = new List<string>(size);
            for (var j = 0; j < size; j++) md5s.Add(r.Str());
            list.Add(new OsuCollection(name, "stable", md5s));
        }
        return list;
    }

    /// <summary>The Songs folder: BeatmapDirectory in the user's osu!.&lt;user&gt;.cfg (absolute, or under the install), else Songs</summary>
    public static string SongsFolder(string dir)
    {
        try
        {
            var cfg = Directory.EnumerateFiles(dir, "osu!.*.cfg")
                .Where(f => !Path.GetFileName(f).Equals("osu!.cfg", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f).Equals($"osu!.{Environment.UserName}.cfg", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            if (cfg != null)
            {
                foreach (var line in File.ReadLines(cfg))
                {
                    var eq = line.IndexOf('=');
                    if (eq < 0 || !line[..eq].Trim().Equals("BeatmapDirectory", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line[(eq + 1)..].Trim();
                    if (value.Length > 0) return Path.IsPathRooted(value) ? value : Path.Combine(dir, value);
                }
            }
        }
        catch (IOException)
        {
            // no config: the default folder
        }
        return Path.Combine(dir, "Songs");
    }
}
