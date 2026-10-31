using System.IO;

namespace ShibaPlayer;

/// <summary>
/// `ShibaPlayer.exe --selftest` - checks genre matching, iTunes result picking (canned JSON), file-name search terms
/// and the playlist store (in %TEMP%). Touches no music files and no network. Prints PASS/FAIL; exit code 0 = all passed.
/// Pipe it to see the output: `.\ShibaPlayer.exe --selftest | Write-Output`.
/// </summary>
public static class SelfTest
{
    private static int _failed;

    public static int Run()
    {
        // ---- genre matching ----
        Check("ID3v1 number (17) is Rock", Genres.Match("(17)") == "Rock");
        Check("bare ID3v1 number 7 is Hip-Hop", Genres.Match("7") == "Hip-Hop");
        Check("Hip-Hop/Rap is Hip-Hop", Genres.Match("Hip-Hop/Rap") == "Hip-Hop");
        Check("Death Metal beats Rock", Genres.Match("Death Metal") == "Metal");
        Check("Synthpop is Electronic", Genres.Match("Synthpop") == "Electronic");
        Check("Alternative Rock is Rock", Genres.Match("Alternative Rock") == "Rock");
        Check("empty is Unknown", Genres.Match("") == "Unknown" && Genres.Match(null) == "Unknown");
        Check("Polka is Other", Genres.Match("Polka") == "Other");

        // ---- iTunes result picking ----
        const string json = """
        {"resultCount":2,"results":[
          {"trackName":"One More Time (Cover)","artistName":"Somebody Else","collectionName":"Covers","primaryGenreName":"Pop",
           "releaseDate":"2015-01-01T08:00:00Z","trackNumber":3,"artworkUrl100":"https://x/a/100x100bb.jpg"},
          {"trackName":"One More Time","artistName":"Daft Punk","collectionName":"Discovery","primaryGenreName":"Dance",
           "releaseDate":"2000-11-30T08:00:00Z","trackNumber":1,"artworkUrl100":"https://x/b/100x100bb.jpg"}]}
        """;
        var hit = Library.Pick(json, "Daft Punk", "One More Time");
        Check("picks the matching artist, not the cover version",
            hit is { Artist: "Daft Punk", Album: "Discovery", Genre: "Dance", Year: 2000, Number: 1, ArtUrl: "https://x/b/600x600bb.jpg" });
        Check("no match for a stranger's song", Library.Pick(json, "Daft Punk", "Around the World") == null);
        Check("featured artists don't block the match", Library.Pick(json, "Daft Punk feat. Romanthony", "One More Time") != null);

        Check("no artist to compare means no match", Library.Pick(json, "", "One More Time") == null);
        const string json2 = """
        {"results":[{"trackName":"Time After Time","artistName":"Cyndi Lauper","collectionName":"Wrong"},
                    {"trackName":"Time","artistName":"Cyndi Lauper","collectionName":"Right"}]}
        """;
        Check("exact title beats one that only starts the same", Library.Pick(json2, "Cyndi Lauper", "Time")?.Album == "Right");
        Check("cut-off title still matches", Library.Pick(json2, "Cyndi Lauper", "Time After")?.Album == "Wrong");

        // ---- which folders may go with their album ----
        var music = Path.Combine(Path.GetTempPath(), "ShibaPlayerSelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var album = Path.Combine(music, "Album");
            Directory.CreateDirectory(album);
            File.WriteAllText(Path.Combine(album, "cover.JPG"), "");
            Check("empty album folder (cover art left) can go", Library.CanRemoveFolder(album, music));
            Check("album folder can go with a trailing slash on the root", Library.CanRemoveFolder(album, music + Path.DirectorySeparatorChar));
            Check("the music folder itself never goes", !Library.CanRemoveFolder(music, music));
            Check("a folder outside the music folder never goes", !Library.CanRemoveFolder(music, album));
            File.WriteAllText(Path.Combine(album, "other.mp3"), "");
            Check("folder with other music stays", !Library.CanRemoveFolder(album, music));
            File.Delete(Path.Combine(album, "other.mp3"));
            File.WriteAllText(Path.Combine(album, "notes.docx"), "");
            Check("folder with the user's own files stays", !Library.CanRemoveFolder(album, music));
            File.Delete(Path.Combine(album, "notes.docx"));
            Directory.CreateDirectory(Path.Combine(album, "Disc 2"));
            Check("folder with a subfolder stays", !Library.CanRemoveFolder(album, music));
        }
        finally { Directory.Delete(music, true); }

        // ---- YouTube file names ----
        Check("title naming the artist is used as is", YouTubeHit.FileName("Rick Astley - Never Gonna Give You Up", "RickAstleyVEVO") == "Rick Astley - Never Gonna Give You Up.m4a");
        Check("Topic channel is the artist", YouTubeHit.FileName("Hello", "Adele - Topic") == "Adele - Hello.m4a");
        Check("VEVO comes off the channel", YouTubeHit.FileName("Hello", "AdeleVEVO") == "Adele - Hello.m4a");
        Check("long dash counts as a separator", YouTubeHit.FileName("A \u2013 B", "C") == "A - B.m4a");
        Check("illegal characters go", YouTubeHit.FileName("AC/DC - What? No: \"Way\"", "x") == "ACDC - What No Way.m4a");
        Check("nothing usable still gives a name", YouTubeHit.FileName("???", "") == "YouTube song.m4a");

        // ---- video-title noise ----
        Check("lyrics markers come off", Library.CleanTitle("Creep - Lyrics") == "Creep" && Library.CleanTitle("Monster with lyrics") == "Monster" && Library.CleanTitle("A Horse With No Name+Lyrics") == "A Horse With No Name");
        Check("official video marker comes off", Library.CleanTitle("Wonderwall | Official Video") == "Wonderwall" && Library.CleanTitle("Яд - Official Music Video") == "Яд");
        Check("a song called Lyrics keeps its name, and so does one with the word inside", Library.CleanTitle("Lyrics") == "Lyrics" && Library.CleanTitle("Lyrics of Love") == "Lyrics of Love");
        Check("cleaned titles match the catalogue", Library.Pick("""{"results":[{"trackName":"Creep","artistName":"Radiohead"}]}""", "Radiohead", "Creep - Lyrics") != null);

        // ---- SoundCloud rows: anyone can upload ----
        const string soundCloud = """
        {"collection":[
          {"title":"Гамора - Яд (slowed)","user":{"username":"Someone"},"genre":"Rap","artwork_url":"https://x/a-large.jpg"},
          {"title":"Гамора - Яд","user":{"username":"Alexey"},"genre":"2016","artwork_url":null,"publisher_metadata":null},
          {"title":"Яд","user":{"username":"Гамора"},"genre":"Hip-hop & Rap","artwork_url":"https://x/b-large.jpg","release_date":"2019-11-25T00:00:00Z","publisher_metadata":{"artist":"Гамора","album_title":"Альбом"}},
          {"title":"Яд","user":{"username":"Гамора"},"genre":"","artwork_url":"https://x/c-large.jpg","publisher_metadata":{"artist":"Гамора","album_title":"Êàéô òû"}},
          {"title":"Яд","user":{"username":"Кто-то другой"},"genre":"Pop","artwork_url":"https://x/d-large.jpg"}]}
        """;
        var cloud = Library.RankSoundCloud(soundCloud, "ГАМОРА", "ЯД");
        Check("SoundCloud: the row with an album and art wins, big cover, real genre and year",
            cloud.Count == 4 && cloud[0] is { Album: "Альбом", Genre: "Hip-hop & Rap", Year: 2019, ArtUrl: "https://x/b-t500x500.jpg", Source: "SoundCloud" });
        Check("SoundCloud: a stranger's upload and a garbled album are refused, and a year isn't a genre",
            cloud.All(h => h.Artist == "Гамора" || h.Artist == "Someone") && cloud.All(h => !h.Album.Contains('Ê')) && cloud.Where(h => h.Genre == "2016").Count() == 0);
        Check("SoundCloud: the plain song beats a slowed upload with better data",
            Library.RankSoundCloud("""{"collection":[{"title":"Яд (slowed)","user":{"username":"Гамора"},"artwork_url":"x","publisher_metadata":{"album_title":"Альбом"}},{"title":"Гамора - Яд","user":{"username":"Кто-то"}}]}""", "Гамора", "Яд")[0].Title == "Яд");
        Check("SoundCloud: an error reply is just no match", Library.RankSoundCloud("{}", "a", "b").Count == 0);

        // ---- Deezer result picking ----
        const string deezer = """
        {"data":[{"id":1,"title":"Панелька (Remix)","artist":{"name":"Хаски"},"album":{"id":10,"title":"Wrong","cover_xl":"x"}},
                 {"id":2,"title":"Панелька","artist":{"name":"Другой"},"album":{"id":20,"title":"Wrong too","cover_xl":"x"}},
                 {"id":3,"title":"Панелька","artist":{"name":"ХАСКИ"},"album":{"id":30,"title":"Right","cover_xl":"big.jpg"}}]}
        """;
        var ranked = Library.RankDeezer(deezer, "Хаски feat. Кто-то", "Панелька (Official Video)");
        Check("Deezer: right artist wins, and the original beats the remix, in Cyrillic",
            ranked.Count == 2 && ranked[0] is { Hit.Album: "Right", Hit.ArtUrl: "big.jpg", AlbumId: 30, TrackId: 3 } && ranked[1].TrackId == 1);
        Check("Deezer: an error reply is just no match", Library.RankDeezer("""{"error":{"code":4}}""", "a", "b").Count == 0);
        Check("Deezer: no artist means no match", Library.RankDeezer(deezer, "", "Панелька").Count == 0);
        Check("Deezer: a real album beats an older compilation",
            Library.AlbumRank("""{"record_type":"album","release_date":"2017-01-01"}""").CompareTo(Library.AlbumRank("""{"record_type":"compile","release_date":"1990-01-01"}""")) < 0);
        Check("Deezer: the earlier release wins",
            Library.AlbumRank("""{"record_type":"album","release_date":"1987-11-12"}""").CompareTo(Library.AlbumRank("""{"record_type":"album","release_date":"2022-01-01"}""")) < 0);
        var detailed = Library.AddDeezerDetails(ranked[0].Hit, """{"release_date":"2017-04-01","genres":{"data":[{"name":"Rap/Hip Hop"}]}}""", """{"track_position":5}""");
        Check("Deezer: genre, year and track number come through", detailed is { Genre: "Rap/Hip Hop", Year: 2017, Number: 5 } && Genres.Match(detailed.Genre) == "Hip-Hop");

        // ---- searching by hand: no matching rules, duplicates folded ----
        var both = Library.ParseSearchResults(
            """{"results":[{"trackName":"Яд","artistName":"Гамора","collectionName":"Альбом","primaryGenreName":"Rap","releaseDate":"2020-01-01T00:00:00Z","artworkUrl100":"a/100x100bb.jpg"}]}""",
            """{"data":[{"id":7,"title":"ЯД","artist":{"name":"ГАМОРА"},"album":{"id":8,"title":"альбом","cover_xl":"xl","cover_small":"s"}},{"id":9,"title":"Другая","artist":{"name":"Кто-то"},"album":{"id":10,"title":"Б","cover_xl":"xl","cover_small":"s"}}]}""");
        Check("hand search lists both sources, without the same song twice",
            both.Count == 2 && both[0] is { Source: "iTunes", Year: 2020, ArtUrl: "a/600x600bb.jpg", ThumbUrl: "a/100x100bb.jpg" } && both[1] is { Source: "Deezer", DeezerAlbumId: 10, DeezerTrackId: 9, ThumbUrl: "s" });
        Check("hand search survives one source failing", Library.ParseSearchResults("{}", "{}").Count == 0);

        // ---- YouTube links and the layouts of playlist rows ----
        Check("playlist link", YouTube.ParsePlaylistId("https://www.youtube.com/playlist?list=PLabc-_1") == "PLabc-_1");
        Check("song in a playlist", YouTube.ParsePlaylistId("https://youtu.be/dQw4w9WgXcQ?si=x&list=PLabc&index=2") == "PLabc");
        Check("plain song link has no playlist", YouTube.ParsePlaylistId("https://www.youtube.com/watch?v=dQw4w9WgXcQ") == null);
        const string rows = """
        {"a":[{"playlistVideoRenderer":{"videoId":"old-layout1","title":{"runs":[{"text":"Old"}]},"shortBylineText":{"runs":[{"text":"Chan"}]},"lengthText":{"simpleText":"3:01"}}},
              {"playlistVideoRenderer":{"videoId":"deleted0000","title":{"simpleText":"[Deleted video]"}}},
              {"lockupViewModel":{"contentId":"new-layout1","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","contentImage":{"x":[{"thumbnailBadgeViewModel":{"text":"2:21"}}]},
                "metadata":{"lockupMetadataViewModel":{"title":{"content":"New"},"metadata":{"m":{"metadataRows":[{"metadataParts":[{"text":{"content":"Chan2"}}]}]}}}}}},
              {"lockupViewModel":{"contentId":"not-a-video","contentType":"LOCKUP_CONTENT_TYPE_PLAYLIST"}},
              {"lockupViewModel":{"contentId":"live0000000","contentType":"LOCKUP_CONTENT_TYPE_VIDEO","contentImage":{"thumbnailBadgeViewModel":{"text":"LIVE"}}}},
              {"continuationItemViewModel":{"continuationCommand":{"innertubeCommand":{"continuationCommand":{"token":"NEXT"}}}}}]}
        """;
        var parsed = new List<YouTubeHit>();
        string? token = null;
        using (var rowsDoc = System.Text.Json.JsonDocument.Parse(rows)) YouTube.CollectVideos(rowsDoc.RootElement, parsed, ref token);
        Check("both playlist layouts are read, deleted and live rows left out",
            parsed.Count == 2 && parsed[0] is { Id: "old-layout1", Title: "Old", Channel: "Chan", LengthText: "3:01" } && parsed[1] is { Id: "new-layout1", Title: "New", Channel: "Chan2", LengthText: "2:21" });
        Check("next page token is found", token == "NEXT");

        // ---- search terms from file names ----
        Check("video junk stays out of the iTunes search", Library.SearchTerm("Daft Punk", "One More Time (Official Video) [4K]") == "daft punk one more time");
        Check("tagged song uses its tags", Library.SearchTerms(new Track { Path = @"C:\x.mp3", Artist = "A", Title = "T" }) == ("A", "T"));
        Check("'01 - Artist - Title' file name", Library.SearchTerms(new Track { Path = @"C:\M\01 - Daft Punk - One More Time.mp3", Title = "x" }) == ("Daft Punk", "One More Time"));
        Check("'Artist - Title' file name", Library.SearchTerms(new Track { Path = @"C:\M\Daft Punk - One More Time.m4a", Title = "x" }) == ("Daft Punk", "One More Time"));

        // ---- playlist store ----
        var real = Store.Dir;
        Store.Dir = Path.Combine(Path.GetTempPath(), "ShibaPlayerSelfTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check("no file = no playlists", Store.LoadPlaylists().Count == 0);
            Store.SavePlaylists(new() { ["Walkies"] = [@"C:\M\a.mp3", @"C:\M\b.m4a"] });
            var back = Store.LoadPlaylists();
            Check("playlists round-trip", back.TryGetValue("Walkies", out var l) && l.SequenceEqual([@"C:\M\a.mp3", @"C:\M\b.m4a"]));
            Store.SaveRoot(@"D:\Music");
            Check("folder round-trips", Store.LoadRoot() == @"D:\Music");
            Check("no theme picked yet", Store.LoadDark() == null);
            Store.SaveDark(true);
            Check("theme round-trips", Store.LoadDark() == true);
            Store.SaveLookedUp(new(StringComparer.OrdinalIgnoreCase) { @"C:\M\a.mp3" });
            Check("looked-up list round-trips, ignoring case", Store.LoadLookedUp().Contains(@"c:\m\A.MP3"));
        }
        finally
        {
            Directory.Delete(Store.Dir, true);
            Store.Dir = real;
        }

        // ---- equalizer (pure math, a 44.1 kHz stereo sine through EqProcessor) ----
        var eq = new Equalizer();
        Check("flat EQ leaves the signal untouched", Gain(eq, 1000) is > 0.999 and < 1.001);
        eq.Set(5, 12); // +12 dB at 1 kHz; the preamp pulls everything down 12 dB so it can't clip
        Check("boosted band keeps its level (boost minus preamp)", Gain(eq, 1000) is > 0.85 and < 1.15);
        Check("other bands drop by the preamp (~-12 dB)", Gain(eq, 31) is > 0.2 and < 0.3);
        eq.Set(5, 0);
        eq.Set(0, -12);
        Check("cut band is quieter", Gain(eq, 31) < 0.4 && Gain(eq, 4000) > 0.95);
        eq.Set(0, 40);
        Check("gains clamp to +12 dB", eq.Gains[0] == 12);

        // ---- effects (pure math) ----
        var loud = Sine(1000, 0.9, 44100);
        var comp = new EffectSettings { Threshold = -20, Ratio = 4, Knee = 1, Makeup = 0, Attack = 5, Release = 50, RmsPeak = 1 };
        var squeezed = (float[])loud.Clone();
        new Compressor(comp, 2, 44100).Process(squeezed);
        Check($"compressor turns loud down (RMS x{Rms(squeezed) / Rms(loud):0.00})", Rms(squeezed) < Rms(loud) * 0.35);
        var quiet = Sine(1000, 0.01, 44100);
        var untouched = (float[])quiet.Clone();
        new Compressor(comp, 2, 44100).Process(untouched);
        Check("compressor leaves quiet alone", Math.Abs(Rms(untouched) / Rms(quiet) - 1) < 0.05);
        foreach (var (name, values) in EffectSettings.CompressorPresets)
            if (values.Length != 7) Check($"compressor preset {name} has 7 values", false);
        foreach (var (name, gains) in Equalizer.Presets)
            if (gains.Length != Equalizer.Bands.Length) Check($"EQ preset {name} has every band", false);

        var click = new float[44100 * 2];
        click[0] = click[1] = 1;
        new Reverb(new EffectSettings(), 44100).Process(click);
        Check("spatializer leaves a tail after a click", Rms(click.AsSpan(44100 / 2)) > 0.0005);

        var offCenter = new float[44100 * 2];
        for (int i = 0; i < 44100; i++) offCenter[2 * i] = (float)Math.Sin(2 * Math.PI * 440 * i / 44100);
        new Widener(new EffectSettings(), 44100).Process(offCenter);
        Check("widener feeds one side into the other", Rms(offCenter.AsSpan(44100)) >= 0 && RightRms(offCenter) > 0.01);

        // speed and pitch: a 1 kHz tone through the stretcher; zero crossings give the frequency
        Check("speed 2x halves the length and keeps the pitch", Stretched(2, 1) is { Seconds: > 0.45 and < 0.55, Hz: > 950 and < 1050 });
        Check("speed 0.5x doubles the length and keeps the pitch", Stretched(0.5, 1) is { Seconds: > 1.9 and < 2.1, Hz: > 950 and < 1050 });
        Check("pitch +12 doubles the pitch and keeps the length", Stretched(1, 2) is { Seconds: > 0.9 and < 1.1, Hz: > 1900 and < 2100 });

        // ---- visualizer math ----
        var bands = new double[32];
        var tone = Sine(1000, 1, 1024);
        Visualizer.Analyze(tone, 44100, bands, new double[1024], new double[1024]);
        var loudest = Array.IndexOf(bands, bands.Max());
        var loudestHz = 40 * Math.Pow(16000 / 40.0, (loudest + 0.5) / 32);
        Check($"visualizer finds a 1 kHz tone (band {loudest} ~{loudestHz:0} Hz, level {bands[loudest]:0.00})", loudestHz is > 600 and < 1600 && bands[loudest] > 0.8);

        // ---- decoding a real song with Windows' codecs (read-only; SKIP when there's no music) ----
        music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        foreach (var ext in new[] { "*.mp3", "*.m4a" })
        {
            var file = Directory.Exists(music)
                ? Directory.EnumerateFiles(music, ext, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).FirstOrDefault()
                : null;
            if (file == null) { Console.WriteLine($"SKIP  no {ext} in {music}"); continue; }
            try
            {
                using var dec = new Decoder(file);
                long samples = 0;
                float peak = 0;
                while (samples < dec.SampleRate * dec.Channels * 3L && dec.Read() is { } chunk) // first ~3 s
                {
                    samples += chunk.Length;
                    foreach (var s in chunk) peak = Math.Max(peak, Math.Abs(s));
                }
                Check($"{ext[1..]} decodes: {dec.Channels} ch, {dec.SampleRate} Hz, {dec.Duration:m\\:ss}, peak {peak:0.00}",
                    dec.Channels is 1 or 2 && dec.SampleRate >= 8000 && dec.Duration > TimeSpan.Zero && samples > 0 && peak is > 0 and < 4);
                dec.Seek(dec.Duration / 2);
                Check($"{ext[1..]} seeks to the middle and keeps decoding", dec.Read() is not null);

                // the whole engine through the sound card, at volume 0 so it stays silent
                using var player = new Player { Volume = 0 };
                string? failed = null;
                player.Failed += m => failed = m;
                player.Open(file);
                Thread.Sleep(1500);
                var pos = player.Position;
                player.Seek(TimeSpan.FromSeconds(60));
                Thread.Sleep(500);
                var after = player.Position;
                Check($"{ext[1..]} plays through the sound card (1.5 s -> {pos.TotalSeconds:0.00} s, after seek {after.TotalSeconds:0.0} s)",
                    failed == null && pos.TotalSeconds is > 1.0 and < 2.0 && after.TotalSeconds is > 60 and < 61.5);

                player.Fx.Speed = 2;
                player.Seek(TimeSpan.FromSeconds(30));
                Thread.Sleep(1000);
                var fast = player.Position;
                var visual = new float[2048];
                var heard = player.ReadVisual(visual) && visual.Any(v => v != 0);
                Check($"{ext[1..]} at 2x moves twice as fast (1 s -> {fast.TotalSeconds - 30:0.00} s) and reaches the visualizer",
                    failed == null && fast.TotalSeconds is > 31.2 and < 32.8 && heard);
            }
            catch (Exception ex) { Check($"{ext[1..]} decodes ({ex.Message})", false); }
        }

        Task.Run(LiveYouTubeAsync).GetAwaiter().GetResult();

        Console.WriteLine(_failed == 0 ? "ALL PASSED" : $"{_failed} FAILED");
        return _failed == 0 ? 0 : 1;
    }

