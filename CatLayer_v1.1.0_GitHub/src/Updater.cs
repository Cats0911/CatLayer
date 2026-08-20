using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CatLayerUpdater
{
    internal static class Program
    {
        private static string UpdateRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CatLayer", "Update"); }
        }

        private static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(UpdateRoot);
                File.AppendAllText(Path.Combine(UpdateRoot, "update.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + text + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                if (args == null || args.Length < 4) throw new ArgumentException("업데이트 실행 인수가 올바르지 않습니다.");
                int pid;
                if (!int.TryParse(args[0], out pid)) throw new ArgumentException("프로세스 ID가 올바르지 않습니다.");
                string installDir = Path.GetFullPath(args[1]);
                string zipPath = Path.GetFullPath(args[2]);
                string restartExe = Path.GetFullPath(args[3]);
                if (!Directory.Exists(installDir)) throw new DirectoryNotFoundException("CatLayer 설치 폴더를 찾지 못했습니다.");
                if (!File.Exists(zipPath)) throw new FileNotFoundException("업데이트 ZIP을 찾지 못했습니다.", zipPath);

                Log("Update started. PID=" + pid + " ZIP=" + zipPath);
                WaitForCatLayerExit(pid);

                string session = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string staging = Path.Combine(UpdateRoot, "staging_" + session);
                string backup = Path.Combine(UpdateRoot, "backup_" + session);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);

                SafeExtract(zipPath, staging);
                string payload = FindPayloadRoot(staging);
                string payloadExe = Path.Combine(payload, "CatLayer.exe");
                string payloadVersion = Path.Combine(payload, "VERSION.txt");
                if (!File.Exists(payloadExe) || !File.Exists(payloadVersion))
                    throw new InvalidDataException("업데이트 ZIP에 CatLayer.exe 또는 VERSION.txt가 없습니다. 자동 업데이트용 ZIP인지 확인해 주세요.");

                CopyDirectory(installDir, backup, null);
                try
                {
                    CopyDirectory(payload, installDir, null);
                }
                catch
                {
                    Log("Copy failed. Restoring backup.");
                    try { CopyDirectory(backup, installDir, null); } catch (Exception restoreEx) { Log("Restore failed: " + restoreEx); }
                    throw;
                }

                Log("Update applied successfully.");
                try { Directory.Delete(staging, true); } catch { }
                try { File.Delete(zipPath); } catch { }
                try { File.Delete(Path.Combine(UpdateRoot, "SHA256.txt")); } catch { }

                if (File.Exists(restartExe))
                {
                    ProcessStartInfo psi = new ProcessStartInfo(restartExe);
                    psi.WorkingDirectory = Path.GetDirectoryName(restartExe);
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex);
                try
                {
                    MessageBox.Show("CatLayer 업데이트에 실패했습니다.\n\n" + ex.Message + "\n\n로그: %LOCALAPPDATA%\\CatLayer\\Update\\update.log",
                        "CatLayer Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
                Environment.ExitCode = 1;
            }
        }

        private static void WaitForCatLayerExit(int pid)
        {
            try
            {
                Process p = Process.GetProcessById(pid);
                if (!p.WaitForExit(30000)) throw new TimeoutException("CatLayer가 30초 안에 종료되지 않았습니다.");
            }
            catch (ArgumentException) { }
        }

        private static void SafeExtract(string zipPath, string destination)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    string target = Path.GetFullPath(Path.Combine(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("업데이트 ZIP에 잘못된 경로가 포함되어 있습니다.");
                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    string parent = Path.GetDirectoryName(target);
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }
        }

        private static string FindPayloadRoot(string staging)
        {
            if (File.Exists(Path.Combine(staging, "CatLayer.exe"))) return staging;
            string[] dirs = Directory.GetDirectories(staging);
            if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "CatLayer.exe"))) return dirs[0];
            foreach (string dir in dirs)
                if (File.Exists(Path.Combine(dir, "CatLayer.exe")) && File.Exists(Path.Combine(dir, "VERSION.txt"))) return dir;
            return staging;
        }

        private static void CopyDirectory(string source, string destination, string skipFileName)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                if (!string.IsNullOrEmpty(skipFileName) && string.Equals(Path.GetFileName(file), skipFileName, StringComparison.OrdinalIgnoreCase)) continue;
                string target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, true);
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                string name = Path.GetFileName(dir);
                CopyDirectory(dir, Path.Combine(destination, name), skipFileName);
            }
        }
    }
}
