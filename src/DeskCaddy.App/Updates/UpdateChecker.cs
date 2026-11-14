using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DeskCaddy.Diagnostics;

namespace DeskCaddy.Updates;

internal sealed record UpdateCheckResult(
    bool HasUpdate,
    string? LatestVersion,
    string? ReleaseUrl,
    string Message);

/// <summary>
/// 通过 GitHub Releases API 检查新版本。只在用户点「检查更新」时发一次请求，
/// 不轮询、不后台常驻，避免额外内存与网络开销。
/// </summary>
internal static class UpdateChecker
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/verlpro228/DeskCaddy/releases/latest";

    private static readonly string VersionText = ReadCurrentVersion();

    private static readonly HttpClient Client = CreateClient();

    public static string CurrentVersion => VersionText;

    public static async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            using var response = await Client.GetAsync(LatestReleaseApi).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                AppLog.Error($"update check http {(int)response.StatusCode}");
                return Failed($"GitHub 返回 {(int)response.StatusCode}，请稍后重试");
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlElement) ? urlElement.GetString() : null;

            if (!TryParseVersion(tag, out var latest) || !TryParseVersion(CurrentVersion, out var current))
            {
                AppLog.Error($"update check unparsable tag={tag}");
                return Failed("无法识别最新版本号");
            }

            var hasUpdate = latest > current;

            AppLog.Info($"update check current={CurrentVersion} latest={latest.ToString(3)} update={hasUpdate}");

            return new UpdateCheckResult(
                hasUpdate,
                latest.ToString(3),
                url,
                hasUpdate
                    ? $"发现新版本 v{latest.ToString(3)}（当前 v{CurrentVersion}）"
                    : $"已是最新版本（v{CurrentVersion}）");
        }
        catch (Exception ex)
        {
            // 网络边界：DNS/TLS/超时/JSON 异常都在这里兜底，界面只显示一句人话。
            AppLog.Error("update check failed", ex);
            return Failed("网络不可用或访问 GitHub 超时");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // GitHub API 要求带 User-Agent；Accept 固定 v3 JSON。
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DeskCaddy/{VersionText}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        return client;
    }

    private static string ReadCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly().GetName().Version;

        return assembly is null ? "0.0.0" : assembly.ToString(3);
    }

    /// <summary>把 "v1.1.1" / "1.1.1.0" 统一成三段版本号再比较。</summary>
    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().TrimStart('v', 'V');

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));

        return true;
    }

    private static UpdateCheckResult Failed(string message) => new(false, null, null, message);
}
