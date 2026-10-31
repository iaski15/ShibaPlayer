using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ShibaPlayer;

// Copied from ShibaDownloader's YouTube.cs (player + download). When every video fails there, the fix belongs here too.

public sealed class YouTubeException(string message) : Exception(message);

public sealed record MediaFormat(string Url, string Mime, long Size, int Height, long Bitrate);

public sealed record VideoInfo(string Title, string Uploader, int LengthSeconds, IReadOnlyList<MediaFormat> Formats)
{
    /// <summary>The highest-bitrate audio YouTube offers for this video (normally Opus in WebM).</summary>
    public MediaFormat BestAudio() =>
        Formats.Where(f => f.Mime.StartsWith("audio/")).OrderByDescending(f => f.Bitrate).FirstOrDefault()
        ?? throw new YouTubeException("This video has no audio stream.");

    /// <summary>YouTube's AAC stream: saved as .m4a as it comes, no re-encoding.</summary>
    public MediaFormat BestAac() =>
        Formats.Where(f => f.Mime.StartsWith("audio/mp4")).OrderByDescending(f => f.Bitrate).FirstOrDefault()
        ?? throw new YouTubeException("This video has no M4A audio stream.");
}

/// <summary>
/// Talks to YouTube's InnerTube player API directly, posing as the visionOS app.
/// That client gets plain stream URLs back - no signature cipher, no JS player, no PO token.
/// ponytail: lives as long as YouTube leaves the VISIONOS client alone; when it breaks, swap the
/// client block below for whatever yt-dlp's _base.py INNERTUBE_CLIENTS currently marks REQUIRE_JS_PLAYER=False.
/// </summary>
public static class YouTube
{
    private const string UserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
    private const string ClientName = "VISIONOS";
    private const string ClientVersion = "1.02";
    private const string ClientId = "101";

    // YouTube throttles long single requests; yt-dlp uses the same 10 MB range chunks.
    private const long ChunkSize = 10 * 1024 * 1024;

    // Stream URLs are locked to the IP that asked for them (ip= in the URL). Over IPv6, Windows can send the player
    // request and the download from different addresses (temporary/privacy ones), and googlevideo answers 403.
    // So connect over IPv4 first, where every connection leaves through the same NAT address; IPv6 only as fallback.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (ctx, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true }; // dual-mode: v4 and v6
            try
            {
                await socket.ConnectAsync(addresses.OrderBy(a => a.AddressFamily != AddressFamily.InterNetwork).ToArray(), ctx.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });

    // Requests without a visitor ID get "confirm you're not a bot" on many videos; the homepage hands one out.
    private static string? _visitorData;

    private static readonly Regex IdRegex = new(
        @"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/|/v/)([A-Za-z0-9_-]{11})", RegexOptions.Compiled);

    public static string? ParseVideoId(string url)
    {
        var m = IdRegex.Match(url);
        if (m.Success) return m.Groups[1].Value;
        url = url.Trim();
        return Regex.IsMatch(url, "^[A-Za-z0-9_-]{11}$") ? url : null;
    }

