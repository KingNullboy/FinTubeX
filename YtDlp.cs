using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FinTubeX;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int Count { get; set; } = 10;
}

public class SearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public double? Duration { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Thumbnail { get; set; } = string.Empty;
}

public class DownloadRequest
{
    /// <summary>A link, or plain text (treated as "download the top search hit").</summary>
    public string Url { get; set; } = string.Empty;
    public bool AudioOnly { get; set; }
    /// <summary>Max height in pixels ("720"), or "best".</summary>
    public string Quality { get; set; } = string.Empty;

    // Advanced
    public bool Playlist { get; set; }
    public bool EmbedThumbnail { get; set; }
    public bool EmbedMetadata { get; set; } = true;
    public bool EmbedSubtitles { get; set; }
    public string SubtitleLangs { get; set; } = "en.*";
    public bool SponsorBlock { get; set; }
    public string CookiesFile { get; set; } = string.Empty;
    public string RateLimit { get; set; } = string.Empty;
    public string OutputTemplate { get; set; } = string.Empty;
    public string CustomFlags { get; set; } = string.Empty;
}

public class DownloadJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Target { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string State { get; set; } = "queued"; // queued | running | done | failed | cancelled
    public double Percent { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime Created { get; } = DateTime.UtcNow;

    [JsonIgnore]
    public CancellationTokenSource Cts { get; } = new();
}

public static class YtDlp
{
    private static readonly List<DownloadJob> Jobs = new();
    private static readonly SemaphoreSlim Gate = new(2); // at most 2 downloads at once (it's a Pi)
    private static readonly Regex Percent = new(@"\[download\]\s+(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
    private static readonly Regex Dest = new(@"(?:Destination:|Merging formats into)\s+""?(.+?)""?\s*$", RegexOptions.Compiled);

    // ---------- search ----------

    public static async Task<List<SearchResult>> SearchAsync(string query, int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 25);
        var args = new List<string>
        {
            "--flat-playlist", "--dump-single-json", "--no-warnings",
            "--", $"ytsearch{count}:{query}"
        };

        var (code, stdout, stderr) = await RunToEndAsync(args, TimeSpan.FromSeconds(40), ct);
        if (code != 0)
        {
            throw new InvalidOperationException(LastLine(stderr) ?? $"yt-dlp exited with code {code}");
        }

        var results = new List<SearchResult>();
        using var doc = JsonDocument.Parse(stdout);
        if (!doc.RootElement.TryGetProperty("entries", out var entries))
        {
            return results;
        }

        foreach (var e in entries.EnumerateArray())
        {
            var id = Str(e, "id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            results.Add(new SearchResult
            {
                Id = id,
                Title = Str(e, "title"),
                Channel = Str(e, "channel") is { Length: > 0 } ch ? ch : Str(e, "uploader"),
                Duration = e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null,
                Url = Str(e, "url") is { Length: > 0 } u ? u : $"https://www.youtube.com/watch?v={id}",
                Thumbnail = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg"
            });
        }

        return results;
    }

    // ---------- download ----------

    public static DownloadJob Enqueue(DownloadRequest req)
    {
        var raw = req.Url.Trim();
        var target = Regex.IsMatch(raw, @"^https?://", RegexOptions.IgnoreCase) ? raw : $"ytsearch1:{raw}";
        var job = new DownloadJob { Target = target, Title = raw };

        lock (Jobs)
        {
            Jobs.Add(job);
            while (Jobs.Count > 40)
            {
                var old = Jobs.FirstOrDefault(j => j.State is "done" or "failed" or "cancelled");
                if (old is null) break;
                Jobs.Remove(old);
            }
        }

        _ = Task.Run(() => RunJobAsync(job, req));
        return job;
    }

    public static List<DownloadJob> ListJobs()
    {
        lock (Jobs)
        {
            return Jobs.OrderByDescending(j => j.Created).ToList();
        }
    }

