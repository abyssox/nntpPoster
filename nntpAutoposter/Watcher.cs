using log4net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Util;
using Util.Configuration;

namespace nntpAutoposter
{
    public class Watcher
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Settings configuration;
        private Task _workerTask;
        private CancellationTokenSource _cts;

        public Watcher(Settings configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void Start()
        {
            foreach (var watchFolderSetting in configuration.WatchFolderSettings)
            {
                log.DebugFormat("Monitoring '{0}' for new files or folders to post.", watchFolderSetting.Path.FullName);
            }

            _cts = new CancellationTokenSource();
            _workerTask = Task.Factory.StartNew(
                () => WatcherLoop(_cts.Token),
                _cts.Token,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public void Stop(int millisecondsTimeout = Timeout.Infinite)
        {
            var localTask = _workerTask;
            var localCts = _cts;

            if (localCts != null && !localCts.IsCancellationRequested)
            {
                try { localCts.Cancel(); } catch { /* ignore */ }
            }

            if (localTask != null)
            {
                try { localTask.Wait(millisecondsTimeout); }
                catch (AggregateException ae)
                {
                    ae.Handle(ex =>
                    {
                        if (ex is OperationCanceledException) return true;
                        log.Warn("Watcher task ended with an exception.", ex);
                        return true;
                    });
                }
            }

            foreach (var watchFolderSetting in configuration.WatchFolderSettings)
            {
                log.InfoFormat("Monitoring '{0}' for files and folders stopped.", watchFolderSetting.Path.FullName);
            }
        }

        private void WatcherLoop(CancellationToken ct)
        {
            var interval = TimeSpan.FromSeconds(
                configuration.FilesystemCheckIntervalSeconds > 0
                    ? configuration.FilesystemCheckIntervalSeconds
                    : 1);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var watchFolderSetting in configuration.WatchFolderSettings)
                    {
                        foreach (var toPost in watchFolderSetting.Path.EnumerateFileSystemInfos())
                        {
                            if (string.Equals(toPost.Extension, ".nfo", StringComparison.OrdinalIgnoreCase))
                                continue;

                            MoveToQueueFolderAndPost(toPost, watchFolderSetting);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.Fatal("Fatal exception in the watcher task.", ex);
                    Environment.Exit(1);
                }

                if (ct.WaitHandle.WaitOne(interval)) break;
            }
        }

        private void MoveToQueueFolderAndPost(FileSystemInfo toPost, WatchFolderSettings folderConfiguration)
        {
            try
            {
                if ((DateTime.Now - toPost.LastAccessTime).TotalMinutes <= configuration.FilesystemCheckTesholdMinutes)
                    return;

                if (toPost.Size() <= 0)
                    return;

                var destination = new DirectoryInfo(Path.Combine(configuration.QueueFolder.FullName, folderConfiguration.ShortName));
                if (!destination.Exists) destination.Create();

                FileSystemInfo queueNfo = null;
                var nfoPath = Path.Combine(folderConfiguration.Path.FullName, toPost.NameWithoutExtension() + ".nfo");
                var nfoFile = new FileInfo(nfoPath);
                if (nfoFile.Exists)
                {
                    queueNfo = nfoFile.Move(destination);
                }

                var queuedItem = toPost.Move(destination);

                AddItemToPostingDb(queuedItem, queueNfo, folderConfiguration);
            }
            catch (Exception ex)
            {
                log.Warn("Error when picking up a file/folder from the watch location.", ex);
            }
        }

        private void AddItemToPostingDb(FileSystemInfo toPost, FileSystemInfo nfoFile, WatchFolderSettings folderConfiguration)
        {
            var size = toPost.Size();
            if (size == 0)
            {
                log.ErrorFormat("File added with a size of 0 bytes, This cannot be uploaded! File name: [{0}]",
                    toPost.FullName);
                return;
            }

            var newUploadEntry = new UploadEntry
            {
                WatchFolderShortName = folderConfiguration.ShortName,
                CreatedAt = DateTime.UtcNow,
                Name = toPost.Name,
                RemoveAfterVerify = configuration.RemoveAfterVerify,
                Cancelled = false,
                Size = size,
                PriorityNum = folderConfiguration.Priority,
                CurrentLocation = Location.Queue,
                HasNfo = nfoFile != null && nfoFile.Exists
            };

            DBHandler.Instance.AddNewUploadEntry(newUploadEntry);
        }
    }
}
