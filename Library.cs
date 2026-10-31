using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShibaPlayer;

/// <summary>One MP3/M4A file and its tags.</summary>
public sealed class Track
{
    public required string Path { get; init; }
    public string Title { get; set; } = "";
    public bool TitleIsFileName { get; set; } // no title tag; the first thing a lookup may replace
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public uint Year { get; set; }
    public uint Number { get; set; }
    public TimeSpan Duration { get; init; }
    public bool HasCover { get; set; }
    public bool IsPlaying { get; set; }

    private ImageSource? _cover;
    // ponytail: covers load on first view and stay cached; fine for a few thousand songs.
    public ImageSource? Cover => _cover ??= HasCover ? Library.LoadCover(Path) : null;
    public void ForgetCover() => _cover = null;

    public string AlbumName => Album.Length > 0 ? Album : "Unknown album";
    public string ArtistName => Artist.Length > 0 ? Artist : "Unknown artist";
    public string GenreGroup => Genres.Match(Genre);
    public string LengthText => $"{(int)Duration.TotalMinutes}:{Duration.Seconds:D2}";
    public bool NeedsMetadata => Artist.Length == 0 || Album.Length == 0 || Genre.Length == 0 || !HasCover;
}

/// <summary>An album, genre or playlist card.</summary>
public sealed record Group(string Name, string Sub, List<Track> Tracks)
{
    public ImageSource? Cover => Tracks.FirstOrDefault(t => t.HasCover)?.Cover;
}

/// <summary>Genre matching: folds the many spellings in the wild ("Hip-Hop/Rap", "(17)", "Synthpop") into a few shelves.</summary>
public static class Genres
{
    private static readonly string[] Id3v1 = "Blues,Classic Rock,Country,Dance,Disco,Funk,Grunge,Hip-Hop,Jazz,Metal,New Age,Oldies,Other,Pop,R&B,Rap,Reggae,Rock,Techno,Industrial,Alternative,Ska,Death Metal,Pranks,Soundtrack,Euro-Techno,Ambient,Trip-Hop,Vocal,Jazz+Funk,Fusion,Trance,Classical,Instrumental,Acid,House,Game,Sound Clip,Gospel,Noise,AlternRock,Bass,Soul,Punk,Space,Meditative,Instrumental Pop,Instrumental Rock,Ethnic,Gothic,Darkwave,Techno-Industrial,Electronic,Pop-Folk,Eurodance,Dream,Southern Rock,Comedy,Cult,Gangsta,Top 40,Christian Rap,Pop/Funk,Jungle,Native American,Cabaret,New Wave,Psychadelic,Rave,Showtunes,Trailer,Lo-Fi,Tribal,Acid Punk,Acid Jazz,Polka,Retro,Musical,Rock & Roll,Hard Rock".Split(',');

    // ponytail: ordered keyword buckets, first match wins; swap for a real taxonomy if it mis-shelves too much.
    private static readonly (string Name, Regex Re)[] Buckets =
    [
        ("Metal", new(@"metal|grind|core\b")),
        ("Hip-Hop", new(@"hip.?hop|rap|trap|gangsta|drill")),
        ("Electronic", new(@"electr|techno|house|trance|dance|edm|step|drum|jungle|ambient|rave|synth|idm|lo-?fi")),
        ("R&B", new(@"r&b|rnb|rhythm|soul|funk")),
        ("Jazz", new(@"jazz|swing|bebop|fusion")),
        ("Classical", new(@"classical|baroque|opera|orchestr|symphon")),
        ("Country", new(@"country|bluegrass")),
        ("Folk", new(@"folk|acoustic|singer")),
        ("Blues", new(@"blues")),
        ("Reggae", new(@"reggae|ska|dub\b|dancehall")),
        ("Soundtrack", new(@"soundtrack|score|film|game|anime|musical|showtune")),
        ("Rock", new(@"rock|punk|grunge|indie|alt")),
        ("Pop", new(@"pop|top 40")),
    ];

