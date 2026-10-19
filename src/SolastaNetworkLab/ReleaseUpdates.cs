using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Solasta.NetworkLab
{
    public sealed class UpdateRelease
    {
        public Version Version;
        public string PageUrl, DownloadUrl, Sha256;
        public long Size;
    }

    public static class ReleaseUpdates
    {
        public const string RepositoryUrl = "https://github.com/Mira-Dev-Agent/SolastaNetworkLab";
        public const string LatestApiUrl = "https://api.github.com/repos/Mira-Dev-Agent/SolastaNetworkLab/releases/latest";
        public const int MaxPackageBytes = 32 * 1024 * 1024;
        public static readonly string[] PackageFiles = {
            "SolastaNetworkLab.dll", "DirectTransport.dll", "Info.json", "ApplyUpdate.ps1", "README.md", "analysis/FINDINGS.md"
        };
        public static Version ParseVersion(string text)
        {
            if (string.IsNullOrEmpty(text)) throw new InvalidDataException("Missing version");
            if (text.StartsWith("v", StringComparison.Ordinal)) text = text.Substring(1);
            var parts = text.Split('.');
            if (parts.Length != 3 || parts.Any(p => p.Length == 0 || p.Any(c => c < '0' || c > '9')))
                throw new InvalidDataException("Expected a stable three-part version");
            Version result;
            if (!Version.TryParse(text, out result)) throw new InvalidDataException("Invalid version");
            return result;
        }
        public static UpdateRelease ParseRelease(byte[] json)
        {
            if (json.Length > 256 * 1024) throw new InvalidDataException("Release metadata too large");
            ReleaseDto dto;
            using (var stream = new MemoryStream(json)) dto = (ReleaseDto)new DataContractJsonSerializer(typeof(ReleaseDto)).ReadObject(stream);
            if (dto == null || dto.Draft || dto.Prerelease) throw new InvalidDataException("Release is not published/stable");
            var version = ParseVersion(dto.Tag);
            var page = RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(dto.Tag);
            if (!string.Equals(dto.Page, page, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected release page");
            var filename = "SolastaNetworkLab-" + version.ToString(3) + ".zip";
            var assets = (dto.Assets ?? new AssetDto[0]).Where(a => a.Name == filename).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("Release has no unique mod package");
            var asset = assets[0];
            var expectedUrl = RepositoryUrl + "/releases/download/" + Uri.EscapeDataString(dto.Tag) + "/" + filename;
            if (asset.Url != expectedUrl || asset.Size <= 0 || asset.Size > MaxPackageBytes)
                throw new InvalidDataException("Invalid package URL or size");
            if (asset.Digest == null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal)
                || asset.Digest.Length != 71 || asset.Digest.Substring(7).Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Release package has no SHA-256 digest");
            return new UpdateRelease { Version = version, PageUrl = page, DownloadUrl = expectedUrl, Size = asset.Size, Sha256 = asset.Digest.Substring(7).ToLowerInvariant() };
        }
        public static bool IsNewer(Version installed, UpdateRelease release)
            => release.Version.CompareTo(ParseVersion(installed.ToString(3))) > 0;

        public static async Task<UpdateRelease> CheckAsync(CancellationToken token)
        {
            using (var http = CreateClient(false, TimeSpan.FromSeconds(15)))
            using (var response = await http.GetAsync(LatestApiUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    return ParseRelease(await ReadLimited(stream, 256 * 1024, token).ConfigureAwait(false));
            }
        }
        public static async Task<string> StageAsync(UpdateRelease release, string modDirectory, CancellationToken token)
        {
            // Only a release validated against the fixed repository may select a download URL.
            var expected = RepositoryUrl + "/releases/download/";
            if (release == null || !release.DownloadUrl.StartsWith(expected, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected download origin");
            byte[] bytes;
            using (var http = CreateClient(true, TimeSpan.FromMinutes(3)))
            using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                // GitHub redirects release assets to its asset CDN; never follow a downgrade to HTTP.
                if (response.RequestMessage.RequestUri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("Insecure asset redirect");
                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    bytes = await ReadLimited(stream, MaxPackageBytes, token).ConfigureAwait(false);
            }
            if (bytes.LongLength != release.Size || Sha256(bytes) != release.Sha256) throw new InvalidDataException("Package checksum or size mismatch");
            ValidatePackage(bytes, release.Version);
            var directory = Path.Combine(Path.GetFullPath(modDirectory), ".updates", release.Version.ToString(3));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "SolastaNetworkLab.zip");
            // Incomplete data is never treated as an installable package.
            File.WriteAllBytes(path + ".partial", bytes);
            if (File.Exists(path)) File.Delete(path);
            File.Move(path + ".partial", path);
            return path;
        }
        public static string Sha256(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
        public static void ValidatePackage(byte[] bytes, Version expectedVersion)
        {
            using (var stream = new MemoryStream(bytes))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var files = zip.Entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal)).ToArray();
                if (files.Length != PackageFiles.Length || files.Sum(e => e.Length) > MaxPackageBytes)
                    throw new InvalidDataException("Unexpected package contents or expanded size");
                foreach (var file in PackageFiles)
                    if (files.Count(e => e.FullName == "SolastaNetworkLab/" + file) != 1)
                        throw new InvalidDataException("Missing or duplicate package file: " + file);
                using (var infoStream = zip.GetEntry("SolastaNetworkLab/Info.json").Open())
                {
                    var info = (ModInfoDto)new DataContractJsonSerializer(typeof(ModInfoDto)).ReadObject(infoStream);
                    if (info.Id != "SolastaNetworkLab" || ParseVersion(info.Version) != expectedVersion)
                        throw new InvalidDataException("Mod identity/version mismatch");
                }
            }
        }
        private static HttpClient CreateClient(bool redirects, TimeSpan timeout)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = redirects, MaxAutomaticRedirections = 5,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { Timeout = timeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SolastaNetworkLab-UpdateChecker/0.5.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }
        private static async Task<byte[]> ReadLimited(Stream stream, int limit, CancellationToken token)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192]; int read;
                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > limit) throw new InvalidDataException("Download exceeds limit");
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
        }
        [DataContract] private sealed class ReleaseDto
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "html_url")] public string Page { get; set; }
            [DataMember(Name = "draft")] public bool Draft { get; set; }
            [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
            [DataMember(Name = "assets")] public AssetDto[] Assets { get; set; }
        }
        [DataContract] private sealed class AssetDto
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "browser_download_url")] public string Url { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
        }
        [DataContract] private sealed class ModInfoDto
        {
            [DataMember] public string Id { get; set; }
            [DataMember] public string Version { get; set; }
        }
    }
}
