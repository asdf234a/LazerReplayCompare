namespace LazerReplayCompare;

/// <summary>One mania difficulty from osu!'s beatmap library (lazer's client.realm or stable's osu!.db).</summary>
public sealed class MapInfo
{
    public string Md5 { get; set; } = "";
    public string Title { get; set; } = "";
    public string TitleUnicode { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ArtistUnicode { get; set; } = "";
    public string Version { get; set; } = "";
    public string Mapper { get; set; } = "";
    public string Source { get; set; } = "";
    public string Tags { get; set; } = "";
    public int Keys { get; set; }
    public float Od { get; set; }
    public float Hp { get; set; }
    /// <summary>Star rating without mods, or a negative value when osu! hasn't worked it out</summary>
    public double Stars { get; set; } = -1;
    public double LengthMs { get; set; }
    public double Bpm { get; set; }
    /// <summary>Notes plus hold notes as osu! counts its objects</summary>
    public int Objects { get; set; }
    /// <summary>Hold notes among them</summary>
    public int Holds { get; set; }
    public int BeatmapId { get; set; }
    public int SetId { get; set; }
    /// <summary>"lazer", "stable" or "lazer+stable"</summary>
    public string Clients { get; set; } = "";
    public string ChartPath { get; set; } = "";
    /// <summary>The background image, when known without reading the chart (lazer); stable's comes from the .osu file</summary>
    public string BackgroundPath { get; set; } = "";
    /// <summary>
    /// osu!'s ranked status: "ranked", "approved", "qualified", "loved", "pending", "wip",
    /// "graveyard", "none" (not submitted, or edited locally), or "" when unknown. osu!stable
    /// doesn't tell pending, WIP and graveyard apart: those are all "pending".
    /// </summary>
    public string Status { get; set; } = "";

    public string TitleOriginal => TitleUnicode.Length > 0 ? TitleUnicode : Title;
    public string ArtistOriginal => ArtistUnicode.Length > 0 ? ArtistUnicode : Artist;
    public string TitleEnglish => Title.Length > 0 ? Title : TitleUnicode;
    public string ArtistEnglish => Artist.Length > 0 ? Artist : ArtistUnicode;
}

/// <summary>osu!'s ranked statuses, and the groups the window filters by</summary>
public static class MapStatus
{
    /// <summary>lazer's BeatmapOnlineStatus</summary>
    public static string FromLazer(int status) => status switch
    {
        1 => "ranked",
        2 => "approved",
        3 => "qualified",
        4 => "loved",
        0 => "pending",
        -1 => "wip",
        -2 => "graveyard",
        -3 or -4 => "none",
        _ => "",
    };

    /// <summary>osu!.db's ranked status byte</summary>
    public static string FromStable(byte status) => status switch
    {
        4 => "ranked",
        5 => "approved",
        6 => "qualified",
        7 => "loved",
        2 => "pending",
        1 => "none",
        _ => "",
    };

    /// <summary>Scores go to a leaderboard: ranked, approved, qualified, loved</summary>
    public static bool HasLeaderboard(string status) => status is "ranked" or "approved" or "qualified" or "loved";

    /// <summary>Pending, WIP, graveyard (and not submitted)</summary>
    public static bool IsUnranked(string status) => status is "pending" or "wip" or "graveyard" or "none";

    public static string Label(string status) => status switch
    {
        "ranked" => "Ranked",
        "approved" => "Approved",
        "qualified" => "Qualified",
        "loved" => "Loved",
        "pending" => "Pending",
        "wip" => "WIP",
        "graveyard" => "Graveyard",
        "none" => "Not submitted",
        _ => "Status unknown",
    };
}

/// <summary>A collection made in osu! (read only here)</summary>
public sealed record OsuCollection(string Name, string Client, IReadOnlyList<string> Md5s);
