using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Solasta.NetworkLab
{
    internal sealed class UpdatePanel : IDisposable
    {
        private readonly Version installed = typeof(UpdatePanel).Assembly.GetName().Version;
        private readonly string modDirectory;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private Task<UpdateRelease> check;
        private Task<string> download;
        private UpdateRelease available;
        private bool prompted, queued;
        private string status = "Checking GitHub for updates...";
        private DateTime lastCheck;
        public UpdatePanel(string directory) { modDirectory = directory; Check(); }
        private void Check()
        {
            if (queued || check != null && !check.IsCompleted || download != null) return;
            lastCheck = DateTime.UtcNow;
            status = "Checking GitHub for updates...";
            check = Task.Run(() => ReleaseUpdates.CheckAsync(cancellation.Token));
        }
        public void Draw()
        {
            GUILayout.Label("Installed: " + installed.ToString(3) + " | Available: " + (available?.Version.ToString(3) ?? "unknown"));
            GUILayout.Label(status);
            if (!queued && available != null && ReleaseUpdates.IsNewer(installed, available) && download == null
                && GUILayout.Button("Download update and install after Solasta exits")) Download();
            if (GUILayout.Button("Open GitHub releases")) Application.OpenURL(ReleaseUpdates.RepositoryUrl + "/releases");
            if (!queued && download == null && DateTime.UtcNow - lastCheck >= TimeSpan.FromMinutes(1)
                && GUILayout.Button("Check for updates now")) { prompted = false; Check(); }
        }
        private void Download()
        {
            if (download != null || queued || available == null) return;
            status = "Downloading and verifying " + available.Version.ToString(3) + "...";
            download = Task.Run(() => ReleaseUpdates.StageAsync(available, modDirectory, cancellation.Token));
        }
        // All game UI and helper startup occur on Unity's main thread.
        public void Tick(bool mayPrompt)
        {
            if (check != null && check.IsCompleted)
            {
                var finished = check; check = null;
                if (finished.IsCanceled || finished.IsFaulted) status = "Update check unavailable. You can check again or open GitHub releases.";
                else
                {
                    available = finished.Result;
                    status = available == null ? "No published release is available yet."
                        : ReleaseUpdates.IsNewer(installed, available) ? "A newer release is available."
                        : "Installed version is up to date or newer than the published release.";
                }
            }
            if (download != null && download.IsCompleted)
            {
                var finished = download; download = null;
                if (finished.IsCanceled || finished.IsFaulted) status = "Update download/verification failed; installed files are unchanged. Retry from this panel.";
                else
                {
                    try { StartInstaller(finished.Result); queued = true; status = "Verified update queued. Exit Solasta normally to install it, then relaunch."; }
                    catch (Exception e) { status = "Could not queue installation: " + e.Message + ". Installed files are unchanged."; }
                }
            }
            if (!mayPrompt || prompted || queued || available == null || !ReleaseUpdates.IsNewer(installed, available)) return;
            prompted = true;
            Gui.GuiService.ShowMessage(MessageModal.Severity.Attention2, "Solasta Network Lab update",
                "Installed: " + installed.ToString(3) + "\nAvailable: " + available.Version.ToString(3)
                + "\nDownload the update from GitHub and install it automatically after Solasta exits? Restart the game afterward."
                + "\nAll multiplayer participants should use the same mod version.",
                "Download update", "Later", Download, () => { });
        }
        private void StartInstaller(string package)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("Automatic installation currently supports Windows only");
            var helper = Path.Combine(modDirectory, ".updates", "apply-" + Guid.NewGuid().ToString("N") + ".ps1");
            File.Copy(Path.Combine(modDirectory, "ApplyUpdate.ps1"), helper);
            using (var game = Process.GetCurrentProcess())
            {
                var args = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + Quote(helper)
                    + " -GameProcessId " + game.Id + " -GameStartTicks " + game.StartTime.ToUniversalTime().Ticks
                    + " -TargetDirectory " + Quote(Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar))
                    + " -PackagePath " + Quote(package) + " -ExpectedSha256 " + Quote(available.Sha256)
                    + " -ExpectedVersion " + Quote(available.Version.ToString(3));
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
                using (var helperProcess = Process.Start(new ProcessStartInfo(powershell, args) {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = modDirectory
                })) { if (helperProcess == null) throw new IOException("Installer did not start"); }
            }
        }
        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
                throw new ArgumentException("Invalid installer path/argument");
            return "\"" + value + "\"";
        }
        public void Dispose() { cancellation.Cancel(); }
    }
}