    public static string Match(string? raw)
    {
        var g = (raw ?? "").Trim();
        var m = Regex.Match(g, @"^\((\d+)\)|^(\d+)$"); // ID3v1 number, "(17)" or "17"
        if (m.Success && int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) is var i && i < Id3v1.Length)
            g = Id3v1[i];
        if (g.Length == 0) return "Unknown";
        var lower = g.ToLowerInvariant();
        foreach (var (name, re) in Buckets)
            if (re.IsMatch(lower)) return name;
        return "Other";
    }
}

/// <summary>What a metadata source knows about one song.</summary>
public sealed record SongHit(string Title, string Artist, string Album, string Genre, uint Year, uint Number, string ArtUrl,
    string ThumbUrl = "", string Source = "", long DeezerAlbumId = 0, long DeezerTrackId = 0)
{
    public string Details => string.Join("  ·  ", new[] { Year > 0 ? Year.ToString() : "", Genre, Source }.Where(s => s.Length > 0));
}

public static class Library
{
    public static readonly string[] Extensions = [".mp3", ".m4a", ".webm"]; // .webm = the "Highest" download quality

    /// <summary>Reads every MP3/M4A under <paramref name="root"/>. Unreadable files are counted, not listed.</summary>
    public static List<Track> Scan(string root, out int unreadable)
    {
        var tracks = new List<Track>();
        unreadable = 0;
        var files = Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));
        foreach (var f in files)
        {
            try { tracks.Add(Read(f)); }
            catch { unreadable++; }
        }
        return tracks;
    }

    public static Track Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var side = WebmTags.Get(path);
        if (side != null || WebmTags.Applies(path))
            return new Track
            {
                Path = path,
                Title = side?.Title ?? System.IO.Path.GetFileNameWithoutExtension(path),
                TitleIsFileName = side?.Title == null,
                Artist = side?.Artist ?? "",
                Album = side?.Album ?? "",
                Genre = side?.Genre ?? "",
                Year = side?.Year ?? 0,
                Number = side?.Number ?? 0,
                HasCover = File.Exists(WebmTags.CoverPath(path)),
                Duration = file.Properties.Duration,
            };
        return new Track
        {
            Path = path,
            Title = string.IsNullOrWhiteSpace(tag.Title) ? System.IO.Path.GetFileNameWithoutExtension(path) : tag.Title,
            TitleIsFileName = string.IsNullOrWhiteSpace(tag.Title),
            Artist = tag.JoinedPerformers ?? "",
            Album = tag.Album ?? "",
            Genre = tag.JoinedGenres ?? "",
            Year = tag.Year,
            Number = tag.Track,
            HasCover = tag.Pictures.Length > 0,
            Duration = file.Properties.Duration,
        };
    }

    public static byte[]? CoverBytes(string path)
    {
        try
        {
            if (WebmTags.Applies(path)) return File.ReadAllBytes(WebmTags.CoverPath(path));
            using var file = TagLib.File.Create(path);
            return file.Tag.Pictures.FirstOrDefault()?.Data.Data;
        }
        catch { return null; }
    }

    public static ImageSource? LoadCover(string path)
    {
        if (CoverBytes(path) is not { } bytes) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.StreamSource = new MemoryStream(bytes);
            img.DecodePixelWidth = 300;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    // ---- metadata completion + covers: iTunes (cleaner albums), Deezer (far more Russian and niche music), then SoundCloud
    // (the most songs, the messiest data). All free, no account. ----

    /// <summary>Looks the song up online, fills only the EMPTY tags (and the cover if there is none) and writes them
    /// into the file. Returns false when nobody has a confident match. Throws HttpRequestException (403/429 = rate limit).</summary>
    public static async Task<bool> CompleteAsync(HttpClient http, Track t)
    {
        var (artist, title) = SearchTerms(t);
        if (await FindAsync(http, artist, title) is not { } hit) return false;
        byte[]? cover = null;
        if (!t.HasCover && hit.ArtUrl.Length > 0)
            try { cover = await http.GetByteArrayAsync(hit.ArtUrl); }
            catch (HttpRequestException) { /* a dead cover link isn't being offline, and the tags are still worth having */ }

        if (!WebmTags.Applies(t.Path))
        using (var file = TagLib.File.Create(t.Path))
        {
            // Untagged files have no tag block to write into; create the format's native one.
            if (file is TagLib.Mpeg4.File) file.GetTag(TagLib.TagTypes.Apple, true);
            else if (file is TagLib.Mpeg.AudioFile) file.GetTag(TagLib.TagTypes.Id3v2, true);
            var tag = file.Tag;
            if (t.Artist.Length == 0) tag.Performers = [hit.Artist];
            if (t.TitleIsFileName) tag.Title = hit.Title;
            if (t.Album.Length == 0) tag.Album = hit.Album;
            if (t.Genre.Length == 0) tag.Genres = [hit.Genre];
            if (t.Year == 0) tag.Year = hit.Year;
            if (t.Number == 0) tag.Track = hit.Number;
            if (cover != null)
                tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
            file.Save();
        }

        if (t.Artist.Length == 0) t.Artist = hit.Artist;
        if (t.TitleIsFileName) { t.Title = hit.Title; t.TitleIsFileName = false; }
        if (t.Album.Length == 0) t.Album = hit.Album;
        if (t.Genre.Length == 0) t.Genre = hit.Genre;
        if (t.Year == 0) t.Year = hit.Year;
        if (t.Number == 0) t.Number = hit.Number;
        if (WebmTags.Applies(t.Path))
        {
            if (cover != null) WebmTags.SaveCover(t.Path, cover);
            WebmTags.Save(t);
        }
        if (cover != null) { t.HasCover = true; t.ForgetCover(); }
        return true;
    }

    /// <summary>If iTunes refuses and nobody else has it either, iTunes' error goes up so the caller can wait and retry.</summary>
    public static async Task<SongHit?> FindAsync(HttpClient http, string artist, string title)
    {
        HttpRequestException? refused = null;
        try
        {
            var json = await http.GetStringAsync($"https://itunes.apple.com/search?media=music&entity=song&limit=10&term={Uri.EscapeDataString(SearchTerm(artist, title))}");
            if (Pick(json, artist, title) is { } hit) return hit;
        }
        catch (HttpRequestException ex) { refused = ex; }

        var other = await FindOnDeezerAsync(http, artist, title) ?? await FindOnSoundCloudAsync(http, artist, title);
        if (other == null && refused != null) throw refused; // keeps the real reason: rate limit means wait, anything else means offline
        return other;
    }

    // ---- SoundCloud: public website search, metadata only. The user chose this over an API key, for personal use. ----

    private const string BrowserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
    private static string? _soundCloudId;

    private static async Task<SongHit?> FindOnSoundCloudAsync(HttpClient http, string artist, string title)
    {
        try
        {
            return RankSoundCloud(await SearchSoundCloudAsync(http, SearchTerm(artist, title)), artist, title).FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static async Task<string> SearchSoundCloudAsync(HttpClient http, string query)
    {
        _soundCloudId ??= await FindSoundCloudIdAsync(http);
        using var first = await Send();
        if (first.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
            return await first.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        _soundCloudId = await FindSoundCloudIdAsync(http); // the site rotates it every so often
        using var second = await Send();
        return await second.EnsureSuccessStatusCode().Content.ReadAsStringAsync();

        async Task<HttpResponseMessage> Send()
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api-v2.soundcloud.com/search/tracks?limit=25&client_id={_soundCloudId}&q={Uri.EscapeDataString(query)}");
            req.Headers.UserAgent.ParseAdd(BrowserAgent);
            return await http.SendAsync(req);
        }
    }

    private static async Task<string> FindSoundCloudIdAsync(HttpClient http)
    {
        var scripts = Regex.Matches(await GetAsBrowser("https://soundcloud.com/"), @"<script[^>]+src=""(https://[a-z0-9.-]+\.sndcdn\.com/[^""]+\.js)""")
            .Select(m => m.Groups[1].Value).Reverse();
        foreach (var script in scripts) // it's in one of the last ones
            if (Regex.Match(await GetAsBrowser(script), @"client_id\s*[:=]\s*""([0-9A-Za-z_-]{20,})""") is { Success: true } m)
                return m.Groups[1].Value;
        throw new InvalidOperationException("SoundCloud's page has changed.");

        async Task<string> GetAsBrowser(string url)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(BrowserAgent);
            using var resp = await http.SendAsync(req);
            return await resp.EnsureSuccessStatusCode().Content.ReadAsStringAsync();
        }
    }

    /// <summary>Matching rows: the exact song before "(slowed)" and cut-off titles, then the most trustworthy first: an album named,
    /// cover art, a release date. Public for the self-test.</summary>
    public static List<SongHit> RankSoundCloud(string json, string artist, string title)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("collection", out var rows)) return [];
        return Ranked(rows.EnumerateArray().ToList(), artist, title, r => SoundCloudRow(r).Artist, r => SoundCloudRow(r).Title)
            .Select(SoundCloudRow)
            .OrderBy(h => h.Title.IndexOfAny(['(', '[']) >= 0 || Norm(h.Title) != Norm(CleanTitle(title)))
            .ThenByDescending(h => (h.Album.Length > 0 ? 4 : 0) + (h.ArtUrl.Length > 0 ? 2 : 0) + (h.Year > 0 ? 1 : 0))
            .ToList();
    }

    /// <summary>Anyone can upload, so the fields need care: "Artist - Title" in the title beats the uploader's name, a "genre" with
    /// no letters is a year someone typed, and an album in cp1251 read as Latin-1 ("Êàéô") is worse than none.</summary>
    private static SongHit SoundCloudRow(JsonElement r)
    {
        var meta = r.TryGetProperty("publisher_metadata", out var pm) && pm.ValueKind == JsonValueKind.Object ? pm : default;
        string Meta(string name) => meta.ValueKind == JsonValueKind.Object ? Str(meta, name) : "";
        var uploader = r.TryGetProperty("user", out var user) ? Str(user, "username") : "";
        var artist = Meta("artist").Length > 0 ? Meta("artist") : uploader;
        var title = Str(r, "title");
        var split = Regex.Match(title, @"^(.+?)\s+[-–—]\s*(.+)$");
        if (split.Success) (artist, title) = (split.Groups[1].Value.Trim(), split.Groups[2].Value.Trim());

        var album = Meta("album_title");
        if (Regex.IsMatch(album, "[À-ÿ]{3}")) album = "";
        var genre = Str(r, "genre");
        if (!genre.Any(char.IsLetter)) genre = "";
        var art = Str(r, "artwork_url");
        return new SongHit(title, artist, album, genre, uint.TryParse(Str(r, "release_date").Split('-')[0], out var y) ? y : 0, 0,
            art.Replace("-large.", "-t500x500."), art, "SoundCloud");
    }

    /// <summary>Deezer's search has no genre, year or track number, and lists compilations before the real album, so a match
    /// costs up to 6 more requests. Any trouble here just counts as no match. Limit is 50 requests per 5 s.</summary>
    private static async Task<SongHit?> FindOnDeezerAsync(HttpClient http, string artist, string title)
    {
        try
        {
            var strict = $"artist:\"{Norm(MainArtist(artist))}\" track:\"{Norm(title)}\"";
            var found = RankDeezer(await Search(strict), artist, title);
            if (found.Count == 0) found = RankDeezer(await Search(SearchTerm(artist, title)), artist, title);
            if (found.Count == 0) return null;

            // the same song on different albums: the original release, not a compilation
            var albums = new List<((SongHit Hit, long AlbumId, long TrackId) Row, string Json)>();
            foreach (var row in found.Where(f => f.Hit.Title.Equals(found[0].Hit.Title, StringComparison.OrdinalIgnoreCase)).DistinctBy(f => f.AlbumId).Take(5))
                albums.Add((row, await http.GetStringAsync($"https://api.deezer.com/album/{row.AlbumId}")));
            var best = albums.OrderBy(a => AlbumRank(a.Json)).First();
            return AddDeezerDetails(best.Row.Hit, best.Json, await http.GetStringAsync($"https://api.deezer.com/track/{best.Row.TrackId}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }

        Task<string> Search(string query) => http.GetStringAsync($"https://api.deezer.com/search?limit=25&q={Uri.EscapeDataString(query)}");
    }

    /// <summary>The matching row of a Deezer search, by the same rules as iTunes. Public for the self-test.</summary>
    public static List<(SongHit Hit, long AlbumId, long TrackId)> RankDeezer(string json, string artist, string title)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return []; // {"error": ...}, e.g. over quota
        return Ranked(data.EnumerateArray().ToList(), artist, title, r => Str(r.GetProperty("artist"), "name"), r => Str(r, "title"))
            .Select(h =>
            {
                var row = DeezerRow(h);
                return (row, row.DeezerAlbumId, row.DeezerTrackId);
            }).ToList();
    }

    private static SongHit DeezerRow(JsonElement h)
    {
        var album = h.GetProperty("album");
        return new SongHit(Str(h, "title"), Str(h.GetProperty("artist"), "name"), Str(album, "title"), "", 0, 0, Str(album, "cover_xl"),
            Str(album, "cover_small"), "Deezer", album.GetProperty("id").GetInt64(), h.GetProperty("id").GetInt64());
    }

    private static SongHit ItunesRow(JsonElement r) =>
        new(Str(r, "trackName"), Str(r, "artistName"), Str(r, "collectionName"), Str(r, "primaryGenreName"),
            uint.TryParse(Str(r, "releaseDate").Split('-')[0], out var y) ? y : 0,
            r.TryGetProperty("trackNumber", out var n) && n.TryGetUInt32(out var num) ? num : 0,
            Str(r, "artworkUrl100").Replace("100x100bb", "600x600bb"), Str(r, "artworkUrl100"), "iTunes");

    // ---- choosing by hand: no matching rules, the person is the judge ----

    /// <summary>Whatever both catalogues have for what was typed. Throws only if both fail.</summary>
    public static async Task<List<SongHit>> SearchEverywhereAsync(HttpClient http, string query)
    {
        var term = Uri.EscapeDataString(query);
        var itunes = Fetch($"https://itunes.apple.com/search?media=music&entity=song&limit=15&term={term}");
        var deezer = Fetch($"https://api.deezer.com/search?limit=25&q={term}");
        var soundCloud = SearchSoundCloudAsync(http, query);
        await Task.WhenAll(itunes.ContinueWith(_ => { }), deezer.ContinueWith(_ => { }), soundCloud.ContinueWith(_ => { }));
        if (itunes.IsFaulted && deezer.IsFaulted && soundCloud.IsFaulted) await itunes;
        return ParseSearchResults(itunes.IsFaulted ? "{}" : itunes.Result, deezer.IsFaulted ? "{}" : deezer.Result, soundCloud.IsFaulted ? "{}" : soundCloud.Result);

        Task<string> Fetch(string url) => http.GetStringAsync(url);
    }

    /// <summary>Both result lists as one, without the same song listed twice. Public for the self-test.</summary>
    public static List<SongHit> ParseSearchResults(string itunesJson, string deezerJson, string soundCloudJson = "{}")
    {
        var rows = new List<SongHit>();
        using (var doc = JsonDocument.Parse(itunesJson))
            if (doc.RootElement.TryGetProperty("results", out var results))
                rows.AddRange(results.EnumerateArray().Where(r => Str(r, "trackName").Length > 0).Select(ItunesRow));
        using (var doc = JsonDocument.Parse(deezerJson))
            if (doc.RootElement.TryGetProperty("data", out var data))
                rows.AddRange(data.EnumerateArray().Select(DeezerRow));
        using (var doc = JsonDocument.Parse(soundCloudJson))
            if (doc.RootElement.TryGetProperty("collection", out var collection))
                rows.AddRange(collection.EnumerateArray().Select(SoundCloudRow).Where(r => r.Title.Length > 0));
        return rows.DistinctBy(r => (Norm(r.Title), Norm(r.Artist), Norm(r.Album))).ToList();
    }

    /// <summary>Writes a chosen song's details over the existing tags. Fields the source doesn't have keep their old value.
    /// The player must not have the file open.</summary>
    public static async Task ApplyAsync(HttpClient http, Track t, SongHit hit)
    {
        if (hit.Source == "Deezer")
            hit = AddDeezerDetails(hit,
                await http.GetStringAsync($"https://api.deezer.com/album/{hit.DeezerAlbumId}"),
                await http.GetStringAsync($"https://api.deezer.com/track/{hit.DeezerTrackId}"));
        byte[]? cover = null;
        if (hit.ArtUrl.Length > 0)
            try { cover = await http.GetByteArrayAsync(hit.ArtUrl); }
            catch (HttpRequestException) { /* the tags alone are still worth having */ }

        if (!WebmTags.Applies(t.Path))
            using (var file = TagLib.File.Create(t.Path))
            {
                if (file is TagLib.Mpeg4.File) file.GetTag(TagLib.TagTypes.Apple, true);
                else if (file is TagLib.Mpeg.AudioFile) file.GetTag(TagLib.TagTypes.Id3v2, true);
                var tag = file.Tag;
                if (hit.Title.Length > 0) tag.Title = hit.Title;
                if (hit.Artist.Length > 0) tag.Performers = [hit.Artist];
                if (hit.Album.Length > 0) tag.Album = hit.Album;
                if (hit.Genre.Length > 0) tag.Genres = [hit.Genre];
                if (hit.Year > 0) tag.Year = hit.Year;
                if (hit.Number > 0) tag.Track = hit.Number;
                if (cover != null)
                    tag.Pictures = [new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" }];
                file.Save();
            }

        if (hit.Title.Length > 0) { t.Title = hit.Title; t.TitleIsFileName = false; }
        if (hit.Artist.Length > 0) t.Artist = hit.Artist;
        if (hit.Album.Length > 0) t.Album = hit.Album;
        if (hit.Genre.Length > 0) t.Genre = hit.Genre;
        if (hit.Year > 0) t.Year = hit.Year;
        if (hit.Number > 0) t.Number = hit.Number;
        if (WebmTags.Applies(t.Path))
        {
            if (cover != null) WebmTags.SaveCover(t.Path, cover);
            WebmTags.Save(t);
        }
        if (cover != null) { t.HasCover = true; t.ForgetCover(); }
    }

    /// <summary>Sort key for a Deezer album: real releases before compilations, then oldest first.</summary>
    public static (int, string) AlbumRank(string albumJson)
    {
        using var album = JsonDocument.Parse(albumJson);
        var date = Str(album.RootElement, "release_date");
        return (Str(album.RootElement, "record_type") == "compile" ? 1 : 0, date.Length > 0 ? date : "9999");
    }

    public static SongHit AddDeezerDetails(SongHit hit, string albumJson, string trackJson)
    {
        using var album = JsonDocument.Parse(albumJson);
        using var track = JsonDocument.Parse(trackJson);
        var genre = album.RootElement.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var list) && list.GetArrayLength() > 0
            ? Str(list[0], "name") : "";
        return hit with
        {
            Genre = genre,
            Year = uint.TryParse(Str(album.RootElement, "release_date").Split('-')[0], out var y) ? y : 0,
            Number = track.RootElement.TryGetProperty("track_position", out var n) && n.TryGetUInt32(out var number) ? number : 0,
        };
    }

    /// <summary>What a catalogue gets asked: "(Official Video)" and the like find nothing there.</summary>
    public static string SearchTerm(string artist, string title) => $"{Norm(artist)} {Norm(CleanTitle(title))}".Trim();

    /// <summary>Takes off what video titles add outside brackets: "Creep - Lyrics", "Monster with lyrics", "Wonderwall | Official Video".
    /// Needs text before the marker, so a song really called "Lyrics" keeps its name. Norm already drops anything in brackets.</summary>
    public static string CleanTitle(string title) =>
        Regex.Replace(title.Trim(), @"(?<=\S)(?:\s*[-–—|]\s*|\s*\+\s*|\s+)(?:with\s+)?lyrics?$|(?<=\S)\s*[-–—|]\s*official\s+(?:music\s+|lyric\s+)?(?:video|audio)$",
            "", RegexOptions.IgnoreCase).Trim();

    /// <summary>Artist + title to search for. Untagged files fall back to "Artist - Title" (or "01 - Artist - Title") file names.</summary>
    public static (string Artist, string Title) SearchTerms(Track t)
    {
        if (t.Artist.Length > 0) return (t.Artist, t.Title);
        var m = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(t.Path), @"^(?:\d{1,3} - )?(.+?) - (.+)$");
        return m.Success ? (m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()) : ("", t.Title);
    }

    /// <summary>The result with this artist and this title; a title that merely starts the same (file names get cut off)
    /// is the fallback. No artist to compare means no match: these get written without asking, and better no tags
    /// than a stranger's.</summary>
    public static SongHit? Pick(string json, string artist, string title)
    {
        using var doc = JsonDocument.Parse(json);
        var ranked = Ranked(doc.RootElement.GetProperty("results").EnumerateArray().ToList(), artist, title, r => Str(r, "artistName"), r => Str(r, "trackName"));
        if (ranked.Count == 0) return null;
        return ItunesRow(ranked[0]);
    }

    /// <summary>Rows with this artist and this title, best first: an exact title, one without "(Remix)"-style brackets ahead of
    /// one with; then titles that merely start the same (file names get cut off). No artist to compare means no match:
    /// these get written without asking, and better no tags than a stranger's.</summary>
    private static List<JsonElement> Ranked(List<JsonElement> rows, string artist, string title, Func<JsonElement, string> artistOf, Func<JsonElement, string> titleOf)
    {
        var wantTitle = Norm(CleanTitle(title));
        var wantArtist = Norm(MainArtist(artist));
        if (wantTitle.Length == 0 || wantArtist.Length == 0) return [];
        var results = rows.Where(r => Norm(artistOf(r)).Contains(wantArtist) && Norm(titleOf(r)).Length > 0).ToList();
        var exact = results.Where(r => Norm(titleOf(r)) == wantTitle).OrderBy(r => titleOf(r).IndexOfAny(['(', '[']) >= 0).ToList();
        return [.. exact, .. results.Except(exact).Where(r => Norm(titleOf(r)).StartsWith(wantTitle) || wantTitle.StartsWith(Norm(titleOf(r))))];
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string MainArtist(string artist) => Regex.Split(artist, @",|&|\bfeat\.?|\bft\.?|;|/")[0];

    // ---- deleting: always to the Recycle Bin, never outright ----

    public static void Recycle(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    public static void RecycleFolder(string path) =>
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

    /// <summary>A folder may go with its album only if it isn't the music folder itself (or above it), has no subfolders,
    /// and all that's left is album leftovers like cover art. Anything else in there is the user's.</summary>
    public static bool CanRemoveFolder(string dir, string? root)
    {
        if (!Directory.Exists(dir) || root == null) return false;
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(dir)) + System.IO.Path.DirectorySeparatorChar;
        var rootFull = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root)) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) || full.Length == rootFull.Length) return false;
        return !Directory.EnumerateDirectories(dir).Any()
            && Directory.EnumerateFiles(dir).All(f => Leftovers.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()));
    }

    private static readonly string[] Leftovers = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ini", ".nfo", ".m3u", ".m3u8", ".cue", ".log"];

    // lower case, "(feat. ...)" / "[Remastered]" dropped, punctuation folded to spaces
    private static string Norm(string s) =>
        Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[\(\[][^\)\]]*[\)\]]?", " "), @"[^\p{L}\p{N}]+", " ").Trim();
}