    private static float[] Sine(double hz, double level, int frames)
    {
        var s = new float[frames * 2];
        for (int i = 0; i < frames; i++) s[2 * i] = s[2 * i + 1] = (float)(level * Math.Sin(2 * Math.PI * hz * i / 44100));
        return s;
    }

    private static double Rms(ReadOnlySpan<float> s)
    {
        double sum = 0;
        foreach (var x in s) sum += x * x;
        return Math.Sqrt(sum / s.Length);
    }

    private static double RightRms(float[] s)
    {
        double sum = 0;
        for (int i = 1; i < s.Length; i += 2) sum += s[i] * s[i];
        return Math.Sqrt(sum / (s.Length / 2));
    }

    /// <summary>1 s of a 1 kHz tone through the Stretcher, fed in chunks like the player does: how long it came out, and at what pitch.</summary>
    private static (double Seconds, double Hz) Stretched(double speed, double pitchRatio)
    {
        var input = Sine(1000, 0.5, 44100 * 2); // 2 s, measure per input second
        var stretcher = new Stretcher(2, 44100);
        var output = new List<float>();
        for (int at = 0; at < input.Length; at += 2304)
            output.AddRange(stretcher.Process(input[at..Math.Min(at + 2304, input.Length)], speed, pitchRatio));
        int crossings = 0, frames = output.Count / 2;
        for (int i = 1; i < frames; i++)
            if (output[2 * (i - 1)] < 0 && output[2 * i] >= 0) crossings++;
        return (frames / 44100.0 / 2, crossings / (frames / 44100.0));
    }

