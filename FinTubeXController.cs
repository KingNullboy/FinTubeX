using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FinTubeX.Api;

/// <summary>
/// Everything here shells out to yt-dlp, so it is admin-only. Without [Authorize] anyone who can reach
/// port 8096 could queue downloads (and "custom flags" would be remote code execution via --exec).
/// To let regular users download, you'd drop CustomFlags/CookiesFile support first, then relax the policy.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("FinTubeX")]
[Produces("application/json")]
public class FinTubeXController : ControllerBase
{
    [HttpPost("Search")]
    public async Task<ActionResult<List<SearchResult>>> Search([FromBody] SearchRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("Type something to search for.");
        }

        try
        {
            return Ok(await YtDlp.SearchAsync(request.Query.Trim(), request.Count, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not 5xx: Cloudflare replaces origin 502/5xx bodies with its own error page.
            return UnprocessableEntity($"Search failed: {ex.Message}");
        }
    }

    [HttpPost("Download")]
    public ActionResult Download([FromBody] DownloadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("A link or search text is required.");
        }

        var job = YtDlp.Enqueue(request);
        return Ok(new { JobId = job.Id, job.Target });
    }

    [HttpGet("Jobs")]
    public ActionResult<List<DownloadJob>> Jobs() => Ok(YtDlp.ListJobs());

    [HttpPost("Jobs/{id:guid}/Cancel")]
    public ActionResult Cancel(Guid id) => YtDlp.Cancel(id) ? NoContent() : NotFound();

    /// <summary>Checks that yt-dlp, deno, ffmpeg and the download folder are all usable from inside the container.</summary>
    [HttpGet("Doctor")]
    public async Task<ActionResult<Dictionary<string, string>>> Doctor(CancellationToken ct) =>
        Ok(await YtDlp.DoctorAsync(ct));
}
