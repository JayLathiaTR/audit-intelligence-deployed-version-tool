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

        var compare = await CompareAsync(repo, baselineSha, currentSha, cancellationToken);
        if (!compare.IsOk)
            return GitHubWorkItemFetchResult.Failure(compare.Error ?? "GitHub compare failed");

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

        return GitHubWorkItemFetchResult.Success(workItems, unlinked);
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
            return CompareResult.Failure($"GitHub compare error: {(int)resp.StatusCode} {resp.ReasonPhrase}");

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

            return CompareResult.Success(commits);
        }
        catch (JsonException)
        {
            return CompareResult.Failure("GitHub compare returned invalid JSON");
        }
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

        public GitHubRequestScope(HttpClient http, string token)
        {
            this.http = http;

            previousAuth = http.DefaultRequestHeaders.Authorization;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Required by GitHub API.
            if (!http.DefaultRequestHeaders.UserAgent.Any())
                http.DefaultRequestHeaders.UserAgent.ParseAdd("AuditIntelligenceDeployedVersionTool/1.0");

            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            http.DefaultRequestHeaders.Remove("X-GitHub-Api-Version");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        public void Dispose()
        {
            http.DefaultRequestHeaders.Authorization = previousAuth;
        }
    }

    private readonly record struct CompareResult(bool IsOk, List<string> CommitShas, string? Error)
    {
        public static CompareResult Success(List<string> commits) => new(true, commits, null);
        public static CompareResult Failure(string error) => new(false, new List<string>(), error);
    }

    private readonly record struct PullRequestLookupResult(bool IsOk, IReadOnlyList<PullRequestInfo> PullRequests, string? Error)
    {
        public static PullRequestLookupResult Success(IReadOnlyList<PullRequestInfo> prs) => new(true, prs, null);
        public static PullRequestLookupResult Failure(string error) => new(false, Array.Empty<PullRequestInfo>(), error);
    }

    private readonly record struct PullRequestInfo(int Number, string Title, string Body, string Url);
}

public readonly record struct GitHubWorkItemFetchResult(bool IsOk, IReadOnlyList<WorkItemLink> WorkItems, IReadOnlyList<UnlinkedPullRequest> UnlinkedPullRequests, string? Error)
{
    public static GitHubWorkItemFetchResult Success(IReadOnlyList<WorkItemLink> workItems, IReadOnlyList<UnlinkedPullRequest> unlinked)
        => new(true, workItems, unlinked, null);

    public static GitHubWorkItemFetchResult Failure(string error)
        => new(false, Array.Empty<WorkItemLink>(), Array.Empty<UnlinkedPullRequest>(), error);
}
