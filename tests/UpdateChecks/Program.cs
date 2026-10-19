using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using Solasta.NetworkLab;

class Program
{
    static int checks;
    static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, message); }
    static void Main(string[] args)
    {
        if (args.Contains("--fake-game")) { Thread.Sleep(4000); return; }
        if (args.Contains("--live"))
        {
            var live = ReleaseUpdates.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
            Check(live != null, "Published release unavailable");
            var stage = Path.Combine(Environment.CurrentDirectory, ".runtime", "live-update-check");
            var zip = ReleaseUpdates.StageAsync(live, stage, CancellationToken.None).GetAwaiter().GetResult();
            Check(File.Exists(zip), "Live release did not stage");
            Console.WriteLine("PASS: live GitHub release " + live.Version + ", SHA-256 verified and staged inside project."); return;
        }
        Check(ReleaseUpdates.ParseVersion("v0.10.0") > ReleaseUpdates.ParseVersion("0.9.9"), "Numeric version ordering");
        var release = ReleaseUpdates.ParseRelease(Metadata());
        Check(ReleaseUpdates.IsNewer(new Version(0, 2, 1, 0), release), "Newer release missed");
        Check(!ReleaseUpdates.IsNewer(new Version(0, 3, 0, 0), release), "Equal release offered");
        Check(!ReleaseUpdates.IsNewer(new Version(1, 0, 0), release), "Downgrade offered");
        Reject(() => ReleaseUpdates.ParseVersion("0.4.0-beta"), "Prerelease version accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(prerelease: true)), "Prerelease release accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(draft: true)), "Draft release accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(page: "https://example.com/releases/tag/v0.3.0")), "External release page accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(asset: "https://example.com/package.zip")), "External asset accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(digest: "sha256:bad")), "Invalid digest accepted");
        Reject(() => ReleaseUpdates.ParseRelease(Metadata(size: ReleaseUpdates.MaxPackageBytes + 1)), "Oversize release accepted");
        var package = Package();
        ReleaseUpdates.ValidatePackage(package, new Version(0, 3, 0)); checks++;
        Reject(() => ReleaseUpdates.ValidatePackage(package, new Version(0, 4, 0)), "Wrong package version accepted");
        Reject(() => ReleaseUpdates.ValidatePackage(Package("OtherMod"), new Version(0, 3, 0)), "Wrong mod accepted");
        Reject(() => ReleaseUpdates.ValidatePackage(Package(extra: "SolastaNetworkLab/../../outside.dll"), new Version(0, 3, 0)), "Traversal entry accepted");
        Reject(() => ReleaseUpdates.ValidatePackage(Package(extra: "SolastaNetworkLab/Info.json"), new Version(0, 3, 0)), "Duplicate entry accepted");
        Reject(() => ReleaseUpdates.ValidatePackage(Package(omit: "DirectTransport.dll"), new Version(0, 3, 0)), "Incomplete package accepted");
        InstallerCheck(false, false);
        InstallerCheck(true, false);
        InstallerCheck(false, true);
        Console.WriteLine("PASS: " + checks + " update checks; versions, release origin, digest, ZIP validation, wait-for-exit install, backup, checksum refusal and rollback.");
    }
    static byte[] Metadata(bool prerelease = false, bool draft = false, string page = null, string asset = null, string digest = null, long size = 100)
        => JsonSerializer.SerializeToUtf8Bytes(new {
            tag_name = "v0.3.0", html_url = page ?? ReleaseUpdates.RepositoryUrl + "/releases/tag/v0.3.0", draft, prerelease,
            assets = new[] { new { name = "SolastaNetworkLab-0.3.0.zip", browser_download_url = asset ?? ReleaseUpdates.RepositoryUrl + "/releases/download/v0.3.0/SolastaNetworkLab-0.3.0.zip", digest = digest ?? "sha256:" + new string('a', 64), size } }
        });
    static byte[] Package(string id = "SolastaNetworkLab", string extra = null, string omit = null)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (var file in ReleaseUpdates.PackageFiles.Where(f => f != omit))
            {
                using var writer = new StreamWriter(zip.CreateEntry("SolastaNetworkLab/" + file).Open());
                writer.Write(file == "Info.json" ? JsonSerializer.Serialize(new { Id = id, Version = "0.3.0" }) : "updated " + file);
            }
            if (extra != null) { using var writer = new StreamWriter(zip.CreateEntry(extra).Open()); writer.Write("invalid"); }
        }
        return stream.ToArray();
    }
    static void InstallerCheck(bool wrongDigest, bool forceRollback)
    {
        var root = Path.Combine(Environment.CurrentDirectory, ".runtime", "update-checks", Guid.NewGuid().ToString("N"));
        var pending = Path.Combine(root, ".updates"); Directory.CreateDirectory(pending);
        var original = Path.Combine(root, "SolastaNetworkLab.dll"); File.WriteAllText(original, "original");
        var second = Path.Combine(root, "DirectTransport.dll"); File.WriteAllText(second, "original transport");
        var bytes = Package(); var package = Path.Combine(pending, "package.zip"); File.WriteAllBytes(package, bytes);
        // Copy the actual helper under test outside the replacement file set.
        var helper = Path.Combine(pending, "helper.ps1"); File.Copy("src/SolastaNetworkLab/ApplyUpdate.ps1", helper);
        using var game = Process.Start(new ProcessStartInfo(Environment.ProcessPath, "--fake-game") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
        using var locked = forceRollback ? new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper,
            "-GameProcessId", game.Id.ToString(), "-GameStartTicks", game.StartTime.ToUniversalTime().Ticks.ToString(),
            "-TargetDirectory", root, "-PackagePath", package, "-ExpectedSha256", wrongDigest ? new string('0', 64) : ReleaseUpdates.Sha256(bytes), "-ExpectedVersion", "0.3.0" }) start.ArgumentList.Add(arg);
        using var installer = Process.Start(start);
        // Wait until the helper has actually reached its wait-for-game-exit stage.
        var log = Path.Combine(pending, "install.log");
        Check(SpinWait.SpinUntil(() => File.Exists(log) || game.HasExited, 10000), "Installer did not start");
        Check(!game.HasExited && File.ReadAllText(original) == "original", "Installer wrote while game was running");
        game.WaitForExit(); Check(installer.WaitForExit(15000), "Installer did not finish");
        if (wrongDigest || forceRollback)
        {
            Check(installer.ExitCode != 0 && File.ReadAllText(original) == "original", "Failed installation did not preserve/restore original");
            Check(!File.Exists(Path.Combine(root, "Info.json")), "Failed installation left new files behind");
        }
        else
        {
            Check(installer.ExitCode == 0 && File.ReadAllText(original) == "updated SolastaNetworkLab.dll", "Queued installation failed: " + File.ReadAllText(log));
            Check(Directory.GetFiles(pending, "SolastaNetworkLab.dll", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "original"), "Backup missing");
            Check(File.Exists(Path.Combine(root, "Info.json")), "Manifest not installed");
        }
    }
}