    public static async Task<VideoInfo> GetVideoAsync(string url, CancellationToken ct)
    {
        var id = ParseVideoId(url) ?? throw new YouTubeException("That doesn't look like a valid YouTube link.");

        _visitorData ??= await FetchVisitorDataAsync(ct);

        var body = JsonSerializer.Serialize(new
        {
            context = new
            {
                client = new
                {
                    clientName = ClientName,
                    clientVersion = ClientVersion,
                    deviceMake = "Apple",
                    deviceModel = "RealityDevice17,1",
                    osName = "visionOS",
                    osVersion = "26.5.23O471",
                    userAgent = UserAgent,
                    visitorData = _visitorData,
                    hl = "en",
                    gl = "US"
                }
            },
            videoId = id,
            contentCheckOk = true,
            racyCheckOk = true
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Add("X-YouTube-Client-Name", ClientId);
        req.Headers.Add("X-YouTube-Client-Version", ClientVersion);
        req.Headers.Add("Origin", "https://www.youtube.com");
        if (_visitorData is not null) req.Headers.Add("X-Goog-Visitor-Id", _visitorData);

        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 429)
            throw new YouTubeException("YouTube is rate-limiting this connection. Wait a bit and try again.");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
        var root = doc.RootElement;

        var status = root.TryGetProperty("playabilityStatus", out var ps) ? ps : default;
        var state = status.ValueKind == JsonValueKind.Object ? Str(status, "status") : null;
        if (state != "OK")
            throw MapPlayability(state, status.ValueKind == JsonValueKind.Object ? Str(status, "reason") : null);

        var details = root.GetProperty("videoDetails");
        if (details.TryGetProperty("isLive", out var live) && live.GetBoolean())
            throw new YouTubeException("Live streams aren't supported.");

        var formats = new List<MediaFormat>();
        if (root.TryGetProperty("streamingData", out var sd) && sd.TryGetProperty("adaptiveFormats", out var af))
        {
            foreach (var f in af.EnumerateArray())
            {
                // Ciphered formats need YouTube's JS player to unscramble - skip them.
                if (Str(f, "url") is not { } u) continue;
                formats.Add(new MediaFormat(
                    u,
                    Str(f, "mimeType") ?? "",
                    long.TryParse(Str(f, "contentLength"), out var len) ? len : 0,
                    f.TryGetProperty("height", out var h) ? h.GetInt32() : 0,
                    f.TryGetProperty("bitrate", out var br) ? br.GetInt64() : 0));
            }
        }
        if (formats.Count == 0)
            throw new YouTubeException("YouTube returned no downloadable streams for this video.");

        return new VideoInfo(
            Str(details, "title") ?? "video",
            Str(details, "author") ?? "",
            int.TryParse(Str(details, "lengthSeconds"), out var secs) ? secs : 0,
            formats);
    }

    private static async Task<string?> FetchVisitorDataAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/");
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await Http.SendAsync(req, ct);
            var m = Regex.Match(await resp.Content.ReadAsStringAsync(ct), "\"VISITOR_DATA\":\"([^\"]+)\"");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }
        catch (HttpRequestException)
        {
            return null; // the player request still works for many videos without it
        }
    }

    private static readonly object WebContext = new { client = new { clientName = "WEB", clientVersion = "2.20250925.01.00", hl = "en", gl = "US" } };

    /// <summary>The list= part of a YouTube link, or null.</summary>
    public static string? ParsePlaylistId(string url)
    {
        var m = Regex.Match(url, @"[?&]list=([A-Za-z0-9_-]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Every playable song in a playlist or album. Mixes (RD...) are generated per video, so they need one and YouTube
    /// only gives a couple of dozen songs at a time. Private lists and "Watch later" need a sign-in, so they fail.</summary>
    public static async Task<(string Title, IReadOnlyList<YouTubeHit> Hits)> GetPlaylistAsync(string url, CancellationToken ct)
    {
        var id = ParsePlaylistId(url) ?? throw new YouTubeException("That link has no playlist in it.");
        var hits = new List<YouTubeHit>();
        var title = "";
        try
        {
            if (id.StartsWith("RD"))
            {
                var video = ParseVideoId(url) ?? (id.Length == 13 ? id[2..] : throw new YouTubeException("Open a song in that mix and copy the link from there."));
                using var mix = await PostWebAsync("next", new { context = WebContext, videoId = video, playlistId = id }, ct);
                // only the mix itself: the rest of this page is "up next" suggestions
                if (Dig(mix.RootElement, "contents", "twoColumnWatchNextResults", "playlist", "playlist") is { } panel)
                {
                    title = Str(panel, "title") ?? "";
                    string? ignored = null;
                    CollectVideos(panel, hits, ref ignored);
                }
            }
            else
            {
                var body = (object)new { context = WebContext, browseId = "VL" + id };
                while (true)
                {
                    using var page = await PostWebAsync("browse", body, ct);
                    if (title.Length == 0 && Dig(page.RootElement, "metadata", "playlistMetadataRenderer") is { } meta) title = Str(meta, "title") ?? "";
                    string? next = null;
                    var before = hits.Count;
                    CollectVideos(page.RootElement, hits, ref next);
                    if (next == null || hits.Count == before) break;
                    body = new { context = WebContext, continuation = next };
                }
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
        {
            throw new YouTubeException("YouTube wouldn't open that playlist. It may be private or deleted.");
        }
        var distinct = hits.DistinctBy(h => h.Id).ToList();
        if (distinct.Count == 0) throw new YouTubeException("YouTube shows no songs in that playlist. It may be private or empty.");
        return (title.Length > 0 ? title : "YouTube playlist", distinct);
    }

    private static async Task<JsonDocument> PostWebAsync(string endpoint, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://www.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var resp = await Http.SendAsync(req, ct);
        if ((int)resp.StatusCode == 429)
            throw new YouTubeException("YouTube is rate-limiting this connection. Wait a bit and try again.");
        resp.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(ct));
    }

    /// <summary>InnerTube search as the WEB client (videos only, live streams left out).</summary>
    public static async Task<IReadOnlyList<YouTubeHit>> SearchAsync(string query, CancellationToken ct)
    {
        using var doc = await PostWebAsync("search", new
        {
            context = WebContext,
            query,
            @params = "EgIQAQ%3D%3D" // "type: video" filter, same value yt-dlp sends
        }, ct);
        var hits = new List<YouTubeHit>();
        string? ignored = null;
        CollectVideos(doc.RootElement, hits, ref ignored);
        return hits;
    }

    /// <summary>Finds song rows anywhere in a response, in each layout YouTube uses, and the token for the next page.
    /// Public for the self-test.</summary>
    public static void CollectVideos(JsonElement e, List<YouTubeHit> hits, ref string? nextPage)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray()) CollectVideos(x, hits, ref nextPage);
        if (e.ValueKind != JsonValueKind.Object) return;

        foreach (var p in e.EnumerateObject())
        {
            switch (p.Name)
            {
                case "videoRenderer" or "playlistVideoRenderer" or "playlistPanelVideoRenderer":
                    if (Str(p.Value, "videoId") is { } id && Dig(p.Value, "lengthText") is { } lt && Text(lt) is { Length: > 0 } len)
                        hits.Add(new YouTubeHit(id, Dig(p.Value, "title") is { } t ? Text(t) : "",
                            (Dig(p.Value, "ownerText") ?? Dig(p.Value, "shortBylineText") ?? Dig(p.Value, "longBylineText")) is { } o ? Text(o) : "", len));
                    continue;
                case "lockupViewModel":
                    if (Str(p.Value, "contentType") == "LOCKUP_CONTENT_TYPE_VIDEO" && Str(p.Value, "contentId") is { } lockupId && BadgeLength(p.Value) is { } badge)
                        hits.Add(new YouTubeHit(lockupId,
                            Dig(p.Value, "metadata", "lockupMetadataViewModel", "title") is { } lockupTitle ? Str(lockupTitle, "content") ?? "" : "",
                            FirstContent(p.Value, "metadataParts") ?? "", badge));
                    continue;
                case "continuationItemRenderer" or "continuationItemViewModel":
                    nextPage ??= FindToken(p.Value);
                    continue;
            }
            CollectVideos(p.Value, hits, ref nextPage);
        }
    }

    private static JsonElement? Dig(JsonElement e, params string[] path)
    {
        foreach (var name in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e)) return null;
        }
        return e;
    }

    private static string Text(JsonElement e) =>
        Str(e, "simpleText") ?? (e.TryGetProperty("runs", out var r) && r.GetArrayLength() > 0 ? Str(r[0], "text") ?? "" : "");

    // Hidden and live rows have a badge without a time in it.
    private static string? BadgeLength(JsonElement lockup)
    {
        foreach (var badge in Everything(lockup, "thumbnailBadgeViewModel"))
            if (Str(badge, "text") is { } text && text.Contains(':')) return text;
        return null;
    }

    private static string? FirstContent(JsonElement lockup, string parts)
    {
        foreach (var list in Everything(lockup, parts))
            if (list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 && Dig(list[0], "text") is { } text)
                return Str(text, "content");
        return null;
    }

    private static string? FindToken(JsonElement e)
    {
        foreach (var command in Everything(e, "continuationCommand"))
            if (Str(command, "token") is { } token) return token;
        return null;
    }

    private static IEnumerable<JsonElement> Everything(JsonElement e, string name)
    {
        if (e.ValueKind == JsonValueKind.Array)
            foreach (var x in e.EnumerateArray())
                foreach (var hit in Everything(x, name)) yield return hit;
        if (e.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name == name) yield return p.Value;
            foreach (var hit in Everything(p.Value, name)) yield return hit;
        }
    }

    /// <summary>Downloads a stream in ranged chunks; <paramref name="onBytes"/> gets each written byte count.</summary>
    public static async Task DownloadAsync(MediaFormat format, string path, Action<long> onBytes, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        long pos = 0;
        var failures = 0;

        do
        {
            var chunkStart = pos;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, format.Url);
                req.Headers.UserAgent.ParseAdd(UserAgent);
                if (format.Size > 0)
                    req.Headers.Range = new RangeHeaderValue(pos, Math.Min(pos + ChunkSize, format.Size) - 1);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 403)
                    throw new YouTubeException("YouTube refused the download (403). The stream link may have expired - try again.");
                resp.EnsureSuccessStatusCode();

                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                int n;
                while ((n = await s.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    pos += n;
                    onBytes(n);
                }
                if (format.Size <= 0) break;
                if (pos == chunkStart)
                    throw new YouTubeException("The download stopped early (the server sent no data). Try again.");
            }
            // Only ranged downloads can resume; without a size a retry would append a second copy to the file.
            catch (Exception ex) when (ex is HttpRequestException or IOException && format.Size > 0 && !ct.IsCancellationRequested && ++failures <= 5)
            {
                // Dropped connection mid-chunk: the next request resumes from pos.
                await Task.Delay(1000 * failures, ct);
            }
        } while (pos < format.Size);
    }

    private static YouTubeException MapPlayability(string? status, string? reason)
    {
        var r = (reason ?? "").ToLowerInvariant();
        if (r.Contains("private")) return new("That video is private.");
        if (r.Contains("members") || r.Contains("join this channel")) return new("That video is members-only.");
        if (r.Contains("age") || r.Contains("inappropriate")) return new("That video is age-restricted and needs sign-in.");
        if (r.Contains("bot")) return new("YouTube is asking for a bot-check. Try again later or from another network.");
        if (status == "ERROR" || r.Contains("unavailable")) return new("That video is unavailable (deleted or region-locked).");
        return new(reason is { Length: > 0 } ? reason : $"YouTube won't play this video ({status ?? "no status"}).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>From ShibaDownloader's Mp4Mux: makes YouTube's fragmented M4A decodable by Media Foundation.</summary>
public static class Mp4Fix
{
    /// <summary>
    /// Media Foundation decodes nothing (0xC00D4A44) from a fragmented MP4 whose track has an edit list, and some
    /// YouTube audio streams carry one. Renaming the 'edts' box to 'free' in place hides it; all it did was trim
    /// the encoder's priming samples (a few ms).
    /// </summary>
    public static void DropEditList(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
        var bytes = ReadTopLevel(fs, "moov");
        (int Off, int Size) edts;
        try { edts = FindPath(bytes, "trak", "edts"); }
        catch (InvalidDataException) { return; } // no edit list - nothing to do
        fs.Position = TopLevel(fs).First(b => b.Type == "moov").Off + edts.Off + 4;
        fs.Write("free"u8);
    }

    private static List<(string Type, long Off, long Size, int HeaderLen)> TopLevel(Stream s)
    {
        var list = new List<(string, long, long, int)>();
        var hdr = new byte[16];
        for (long pos = 0; pos + 8 <= s.Length;)
        {
            s.Position = pos;
            s.ReadExactly(hdr, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            var headerLen = 8;
            if (size == 1)
            {
                s.ReadExactly(hdr, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(8));
                headerLen = 16;
            }
            else if (size == 0) size = s.Length - pos;
            if (size < headerLen || pos + size > s.Length)
                throw new InvalidDataException("Downloaded stream is truncated.");
            list.Add((Encoding.ASCII.GetString(hdr, 4, 4), pos, size, headerLen));
            pos += size;
        }
        return list;
    }

    private static byte[] ReadTopLevel(Stream s, string type)
    {
        var (_, off, size, _) = TopLevel(s).FirstOrDefault(b => b.Type == type);
        if (size == 0) throw new InvalidDataException($"Downloaded stream has no '{type}' box.");
        var bytes = new byte[size];
        s.Position = off;
        s.ReadExactly(bytes);
        return bytes;
    }

    private static (int Off, int Size) Find(byte[] b, int start, int end, string type)
    {
        for (var pos = start; pos + 8 <= end;)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos));
            if (size < 8) break;
            if (Encoding.ASCII.GetString(b, pos + 4, 4) == type) return (pos, size);
            pos += size;
        }
        throw new InvalidDataException($"MP4 box '{type}' not found.");
    }

    // Path below a top-level box (moov/moof) whose 8-byte header starts at 0.
    private static (int Off, int Size) FindPath(byte[] b, params string[] path)
    {
        (int Off, int Size) box = (0, b.Length);
        foreach (var type in path)
            box = Find(b, box.Off + 8, box.Off + box.Size, type);
        return box;
    }
}