/// <summary>
/// Tags and covers for .webm songs live in %APPDATA%\ShibaPlayer, not in the file: TagLibSharp writes WebM that
/// Media Foundation then refuses to open (0xC00D3E8C), even for one text tag. So those files are never written to.
/// ponytail: other players don't see these tags, and moving the file loses them; a real WebM tag writer fixes both.
/// </summary>
public static class WebmTags
{
    public sealed record Entry(string? Title, string Artist, string Album, string Genre, uint Year, uint Number);

    private const string FileName = "webm-tags.json";
    private static Dictionary<string, Entry>? _all;
    private static string? _loadedFrom;

    public static bool Applies(string path) => System.IO.Path.GetExtension(path).Equals(".webm", StringComparison.OrdinalIgnoreCase);

    public static string CoverPath(string path)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant())));
        return System.IO.Path.Combine(Store.Dir, "covers", hash + ".jpg");
    }

    public static Entry? Get(string path)
    {
        if (!Applies(path)) return null;
        lock (FileName) return All().GetValueOrDefault(path);
    }

    public static void Save(Track t)
    {
        lock (FileName)
        {
            All()[t.Path] = new Entry(t.TitleIsFileName ? null : t.Title, t.Artist, t.Album, t.Genre, t.Year, t.Number);
            Store.Save(FileName, _all);
        }
    }

    public static void SaveCover(string path, byte[] cover)
    {
        var file = CoverPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, cover);
    }

    private static Dictionary<string, Entry> All()
    {
        if (_all == null || _loadedFrom != Store.Dir) // the self-test points Store at a temp folder
        {
            _all = new(Store.Load<Dictionary<string, Entry>>(FileName) ?? [], StringComparer.OrdinalIgnoreCase);
            _loadedFrom = Store.Dir;
        }
        return _all;
    }
}