    /// <summary>The whole YouTube path on one real song, in %TEMP%: search, download, tag, decode. SKIP when YouTube or the network says no.</summary>
    private static async Task LiveYouTubeAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ShibaPlayerSelfTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var realStore = Store.Dir;
        Store.Dir = dir; // .webm tags go to the store, and these aren't real songs
        try
        {
            using (var lookup = new System.Net.Http.HttpClient())
            {
                var gamora = await Library.FindAsync(lookup, "ГАМОРА", "ЯД");
                Check($"live lookup finds a song only SoundCloud has: {gamora?.Source}, cover {gamora?.ArtUrl.Length > 0}", gamora is { Source: "SoundCloud", Title.Length: > 0 });
                var handSearch = await Library.SearchEverywhereAsync(lookup, "Гамора Яд");
                Check($"hand search includes SoundCloud ({handSearch.Count(h => h.Source == "SoundCloud")} rows)", handSearch.Any(h => h.Source == "SoundCloud"));
                var rick = await Library.FindAsync(lookup, "Rick Astley", "Never Gonna Give You Up");
                Check($"live lookup prefers the real album over compilations: {rick?.Album} ({rick?.Year})", rick is { Album: not "Reeling In The Decades", Year: < 2000 });
                var husky = await Library.FindAsync(lookup, "Хаски", "Панелька");
                Check($"live lookup finds a song iTunes doesn't have: {husky?.Album} / {husky?.Genre} / {husky?.Year} / track {husky?.Number}",
                    husky is { Album.Length: > 0, Genre.Length: > 0, Year: > 0, Number: > 0, ArtUrl.Length: > 0 });
            }

            var (listName, listed) = await YouTube.GetPlaylistAsync("https://www.youtube.com/playlist?list=PLQCXKl33aBijHlP4kjmQNP1TTeBlnKPoc", CancellationToken.None);
            Check($"long playlist comes back whole, past page one: {listName}, {listed.Count} songs", listed.Count > 150 && listed.All(h => h.Title.Length > 0 && h.LengthText.Length > 0));
            var (mixName, mix) = await YouTube.GetPlaylistAsync("https://www.youtube.com/watch?v=dQw4w9WgXcQ&list=RDdQw4w9WgXcQ", CancellationToken.None);
            Check($"mix link works: {mix.Count} songs", mix.Count > 5 && mixName.Length > 0 && mix.Any(h => h.Id == "dQw4w9WgXcQ"));
            try
            {
                await YouTube.GetPlaylistAsync("https://www.youtube.com/playlist?list=PLnotarealplaylist00000000000000", CancellationToken.None);
                Check("bad playlist says so", false);
            }
            catch (YouTubeException) { Check("bad playlist gets a plain error", true); }

            var hits = await YouTube.SearchAsync("rick astley never gonna give you up", CancellationToken.None);
            Check($"YouTube search finds videos ({hits.Count})", hits.Count > 0 && hits[0].Title.Length > 0 && hits[0].LengthText.Length > 0);
            var hit = hits.First(h => h.Title.Contains("Never Gonna", StringComparison.OrdinalIgnoreCase));
            var format = (await YouTube.GetVideoAsync(hit.Id, CancellationToken.None)).BestAac();
            var path = Path.Combine(dir, "Rick Astley - Never Gonna Give You Up (Official Video).m4a");
            await YouTube.DownloadAsync(format, path, _ => { }, CancellationToken.None);
            Mp4Fix.DropEditList(path);
            Check($"YouTube download is complete ({new FileInfo(path).Length / 1024} KB)", format.Size > 0 && new FileInfo(path).Length == format.Size);

            using var http = new System.Net.Http.HttpClient();
            var filled = await Library.CompleteAsync(http, Library.Read(path));
            var tagged = Library.Read(path);
            Check($"download gets tagged: {tagged.Artist} / {tagged.Album} / {tagged.Genre}, cover {tagged.HasCover}",
                filled && tagged.Artist.Contains("Rick Astley") && tagged.Album.Length > 0 && tagged.HasCover && !tagged.TitleIsFileName);

            using (var dec = new Decoder(path))
            {
                var samples = dec.Read();
                for (int i = 0; i < 20 && samples is { Length: 0 }; i++) samples = dec.Read();
                Check($"tagged download still decodes ({dec.SampleRate} Hz, {dec.Duration:m\\:ss})", samples is { Length: > 0 } && dec.Duration > TimeSpan.Zero);
            }

            // the "Highest" stream, and MP3 made from it
            var info = await YouTube.GetVideoAsync(hit.Id, CancellationToken.None);
            var best = info.BestAudio();
            var bestPath = Path.Combine(dir, "Rick Astley - Never Gonna Give You Up" + (best.Mime.StartsWith("audio/mp4") ? ".m4a" : ".webm"));
            await YouTube.DownloadAsync(best, bestPath, _ => { }, CancellationToken.None);
            if (best.Mime.StartsWith("audio/mp4")) Mp4Fix.DropEditList(bestPath);
            var bestKbps = best.Bitrate / 1000;
            var mp3Source = bestPath;
            try
            {
                using var dec = new Decoder(bestPath);
                var samples = dec.Read();
                for (int i = 0; i < 20 && samples is { Length: 0 }; i++) samples = dec.Read();
                Check($"highest stream decodes ({best.Mime.Split(';')[0]}, {bestKbps} kbps, {dec.SampleRate} Hz)", samples is { Length: > 0 });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SKIP  this PC can't decode {best.Mime}: {ex.Message}. The app falls back to M4A.");
                mp3Source = path;
            }
            if (mp3Source == bestPath)
            {
                var read = Library.Read(bestPath);
                filled = await Library.CompleteAsync(http, read);
                var again = Library.Read(bestPath);
                Check($"highest stream gets tagged: {again.Artist} / {again.Album}, cover {again.HasCover}", filled && again.Artist.Contains("Rick Astley") && again.HasCover);
                try
                {
                    using var taggedDec = new Decoder(bestPath);
                    var s2 = taggedDec.Read();
                    for (int i = 0; i < 20 && s2 is { Length: 0 }; i++) s2 = taggedDec.Read();
                    Check("tagged highest stream still decodes", s2 is { Length: > 0 });
                }
                catch (Exception ex) { Check($"tagged highest stream still decodes ({ex.Message})", false); mp3Source = path; }
            }

            var mp3 = Path.Combine(dir, "Rick Astley - Never Gonna Give You Up.mp3");
            Mp3Encoder.Encode(mp3Source, mp3, 320);
            using (var dec = new Decoder(mp3))
            {
                var kbps = new FileInfo(mp3).Length * 8 / 1000 / Math.Max(dec.Duration.TotalSeconds, 1);
                Check($"MP3 320 converts ({kbps:0} kbps, {dec.Duration:m\\:ss})", kbps is > 300 and < 340 && dec.Duration.TotalSeconds > 200);
            }
            // choosing by hand overwrites what's there, in each format
            var chosen = (await Library.SearchEverywhereAsync(http, "Хаски Панелька")).First(h => h is { Source: "Deezer", Artist: "Хаски" } && h.Title.StartsWith("Панелька"));
            foreach (var target in new[] { path, mp3Source == bestPath ? bestPath : path })
            {
                await Library.ApplyAsync(http, Library.Read(target), chosen);
                var applied = Library.Read(target);
                using var after = new Decoder(target);
                var audio = after.Read();
                for (int i = 0; i < 20 && audio is { Length: 0 }; i++) audio = after.Read();
                Check($"picked song replaces the tags on {Path.GetExtension(target)} and it still plays: {applied.Album == chosen.Album}, genre set {applied.Genre.Length > 0}",
                    applied is { Artist: "Хаски", TitleIsFileName: false, HasCover: true } && applied.Album == chosen.Album && applied.Genre.Length > 0 && audio is { Length: > 0 });
            }

            await Library.CompleteAsync(http, Library.Read(mp3));
            var mp3Tags = Library.Read(mp3);
            Check($"MP3 gets tagged: {mp3Tags.Artist} / {mp3Tags.Album}, cover {mp3Tags.HasCover}", mp3Tags.Artist.Contains("Rick Astley") && mp3Tags.HasCover);
        }
        catch (Exception ex) when (ex is YouTubeException or System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"SKIP  YouTube: {ex.Message}");
        }
        catch (Exception ex) { Check($"YouTube path threw {ex.GetType().Name}: {ex.Message}", false); }
        finally
        {
            Store.Dir = realStore;
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Output RMS / input RMS for a stereo sine at <paramref name="hz"/> after the filters settle.</summary>
    private static double Gain(Equalizer eq, double hz)
    {
        const int rate = 44100;
        var p = new EqProcessor(eq, 2, rate);
        var buf = new float[rate * 2]; // 1 s stereo
        for (int i = 0; i < rate; i++) buf[2 * i] = buf[2 * i + 1] = (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate));
        var input = Rms(buf.AsSpan(rate)); // second half, after warm-up
        p.Process(buf);
        return Rms(buf.AsSpan(rate)) / input;

        static double Rms(Span<float> s)
        {
            double sum = 0;
            foreach (var x in s) sum += x * x;
            return Math.Sqrt(sum / s.Length);
        }
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok) _failed++;
    }
}