public sealed record YouTubeHit(string Id, string Title, string Channel, string LengthText)
{
    public string ThumbUrl => $"https://i.ytimg.com/vi/{Id}/mqdefault.jpg";

    /// <summary>"Artist - Title.m4a", the form metadata lookups understand. Titles that already name the artist win;
    /// otherwise the channel is the artist ("Rick Astley - Topic", "RickAstleyVEVO").</summary>
    public static string FileName(string title, string channel)
    {
        title = title.Replace(" \u2013 ", " - ").Replace(" \u2014 ", " - ");
        channel = Regex.Replace(channel, @"\s*-\s*Topic$|VEVO$", "", RegexOptions.IgnoreCase).Trim();
        var name = title.Contains(" - ") || channel.Length == 0 ? title : $"{channel} - {title}";
        name = string.Concat(name.Split(Path.GetInvalidFileNameChars()));
        if (name.Length > 120) name = name[..120];
        name = name.Trim().TrimEnd('.', '-', ' ');
        return (name.Length > 0 ? name : "YouTube song") + ".m4a";
    }
}

/// <summary>One line in the YouTube results; the button's text tracks the download.</summary>
public sealed class YouTubeRow(YouTubeHit hit, string action) : INotifyPropertyChanged
{
    public const string Download = "Download", Play = "\u25B6 Play";

    public YouTubeHit Hit { get; } = hit;
    public string FileName { get; } = YouTubeHit.FileName(hit.Title, hit.Channel);

    public string Action
    {
        get => action;
        set { action = value; PropertyChanged?.Invoke(this, new(nameof(Action))); }
    }

    public bool CanClick
    {
        get => _canClick;
        set { _canClick = value; PropertyChanged?.Invoke(this, new(nameof(CanClick))); }
    }
    private bool _canClick = true;

    public event PropertyChangedEventHandler? PropertyChanged;
}