/// <summary>The settings page. Saved as settings.json.</summary>
public sealed class AppSettings
{
    public static readonly (string Key, string Name)[] Qualities =
    [
        ("m4a", "M4A  -  AAC about 128 kbps, exactly as YouTube stores it. Fastest, smallest."),
        ("mp3192", "MP3 192 kbps  -  plays anywhere. Converted, so it takes a little longer."),
        ("mp3320", "MP3 320 kbps  -  bigger MP3. Still only as good as YouTube's source."),
        ("best", "Highest  -  the best stream YouTube has, untouched. Normally Opus ~160 kbps (.webm)."),
    ];

    public string Quality { get; set; } = "m4a";
    public int Viz { get; set; } = 1;
}

/// <summary>Remembers the music folder and playlists in %APPDATA%\ShibaPlayer. Playlists hold file paths.</summary>
public static class Store
{
    public static string Dir { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShibaPlayer");

    private static string PlaylistFile => System.IO.Path.Combine(Dir, "playlists.json");
    private static string RootFile => System.IO.Path.Combine(Dir, "folder.txt");

    public static Dictionary<string, List<string>> LoadPlaylists()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(PlaylistFile)) ?? []; }
        catch { return []; }
    }

    public static void SavePlaylists(Dictionary<string, List<string>> playlists)
    {
        Directory.CreateDirectory(Dir);
        // write-then-move so a crash mid-save can't eat every playlist
        File.WriteAllText(PlaylistFile + ".tmp", JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(PlaylistFile + ".tmp", PlaylistFile, true);
    }

    private static string EqFile => System.IO.Path.Combine(Dir, "equalizer.json");

    public static float[]? LoadEq()
    {
        try { return JsonSerializer.Deserialize<float[]>(File.ReadAllText(EqFile)); }
        catch { return null; }
    }

    public static void SaveEq(float[] gains)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(EqFile, JsonSerializer.Serialize(gains));
    }

    private static string ThemeFile => System.IO.Path.Combine(Dir, "theme.txt");
    // New name when a better source arrives: everything the old sources couldn't find gets one more try.
    private static string LookedUpFile => System.IO.Path.Combine(Dir, "looked-up-4.json");

    /// <summary>True/false once the user picked a theme, null before that.</summary>
    public static bool? LoadDark() => File.Exists(ThemeFile) ? File.ReadAllText(ThemeFile).Trim() == "dark" : null;

    public static void SaveDark(bool dark)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ThemeFile, dark ? "dark" : "light");
    }

    public static HashSet<string> LoadLookedUp()
    {
        try { return new(JsonSerializer.Deserialize<string[]>(File.ReadAllText(LookedUpFile)) ?? [], StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    public static void SaveLookedUp(HashSet<string> paths)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(LookedUpFile, JsonSerializer.Serialize(paths));
    }

    /// <summary>A settings file by name; null if it's missing or broken.</summary>
    public static T? Load<T>(string name) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(System.IO.Path.Combine(Dir, name))); }
        catch { return null; }
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(System.IO.Path.Combine(Dir, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? LoadRoot() => File.Exists(RootFile) ? File.ReadAllText(RootFile).Trim() : null;

    public static void SaveRoot(string root)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(RootFile, root);
    }
}
