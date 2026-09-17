using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace novideo_srgb
{
    public enum UpdateCheckStatus
    {
        UpToDate,
        UpdateAvailable,
        Failed
    }

    public class UpdateCheckResult
    {
        public UpdateCheckStatus Status { get; }

        // The release version exactly as the tag spelled it (no artificially appended zeros),
        // for display. Comparison uses a separately normalised copy - see UpdateChecker.
        public Version Version { get; }
        public string ReleaseUrl { get; }

        private UpdateCheckResult(UpdateCheckStatus status, Version version, string releaseUrl)
        {
            Status = status;
            Version = version;
            ReleaseUrl = releaseUrl;
        }

        public static UpdateCheckResult UpToDate() =>
            new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, null);

        public static UpdateCheckResult Failed() =>
            new UpdateCheckResult(UpdateCheckStatus.Failed, null, null);

        public static UpdateCheckResult Available(Version version, string releaseUrl) =>
            new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, version, releaseUrl);
    }

    [DataContract]
    internal class GitHubRelease
    {
        [DataMember(Name = "tag_name")] public string TagName { get; set; }
        [DataMember(Name = "html_url")] public string HtmlUrl { get; set; }
    }

    public static class UpdateChecker
    {
        private const string LatestReleaseApi =
            "https://api.github.com/repos/acckiydarik/novideo_srgb/releases/latest";

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            // Some Windows/.NET Framework 4.8 configurations still default to TLS 1.0/1.1,
            // which GitHub refuses; opt in explicitly rather than depending on machine config.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // GitHub rejects API requests without a User-Agent with 403.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("novideo_srgb-fork/" + CurrentVersion());
            return client;
        }

        private static string CurrentVersion()
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v.Major + "." + v.Minor;
        }

        public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            string json;
            using (var response = await Client.GetAsync(LatestReleaseApi, cancellationToken).ConfigureAwait(false))
            {
                // 403 (rate limit) and 404 return a body that is valid JSON but not a release -
                // bail out here so the caller reports "could not check" instead of a confusing
                // parse error.
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            return Evaluate(json, Assembly.GetExecutingAssembly().GetName().Version);
        }

        // Pure function: JSON + local version in, verdict out. Kept free of HTTP so the parsing
        // and comparison rules can be exercised without touching the network.
        public static UpdateCheckResult Evaluate(string json, Version localVersion)
        {
            if (localVersion == null) return UpdateCheckResult.Failed();

            GitHubRelease release;
            try
            {
                var serializer = new DataContractJsonSerializer(typeof(GitHubRelease));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json ?? string.Empty)))
                {
                    release = (GitHubRelease)serializer.ReadObject(stream);
                }
            }
            catch (Exception)
            {
                return UpdateCheckResult.Failed();
            }

            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return UpdateCheckResult.Failed();
            }

            var tag = release.TagName.Trim();
            if (tag.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag.Substring(1);
            }

            Version remote;
            // Pre-release/non-numeric tags (v4.6-beta) are not parseable as System.Version -
            // treat them as "cannot determine" rather than letting FormatException escape.
            if (!Version.TryParse(tag, out remote))
            {
                return UpdateCheckResult.Failed();
            }

            if (Normalize(remote) <= Normalize(localVersion))
            {
                return UpdateCheckResult.UpToDate();
            }

            // A newer version with no usable link would leave the user with a dead hyperlink,
            // so validate it here rather than at the point of use: the UI code builds a Uri
            // outside its try/catch, and Process.Start would happily launch a non-http scheme.
            if (!IsUsableReleaseUrl(release.HtmlUrl))
            {
                return UpdateCheckResult.Failed();
            }

            return UpdateCheckResult.Available(remote, release.HtmlUrl);
        }

        private static bool IsUsableReleaseUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;

            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        // Version leaves unspecified components at -1, so 4.5, 4.5.0 and 4.5.0.0 are not equal
        // out of the box. Compare on a copy padded with zeros; the original is kept for display
        // so the text shows the version the way the tag actually spelled it.
        private static Version Normalize(Version version)
        {
            return new Version(
                version.Major,
                Math.Max(version.Minor, 0),
                Math.Max(version.Build, 0),
                Math.Max(version.Revision, 0));
        }
    }
}
