using System.Text;

namespace RawScoreBatchAnalyzer.Analysis;

internal sealed record OsrHeader(
    int Ruleset,
    int Version,
    string BeatmapHash,
    string Player,
    ushort Count300,
    ushort Count100,
    ushort Count50,
    ushort CountGeki,
    ushort CountKatu,
    ushort CountMiss,
    int TotalScore,
    ushort MaxCombo,
    bool Perfect,
    int Mods,
    long Timestamp);

internal static class OsrHeaderReader
{
    public static OsrHeader Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        var ruleset = reader.ReadByte();
        var version = reader.ReadInt32();
        var beatmapHash = ReadString(reader);
        var player = ReadString(reader);
        _ = ReadString(reader); // replay hash
        var count300 = reader.ReadUInt16();
        var count100 = reader.ReadUInt16();
        var count50 = reader.ReadUInt16();
        var countGeki = reader.ReadUInt16();
        var countKatu = reader.ReadUInt16();
        var countMiss = reader.ReadUInt16();
        var totalScore = reader.ReadInt32();
        var maxCombo = reader.ReadUInt16();
        var perfect = reader.ReadBoolean();
        var mods = reader.ReadInt32();
        _ = ReadString(reader); // life bar graph
        var timestamp = reader.ReadInt64();

        return new OsrHeader(
            ruleset,
            version,
            beatmapHash,
            player,
            count300,
            count100,
            count50,
            countGeki,
            countKatu,
            countMiss,
            totalScore,
            maxCombo,
            perfect,
            mods,
            timestamp);
    }

    private static string ReadString(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        if (marker == 0)
            return string.Empty;

        if (marker != 0x0b)
            throw new InvalidDataException($"Invalid osu string marker: {marker}");

        var length = ReadUleb128(reader);
        var bytes = reader.ReadBytes((int)length);
        return Encoding.UTF8.GetString(bytes);
    }

    private static ulong ReadUleb128(BinaryReader reader)
    {
        ulong result = 0;
        var shift = 0;

        while (true)
        {
            var value = reader.ReadByte();
            result |= (ulong)(value & 0x7f) << shift;
            if ((value & 0x80) == 0)
                return result;

            shift += 7;
        }
    }
}
