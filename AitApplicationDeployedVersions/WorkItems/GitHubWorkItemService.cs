using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using AitApplicationDeployedVersions.Security;

namespace AitApplicationDeployedVersions.WorkItems;

public sealed class GitHubWorkItemService
{
    private static readonly Regex AbWorkItemRegex = new(@"\bAB#\s*(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient http;

    public GitHubWorkItemService(HttpClient http)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public static string? TryGetGitHubToken(string credentialTargetName, string envVarName)
    {
        var token = WindowsCredentialManager.TryReadSecret(credentialTargetName);
        if (!string.IsNullOrWhiteSpace(token)) return token;

        token = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    public async Task<GitHubWorkItemFetchResult> FetchAsync(
        string repo,
        string baselineSha,
        string currentSha,
        string gitHubToken,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo is required", nameof(repo));
        if (string.IsNullOrWhiteSpace(baselineSha)) throw new ArgumentException("baselineSha is required", nameof(baselineSha));
        if (string.IsNullOrWhiteSpace(currentSha)) throw new ArgumentException("currentSha is required", nameof(currentSha));
        if (string.IsNullOrWhiteSpace(gitHubToken)) throw new ArgumentException("gitHubToken is required", nameof(gitHubToken));

        using var requestScope = new GitHubRequestScope(http, gitHubToken);

        var access = await CheckRepoAccessAsync(repo, cancellationToken);
        if (!access.IsOk)
            return GitHubWorkItemFetchResult.Failure(access.Error ?? "GitHub repo access check failed");

        var compare = await CompareAsync(repo, baselineSha, currentSha, cancellationToken);
        if (!compare.IsOk)
        {
            // If repo is accessible but compare 404s, it's often because one of the SHAs isn't in this repo.
            if (compare.StatusCode == 404)
            {
                var baseExists = await CommitExistsAsync(repo, baselineSha, cancellationToken);
                var headExists = await CommitExistsAsync(repo, currentSha, cancellationToken);

                if (!baseExists)
                    return GitHubWorkItemFetchResult.Failure($"GitHub compare 404: baseline SHA not found in repo ({baselineSha}).");
                if (!headExists)
                    return GitHubWorkItemFetchResult.Failure($"GitHub compare 404: current SHA not found in repo ({currentSha}).");

                return GitHubWorkItemFetchResult.Failure(compare.Error ?? "GitHub compare 404 (no common ancestor or compare not possible)");
            }

            return GitHubWorkItemFetchResult.Failure(compare.Error ?? "GitHub compare failed");
        }

        var warning = BuildCompareWarning(compare);

        var pullRequestsByNumber = new Dictionary<int, PullRequestInfo>();

        foreach (var sha in compare.CommitShas)
        {
            var prs = await PullRequestsForCommitAsync(repo, sha, cancellationToken);
            if (!prs.IsOk)
                return GitHubWorkItemFetchResult.Failure(prs.Error ?? "GitHub commit->PR lookup failed");

            foreach (var pr in prs.PullRequests)
                pullRequestsByNumber[pr.Number] = pr;
        }

        var workItems = new List<WorkItemLink>();
        var unlinked = new List<UnlinkedPullRequest>();

        foreach (var pr in pullRequestsByNumber.Values.OrderByDescending(p => p.Number))
        {
            var prText = $"{pr.Title}\n{pr.Body}";
            var ids = ExtractWorkItemIds(prText);

            if (ids.Count == 0)
            {
                unlinked.Add(new UnlinkedPullRequest
                {
                    PullRequestNumber = pr.Number,
                    PullRequestTitle = pr.Title,
                    PullRequestUrl = pr.Url
                });

                continue;
            }

            foreach (var id in ids)
            {
                workItems.Add(new WorkItemLink
                {
                    WorkItemId = id,
                    PullRequestNumber = pr.Number,
                    PullRequestTitle = pr.Title,
                    PullRequestUrl = pr.Url
                });
            }
        }

        // Deduplicate pairs.
        workItems = workItems
            .GroupBy(w => (w.WorkItemId, w.PullRequestNumber))
            .Select(g => g.First())
            .OrderBy(w => w.WorkItemId)
            .ThenByDescending(w => w.PullRequestNumber)
            .ToList();

        return GitHubWorkItemFetchResult.Success(workItems, unlinked, warning);
    }

    private static string? BuildCompareWarning(CompareResult compare)
    {
        // This catches the GitOps case where baseline/current are from different release branches.
        // GitHub compare will still return commits (from merge-base), but we should warn if the head is behind.
        if (compare.Status is null && compare.AheadBy is null && compare.BehindBy is null)
            return null;

        var status = compare.Status?.Trim();
        var ahead = compare.AheadBy ?? 0;
        var behind = compare.BehindBy ?? 0;

        if (behind <= 0 && !string.Equals(status, "diverged", StringComparison.OrdinalIgnoreCase))
            return null;

        var statusPart = string.IsNullOrWhiteSpace(status) ? "" : $"status={status}";
        var aheadPart = compare.AheadBy is null ? "" : $"ahead={ahead}";
        var behindPart = compare.BehindBy is null ? "" : $"behind={behind}";

        var parts = new[] { statusPart, aheadPart, behindPart }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var detail = string.Join(", ", parts);
        return string.IsNullOrWhiteSpace(detail)
            ? "Warning: baseline/current appear to be on different branches; consider resetting the baseline."
            : $"Warning: baseline/current appear to be on different branches ({detail}); consider resetting the baseline.";
    }

    private static List<int> ExtractWorkItemIds(string text)
    {
        var results = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return results;

        foreach (Match m in AbWorkItemRegex.Matches(text))
        {
            if (!m.Success) continue;
            if (m.Groups.Count < 2) continue;

            if (int.TryParse(m.Groups[1].Value, out var id))
                results.Add(id);
        }

        return results.Distinct().ToList();
    }

    private async Task<CompareResult> CompareAsync(string repo, string baselineSha, string currentSha, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/compare/{baselineSha}...{currentSha}";

        using var resp = await http.GetAsync(url, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return CompareResult.Failure(
                statusCode: (int)resp.StatusCode,
                error: $"GitHub compare error: {(int)resp.StatusCode} {resp.ReasonPhrase}{TryExtractApiMessageSuffix(json)}");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var commits = new List<string>();
            if (root.TryGetProperty("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in commitsEl.EnumerateArray())
                {
                    if (c.TryGetProperty("sha", out var shaEl) && shaEl.ValueKind == JsonValueKind.String)
                    {
                        var sha = shaEl.GetString();
                        if (!string.IsNullOrWhiteSpace(sha)) commits.Add(sha);
                    }
                }
            }

            // Include the head commit itself as well (GitHub compare returns it separately).
            if (root.TryGetProperty("head_commit", out var headEl) && headEl.ValueKind == JsonValueKind.Object)
            {
                if (headEl.TryGetProperty("sha", out var headShaEl) && headShaEl.ValueKind == JsonValueKind.String)
                {
                    var headSha = headShaEl.GetString();
                    if (!string.IsNullOrWhiteSpace(headSha)) commits.Add(headSha);
                }
            }

            commits = commits.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string? status = null;
            int? aheadBy = null;
            int? behindBy = null;

            if (root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String)
                status = statusEl.GetString();

            if (root.TryGetProperty("ahead_by", out var aheadEl) && aheadEl.TryGetInt32(out var a))
                aheadBy = a;

            if (root.TryGetProperty("behind_by", out var behindEl) && behindEl.TryGetInt32(out var b))
                behindBy = b;

            return CompareResult.Success(commits, status, aheadBy, behindBy);
        }
        catch (JsonException)
        {
            return CompareResult.Failure(statusCode: null, error: "GitHub compare returned invalid JSON");
        }
    }

    private async Task<RepoAccessResult> CheckRepoAccessAsync(string repo, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}";
        using var resp = await http.GetAsync(url, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (resp.IsSuccessStatusCode)
            return RepoAccessResult.Success();

        var code = (int)resp.StatusCode;

        // GitHub often returns 404 for private repos when token lacks access.
        var msg = code == 404
            ? "GitHub repo not found OR token lacks access"
            : $"GitHub repo access error: {code} {resp.ReasonPhrase}";

        return RepoAccessResult.Failure($"{msg}{TryExtractApiMessageSuffix(json)}");
    }

    private async Task<bool> CommitExistsAsync(string repo, string sha, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{repo}/commits/{sha}";
        using var resp = await http.GetAsync(url, cancellationToken);
        return resp.IsSuccessStatusCode;
    }

    private static string TryExtractApiMessageSuffix(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                var msg = m.GetString();
                if (!string.IsNullOrWhiteSpace(msg))
                    return $" (message: {msg})";
            }
        }
        catch
        {
            // ignore
        }
        return "";
    }

    private async Task<PullRequestLookupResult> PullRequestsForCommitAsync(string repo, string commitSha, CancellationToken cancellationToken)
    {
        // This endpoint requires an API preview header historically; "application/vnd.github+json" should be fine on modern API.
        var url = $"https://api.github.com/repos/{repo}/commits/{commitSha}/pulls";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await http.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            return PullRequestLookupResult.Failure($"GitHub commit PRs error: {(int)resp.StatusCode} {resp.ReasonPhrase}");

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return PullRequestLookupResult.Success(Array.Empty<PullRequestInfo>());

            var prs = new List<PullRequestInfo>();
            foreach (var prEl in doc.RootElement.EnumerateArray())
            {
                var number = prEl.TryGetProperty("number", out var nEl) && nEl.TryGetInt32(out var n) ? n : 0;
                var title = prEl.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() : null;
                var body = prEl.TryGetProperty("body", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() : null;
                var prUrl = prEl.TryGetProperty("html_url", out var uEl) && uEl.ValueKind == JsonValueKind.String ? uEl.GetString() : null;

                if (number <= 0 || string.IsNullOrWhiteSpace(prUrl))
                    continue;

                prs.Add(new PullRequestInfo(
                    Number: number,
                    Title: title ?? $"PR #{number}",
                    Body: body ?? string.Empty,
                    Url: prUrl));
            }

            return PullRequestLookupResult.Success(prs);
        }
        catch (JsonException)
        {
            return PullRequestLookupResult.Failure("GitHub commit PRs returned invalid JSON");
        }
    }

    private sealed class GitHubRequestScope : IDisposable
    {
        private readonly HttpClient http;
        private readonly AuthenticationHeaderValue? previousAuth;
        private readonly List<string> previousAccept;
        private readonly string? previousApiVersion;

        public GitHubRequestScope(HttpClient http, string token)
        {
            this.http = http;

            previousAuth = http.DefaultRequestHeaders.Authorization;
            http.DefaultRequestHeaders.Authorization = BuildAuthHeader(token);

            // Required by GitHub API.
            if (!http.DefaultRequestHeaders.UserAgent.Any())
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AuditIntelligenceDeployedVersionTool/1.0");

            previousAccept = http.DefaultRequestHeaders.Accept.Select(a => a.MediaType ?? "").ToList();
            previousApiVersion = http.DefaultRequestHeaders.Contains("X-GitHub-Api-Version")
                ? http.DefaultRequestHeaders.GetValues("X-GitHub-Api-Version").FirstOrDefault()
                : null;

            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        public void Dispose()
        {
            http.DefaultRequestHeaders.Authorization = previousAuth;

            http.DefaultRequestHeaders.Accept.Clear();
            foreach (var m in previousAccept.Where(m => !string.IsNullOrWhiteSpace(m)))
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(m));

            http.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
            if (!string.IsNullOrWhiteSpace(previousApiVersion))
                http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", previousApiVersion);
        }

        private static AuthenticationHeaderValue BuildAuthHeader(string token)
        {
            token = token.Trim();

            // Fine-grained tokens typically start with github_pat_.
            if (token.StartsWith("github_pat_", StringComparison.OrdinalIgnoreCase))
                return new AuthenticationHeaderValue("Bearer", token);

            // Classic PATs commonly start with ghp_.
            return new AuthenticationHeaderValue("token", token);
        }
    }

    private readonly record struct RepoAccessResult(bool IsOk, string? Error)
    {
        public static RepoAccessResult Success() => new(true, null);
        public static RepoAccessResult Failure(string error) => new(false, error);
    }

    private readonly record struct CompareResult(bool IsOk, List<string> CommitShas, string? Status, int? AheadBy, int? BehindBy, int? StatusCode, string? Error)
    {
        public static CompareResult Success(List<string> commits, string? status, int? aheadBy, int? behindBy)
            => new(true, commits, status, aheadBy, behindBy, null, null);

        public static CompareResult Failure(int? statusCode, string error)
            => new(false, new List<string>(), null, null, null, statusCode, error);
    }

    private readonly record struct PullRequestLookupResult(bool IsOk, IReadOnlyList<PullRequestInfo> PullRequests, string? Error)
    {
        public static PullRequestLookupResult Success(IReadOnlyList<PullRequestInfo> prs) => new(true, prs, null);
        public static PullRequestLookupResult Failure(string error) => new(false, Array.Empty<PullRequestInfo>(), error);
    }

    private readonly record struct PullRequestInfo(int Number, string Title, string Body, string Url);
}

public readonly record struct GitHubWorkItemFetchResult(bool IsOk, IReadOnlyList<WorkItemLink> WorkItems, IReadOnlyList<UnlinkedPullRequest> UnlinkedPullRequests, string? Warning, string? Error)
{
    public static GitHubWorkItemFetchResult Success(IReadOnlyList<WorkItemLink> workItems, IReadOnlyList<UnlinkedPullRequest> unlinked, string? warning)
        => new(true, workItems, unlinked, warning, null);

    public static GitHubWorkItemFetchResult Failure(string error)
        => new(false, Array.Empty<WorkItemLink>(), Array.Empty<UnlinkedPullRequest>(), null, error);
}
