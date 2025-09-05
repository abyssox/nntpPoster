using ExternalProcessWrappers;
using log4net;
using nntpPoster;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Util;
using Util.Configuration;

namespace nntpAutoposter
{
    public class AutoPoster
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly HashSet<string> FfmpegHandledExtensions =
            new HashSet<string>(StringComparer.InvariantCultureIgnoreCase)
            {
                "mkv", "avi", "wmv", "mp4", "mov", "ogg", "ogm", "wav",
                "mka", "mks", "mpeg", "mpg", "vob", "mp3", "asf", "ape", "flac"
            };
        private static readonly Regex MultiDotRegex = new Regex(@"\.{2,}", RegexOptions.Compiled);
        private readonly Settings configuration;
        private Task _workerTask;
        private CancellationTokenSource _cts;

        public AutoPoster(Settings configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void Start()
        {
            log.InfoFormat("Starting nntpPoster version {0}", Assembly.GetExecutingAssembly().GetName().Version);
            InitializeEnvironment();

            _cts = new CancellationTokenSource();
            _workerTask = Task.Factory.StartNew(
                () => AutopostingLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop(int millisecondsTimeout = Timeout.Infinite)
        {
            var localCts = _cts;
            var localTask = _workerTask;

            if (localCts != null && !localCts.IsCancellationRequested)
            {
                try { localCts.Cancel(); } catch { /* ignore */ }
            }

            if (localTask == null) return;

            try
            {
                if (!localTask.Wait(millisecondsTimeout))
                {
                    log.Warn("Stop timed out waiting for worker task to finish.");
                }
            }
            catch (AggregateException ae)
            {
                ae.Handle(ex =>
                {
                    if (ex is OperationCanceledException) return true;
                    log.Error("Worker task faulted while stopping.", ex);
                    return true;
                });
            }
        }

        private void InitializeEnvironment()
        {
            var working = configuration.WorkingFolder;
            log.Info("Cleaning out processing folder of any leftover files.");

            try
            {
                if (!working.Exists)
                {
                    working.Create();
                    return;
                }

                foreach (var fsi in working.EnumerateFileSystemInfos())
                {
                    try
                    {
                        var attrs = File.GetAttributes(fsi.FullName);
                        if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                            Directory.Delete(fsi.FullName, true);
                        else
                            File.Delete(fsi.FullName);
                    }
                    catch (Exception ex)
                    {
                        log.WarnFormat("Failed to clean '{0}': {1}", fsi.FullName, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("Failed to enumerate or clean working folder.", ex);
            }
        }

        private void AutopostingLoop(CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(configuration.AutoposterIntervalSeconds > 0
                ? configuration.AutoposterIntervalSeconds
                : 0);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    UploadNextItemInQueue();
                }
                catch (OperationCanceledException) { /* graceful exit */ }
                catch (Exception ex)
                {
                    log.Error("Unexpected error in autoposting loop. Continuing.", ex);
                }

                if (ct.IsCancellationRequested) break;

                if (interval > TimeSpan.Zero)
                {
                    if (ct.WaitHandle.WaitOne(interval)) break;
                }
            }
        }

        private void UploadNextItemInQueue()
        {
            UploadEntry nextUpload = null;
            try
            {
                nextUpload = DBHandler.Instance.GetNextUploadEntryToUpload();
                if (nextUpload == null) return;

                var folderCfg = configuration.GetWatchFolderSettings(nextUpload.WatchFolderShortName);

                var fullPath = nextUpload.GetCurrentPath(configuration, nextUpload.Name);
                FileSystemInfo toUpload;
                bool isDirectory;

                try
                {
                    var attrs = File.GetAttributes(fullPath);
                    if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                    {
                        isDirectory = true;
                        toUpload = new DirectoryInfo(fullPath);
                    }
                    else
                    {
                        isDirectory = false;
                        toUpload = new FileInfo(fullPath);
                    }
                }
                catch (Exception)
                {
                    log.WarnFormat("Can no longer find '{0}', cancelling upload", fullPath);
                    nextUpload.CurrentLocation = Location.None;
                    nextUpload.Cancelled = true;
                    DBHandler.Instance.UpdateUploadEntry(nextUpload);
                    return;
                }

                if (nextUpload.UploadAttempts >= configuration.MaxRepostCount)
                {
                    log.WarnFormat("Cancelling the upload after {0} retry attempts.", nextUpload.UploadAttempts);
                    nextUpload.Cancelled = true;
                    nextUpload.Move(configuration, Location.Failed);
                    DBHandler.Instance.UpdateUploadEntry(nextUpload);
                    return;
                }

                PostRelease(folderCfg, nextUpload, toUpload, isDirectory);
            }
            catch (Exception ex)
            {
                log.Error("The upload failed to post. Retrying.", ex);
            }
        }

        private void PostRelease(WatchFolderSettings folderCfg, UploadEntry entry, FileSystemInfo toUpload, bool isDirectory)
        {
            entry.UploadAttempts++;

            var baseName = StripNonAscii(NameWithoutExtensionFast(toUpload));
            entry.CleanedName = ApplyTags(folderCfg.CleanName ? CleanName(folderCfg, baseName) : baseName, folderCfg);

            if (folderCfg.UseObfuscation)
            {
                entry.ObscuredName = Guid.NewGuid().ToString("N");
                entry.NotifiedIndexerAt = null;
            }

            DBHandler.Instance.UpdateUploadEntry(entry);

            var poster = new UsenetPoster(configuration, folderCfg);
            FileSystemInfo prepared = null;

            try
            {
                prepared = isDirectory
                    ? (FileSystemInfo)PrepareDirectoryForPosting(folderCfg, entry, (DirectoryInfo)toUpload)
                    : PrepareFileForPosting(folderCfg, entry, (FileInfo)toUpload);

                var password = folderCfg.ApplyRandomPassword ? Guid.NewGuid().ToString("N") : folderCfg.RarPassword;

                FileInfo nfoFile = null;
                if (entry.HasNfo)
                {
                    var nfoPath = entry.GetCurrentPath(configuration, NameWithoutExtensionFast(toUpload) + ".nfo");
                    nfoFile = new FileInfo(nfoPath);
                }

                var nzbFile = poster.PostToUsenet(prepared, password, false, nfoFile, configuration.KeepProcessingFolderAfterError);

                entry.NzbContents = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + nzbFile;
                if (configuration.NzbOutputFolder != null)
                {
                    var nzbPath = Path.Combine(configuration.NzbOutputFolder.FullName, entry.CleanedName + ".nzb");
                    File.WriteAllText(nzbPath, entry.NzbContents);
                }

                entry.RarPassword = password;
                entry.UploadedAt = DateTime.UtcNow;
                entry.Move(configuration, Location.Backup);
                DBHandler.Instance.UpdateUploadEntry(entry);

                log.InfoFormat("[{0}] was uploaded as obfuscated release [{1}] to usenet.",
                    entry.CleanedName, entry.ObscuredName);
            }
            finally
            {
                if (prepared != null)
                {
                    try
                    {
                        prepared.Refresh();
                        if (prepared.Exists)
                        {
                            var attrs = File.GetAttributes(prepared.FullName);
                            if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                                Directory.Delete(prepared.FullName, true);
                            else
                                File.Delete(prepared.FullName);
                        }
                    }
                    catch (Exception ex)
                    {
                        log.WarnFormat("Cleanup failed for '{0}': {1}", prepared.FullName, ex.Message);
                    }
                }
            }
        }

        private static string NameWithoutExtensionFast(FileSystemInfo fsi)
        {
            var name = fsi.Name;
            var lastDot = name.LastIndexOf('.');
            return lastDot > 0 ? name.Substring(0, lastDot) : name;
        }

        private string ApplyTags(string cleanedName, WatchFolderSettings cfg)
        {
            if (!string.IsNullOrEmpty(cfg.PreTag) && !cleanedName.StartsWith(cfg.PreTag))
                cleanedName = cfg.PreTag + cleanedName;

            if (!string.IsNullOrEmpty(cfg.PostTag) && !cleanedName.EndsWith(cfg.PostTag))
                cleanedName = cleanedName + cfg.PostTag;

            return cleanedName;
        }

        private DirectoryInfo PrepareDirectoryForPosting(WatchFolderSettings cfg, UploadEntry entry, DirectoryInfo source)
        {
            var baseFolder = Path.Combine(configuration.WorkingFolder.FullName, cfg.ShortName);
            var destName = cfg.UseObfuscation ? entry.ObscuredName : entry.CleanedName;
            var destination = Path.Combine(baseFolder, destName);

            EnsureDirectoryExists(baseFolder);
            source.Copy(destination, true);
            return new DirectoryInfo(destination);
        }

        private FileInfo PrepareFileForPosting(WatchFolderSettings cfg, UploadEntry entry, FileInfo source)
        {
            var baseFolder = Path.Combine(configuration.WorkingFolder.FullName, cfg.ShortName);
            var destName = (cfg.UseObfuscation ? entry.ObscuredName : entry.CleanedName) + source.Extension;
            var destination = Path.Combine(baseFolder, destName);

            EnsureDirectoryExists(baseFolder);

            source.CopyTo(destination, true);
            var prepared = new FileInfo(destination);

            if (cfg.StripFileMetadata)
                StripMetaDataFromFile(prepared);

            return prepared;
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        }

        private void StripMetaDataFromFile(FileInfo file)
        {
            try
            {
                if (file.Extension.Length < 2) return;

                var ext = file.Extension.Substring(1); // remove dot

                if (ext.Equals("mkv", StringComparison.InvariantCultureIgnoreCase))
                    StripMkvMetaDataFromFile(file);

                if (FfmpegHandledExtensions.Contains(ext))
                    StripMetaDataWithFFmpeg(file);
            }
            catch (Exception ex)
            {
                log.Warn("Could not strip metadata from file. Posting with metadata.", ex);
            }
        }

        private void StripMkvMetaDataFromFile(FileInfo file)
        {
            var mkvPropEdit = new MkvPropEditWrapper(configuration.InactiveProcessTimeout, configuration.MkvPropEditLocation);
            mkvPropEdit.SetTitle(file, string.Empty);
        }

        private void StripMetaDataWithFFmpeg(FileInfo file)
        {
            var ffmpeg = new FFmpegWrapper(configuration.InactiveProcessTimeout, configuration.FFmpegLocation);
            ffmpeg.TryStripMetadata(file);
        }

        private string CleanName(WatchFolderSettings cfg, string name)
        {
            var sb = new StringBuilder(name.Length + 16);
            for (int i = 0; i < name.Length; i++)
            {
                var ch = name[i];
                switch (ch)
                {
                    case ' ':
                        sb.Append('.');
                        break;
                    case '+':
                        sb.Append('.');
                        break;
                    case '&':
                        sb.Append("and");
                        break;
                    default:
                        sb.Append(ch);
                        break;
                }
            }

            // Remove configured characters
            if (cfg.CharsToRemove != null && cfg.CharsToRemove.Length > 0)
            {
                for (int i = 0; i < cfg.CharsToRemove.Length; i++)
                {
                    var c = cfg.CharsToRemove[i];
                    if (c == '&') continue; // already handled above
                    sb.Replace(c.ToString(), string.Empty);
                }
            }

            var cleaned = MultiDotRegex.Replace(sb.ToString(), string.Empty);
            log.InfoFormat("Cleaned the name [{0}] to [{1}]", name, cleaned);
            return cleaned;
        }

        private static string StripNonAscii(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            bool needsClean = false;
            for (int i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (ch < 32 || ch > 126) { needsClean = true; break; }
            }
            if (!needsClean) return input;

            var buffer = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                var ch = input[i];
                if (ch >= 32 && ch <= 126) buffer.Append(ch);
            }
            return buffer.ToString();
        }
    }
}