    public static bool Cancel(Guid id)
    {
        DownloadJob? job;
        lock (Jobs)
        {
            job = Jobs.FirstOrDefault(j => j.Id == id);
        }

        if (job is null) return false;
        job.Cts.Cancel();
        return true;
    }

    private static async Task RunJobAsync(DownloadJob job, DownloadRequest req)
    {
        try
        {
            await Gate.WaitAsync(job.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            job.State = "cancelled";
            return;
        }

        try
        {
            job.State = "running";
            var args = BuildDownloadArgs(req, job.Target);
            using var p = new Process { StartInfo = CreateStartInfo(args) };
            p.Start();

            using var reg = job.Cts.Token.Register(() =>
            {
                try { p.Kill(true); } catch { /* already gone */ }
            });

            var pumps = Task.WhenAll(Pump(p.StandardOutput, job), Pump(p.StandardError, job));
            await p.WaitForExitAsync();
            await pumps;

            if (job.Cts.IsCancellationRequested)
            {
                job.State = "cancelled";
            }
            else if (p.ExitCode == 0)
            {
                job.State = "done";
                job.Percent = 100;
            }
            else
            {
                job.State = "failed";
            }
        }
        catch (Win32Exception)
        {
            job.State = "failed";
            job.Message = $"Could not start yt-dlp at '{Cfg.YtDlpPath}'. Check the path in Settings.";
        }
        catch (Exception ex)
        {
            job.State = "failed";
            job.Message = ex.Message;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task Pump(StreamReader reader, DownloadJob job)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            if (line.Length == 0) continue;

            var m = Percent.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
            {
                job.Percent = pct;
                continue;
            }

            var d = Dest.Match(line);
            if (d.Success)
            {
                job.Title = Path.GetFileNameWithoutExtension(d.Groups[1].Value);
            }

            job.Message = line.Length > 300 ? line[..300] : line;
        }
    }

    private static List<string> BuildDownloadArgs(DownloadRequest req, string target)
    {
        var cfg = Cfg;
        var a = new List<string>
        {
            "--newline", "--no-colors",
            "--js-runtimes", string.IsNullOrWhiteSpace(cfg.DenoPath) ? "deno" : $"deno:{cfg.DenoPath}"
        };

        if (!string.IsNullOrWhiteSpace(cfg.RemoteComponents))
        {
            a.AddRange(new[] { "--remote-components", cfg.RemoteComponents });
        }

        if (!string.IsNullOrWhiteSpace(cfg.FfmpegPath))
        {
            a.AddRange(new[] { "--ffmpeg-location", cfg.FfmpegPath });
        }

        a.Add(req.Playlist ? "--yes-playlist" : "--no-playlist");

        // Quality. Sorting (instead of a hard filter) means we still get *something* if 720p doesn't exist,
        // and h264/aac is preferred so a Pi can direct-play instead of transcoding.
        var quality = string.IsNullOrWhiteSpace(req.Quality) ? cfg.DefaultQuality : req.Quality;
        if (req.AudioOnly)
        {
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0" });
        }
        else
        {
            var res = quality.Equals("best", StringComparison.OrdinalIgnoreCase) || !int.TryParse(quality, out var h)
                ? "res"
                : $"res:{h}";
            a.AddRange(new[] { "-f", "bv*+ba/b", "-S", $"{res},vcodec:h264,acodec:aac", "--merge-output-format", "mp4" });
        }

        // Output location (templates can't escape the download folder).
        var root = Path.GetFullPath(cfg.DownloadPath);
        var template = string.IsNullOrWhiteSpace(req.OutputTemplate)
            ? (req.Playlist ? "%(playlist_title)s/%(title)s [%(id)s].%(ext)s" : "%(title)s [%(id)s].%(ext)s")
            : req.OutputTemplate.Trim();
        var full = Path.GetFullPath(Path.Combine(root, template));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Output template must stay inside the download folder.");
        }

        a.AddRange(new[] { "-o", full });

        if (req.EmbedMetadata) a.Add("--embed-metadata");
        if (req.EmbedThumbnail) a.Add("--embed-thumbnail");
        if (req.EmbedSubtitles && !req.AudioOnly)
        {
            a.AddRange(new[] { "--write-subs", "--embed-subs", "--sub-langs", string.IsNullOrWhiteSpace(req.SubtitleLangs) ? "en.*" : req.SubtitleLangs });
        }

        if (req.SponsorBlock) a.AddRange(new[] { "--sponsorblock-remove", "sponsor,selfpromo,interaction" });
        if (!string.IsNullOrWhiteSpace(req.CookiesFile)) a.AddRange(new[] { "--cookies", req.CookiesFile.Trim() });
        if (!string.IsNullOrWhiteSpace(req.RateLimit)) a.AddRange(new[] { "-r", req.RateLimit.Trim() });

        if (!string.IsNullOrWhiteSpace(req.CustomFlags))
        {
            a.AddRange(SplitArgs(req.CustomFlags));
        }

        // "--" ends option parsing so a hostile URL can never be read as a flag.
        a.Add("--");
        a.Add(target);
        return a;
    }

    // ---------- diagnostics ----------

    public static async Task<Dictionary<string, string>> DoctorAsync(CancellationToken ct)
    {
        var cfg = Cfg;
        var report = new Dictionary<string, string>();

        report["yt-dlp"] = await Probe(cfg.YtDlpPath, "--version", ct);
        report["deno"] = await Probe(string.IsNullOrWhiteSpace(cfg.DenoPath) ? "deno" : cfg.DenoPath, "--version", ct, firstLineOnly: true);
        report["ffmpeg"] = await Probe(cfg.FfmpegPath, "-version", ct, firstLineOnly: true);

        try
        {
            Directory.CreateDirectory(cfg.DownloadPath);
            var probe = Path.Combine(cfg.DownloadPath, ".fintubex-write-test");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            report["download folder"] = $"writable ({cfg.DownloadPath})";
        }
        catch (Exception ex)
        {
            report["download folder"] = $"NOT writable: {ex.Message}";
        }

        return report;
    }

    private static async Task<string> Probe(string exe, string arg, CancellationToken ct, bool firstLineOnly = false)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, arg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi)!;
            var text = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            if (firstLineOnly) text = text.Split('\n')[0];
            return p.ExitCode == 0 ? text : $"exit {p.ExitCode}";
        }
        catch (Exception ex)
        {
            return $"MISSING ({ex.Message})";
        }
    }

    // ---------- helpers ----------

    private static PluginConfiguration Cfg => Plugin.Instance!.Configuration;

    private static ProcessStartInfo CreateStartInfo(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Cfg.YtDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a); // no shell, no string concatenation, no injection
        }

        var cache = Plugin.Instance!.CacheDir;
        psi.Environment["HOME"] = cache;
        psi.Environment["XDG_CACHE_HOME"] = cache;
        psi.Environment["DENO_DIR"] = Path.Combine(cache, "deno");
        return psi;
    }

    private static async Task<(int Code, string Out, string Err)> RunToEndAsync(IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var p = new Process { StartInfo = CreateStartInfo(args) };
        p.Start();
        var o = p.StandardOutput.ReadToEndAsync(cts.Token);
        var e = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, await o, await e);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private static string? LastLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();

    /// <summary>Minimal shell-style splitter: spaces separate args, quotes group them.</summary>
    private static IEnumerable<string> SplitArgs(string s)
    {
        var cur = new StringBuilder();
        char quote = '\0';
        bool has = false;
        foreach (var ch in s)
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0'; else cur.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                has = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0 || has)
                {
                    yield return cur.ToString();
                    cur.Clear();
                    has = false;
                }
            }
            else
            {
                cur.Append(ch);
            }
        }

        if (cur.Length > 0 || has) yield return cur.ToString();
    }
}
