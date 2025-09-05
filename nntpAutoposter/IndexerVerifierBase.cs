using log4net;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Util.Configuration;

namespace nntpAutoposter
{
    public abstract class IndexerVerifierBase
    {
        protected static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private Task _workerTask;
        private CancellationTokenSource _cts;

        protected Settings Configuration { get; set; }

        public static IndexerVerifierBase GetActiveVerifier(Settings configuration)
        {
            if ("NewznabSearch".Equals(configuration.VerificationType, StringComparison.InvariantCultureIgnoreCase))
                return new IndexerVerifierNewznabSearch(configuration);

            if ("PostVerify".Equals(configuration.VerificationType, StringComparison.InvariantCultureIgnoreCase))
                return new IndexerVerifierPostVerify(configuration);

            if ("Dummy".Equals(configuration.VerificationType, StringComparison.InvariantCultureIgnoreCase))
                return new IndexerVerifierDummy(configuration);

            if (!string.IsNullOrEmpty(configuration.VerificationType))
                log.WarnFormat("{0} is an unknown verification type. Valid values are 'NewznabSearch', 'PostVerify' and 'Dummy'", configuration.VerificationType);
            else
                log.Info("No verification type defined in configuration.");

            if (configuration.BackupFolder != null)
                log.Warn("You will have to clean up the backup directory manually or your disk will fill up.");

            return null;
        }

        protected IndexerVerifierBase(Settings configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _workerTask = Task.Factory.StartNew(
                () => IndexerVerifierLoop(_cts.Token),
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

            if (localTask == null) return;

            try
            {
                localTask.Wait(millisecondsTimeout);
            }
            catch (AggregateException ae)
            {
                ae.Handle(ex =>
                {
                    if (ex is OperationCanceledException) return true;
                    log.Warn("Indexer verifier task ended with an exception.", ex);
                    return true;
                });
            }
        }

        private void IndexerVerifierLoop(CancellationToken ct)
        {
            var interval = TimeSpan.FromMinutes(
                Configuration.VerifierIntervalMinutes > 0 ? Configuration.VerifierIntervalMinutes : 1);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    VerifyUploadsOnIndexer();
                }
                catch (Exception ex)
                {
                    log.Fatal("Fatal exception in the indexer verifier task.", ex);
                    Environment.Exit(1);
                }

                if (ct.WaitHandle.WaitOne(interval)) break;
            }
        }

        private void VerifyUploadsOnIndexer()
        {
            var toVerify = DBHandler.Instance.GetUploadEntriesToVerify();
            foreach (var upload in toVerify)
            {
                try
                {
                    var fullPath = upload.GetCurrentPath(Configuration, upload.Name);

                    var backupExists = Directory.Exists(fullPath) || File.Exists(fullPath);
                    if (!backupExists)
                    {
                        log.WarnFormat("The upload [{0}] was removed from the backup folder, cancelling verification.", upload.Name);
                        upload.Cancelled = true;
                        DBHandler.Instance.UpdateUploadEntry(upload);
                        continue;
                    }

                    if (!upload.UploadedAt.HasValue)
                    {
                        log.DebugFormat("Upload [{0}] has no UploadedAt; skipping.", upload.CleanedName);
                        continue;
                    }

                    var ageMinutes = (DateTime.UtcNow - upload.UploadedAt.Value).TotalMinutes;
                    if (ageMinutes < Configuration.VerifyAfterMinutes)
                    {
                        log.DebugFormat("The upload [{0}] is younger than {1} minutes. Skipping check.",
                            upload.CleanedName, Configuration.VerifyAfterMinutes);
                        continue;
                    }

                    VerifyEntryOnIndexer(upload);
                }
                catch (Exception ex)
                {
                    log.Error(string.Format("Could not verify release [{0}] on index:", upload.CleanedName), ex);
                }
            }
        }

        protected virtual void VerifyEntryOnIndexer(UploadEntry upload)
        {
            if (UploadIsOnIndexer(upload))
            {
                upload.SeenOnIndexAt = DateTime.UtcNow;
                DBHandler.Instance.UpdateUploadEntry(upload);
                log.InfoFormat("Release [{0}] has been found on the indexer.", upload.CleanedName);

                if (upload.RemoveAfterVerify)
                {
                    upload.Delete(Configuration);
                }
            }
            else
            {
                log.WarnFormat("Release [{0}] has NOT been found on the indexer. Checking if a repost is required.",
                    upload.CleanedName);
                RepostIfRequired(upload);
            }
        }

        protected virtual void RepostIfRequired(UploadEntry upload)
        {
            if (!upload.UploadedAt.HasValue)
            {
                log.DebugFormat("Upload [{0}] has no UploadedAt; skipping repost check.", upload.CleanedName);
                return;
            }

            var ageInMinutes = (DateTime.UtcNow - upload.UploadedAt.Value).TotalMinutes;

            if (ageInMinutes > Configuration.RepostAfterMinutes)
            {
                log.WarnFormat("Could not find [{0}] after {1} minutes, reposting, attempt {2}",
                    upload.CleanedName, Configuration.RepostAfterMinutes, upload.UploadAttempts);
                upload.UploadedAt = null;
                upload.Move(Configuration, Location.Queue);
                DBHandler.Instance.UpdateUploadEntry(upload);
            }
            else
            {
                log.InfoFormat("A repost of [{0}] is not required as {1} minutes have not passed since upload.",
                    upload.CleanedName, Configuration.RepostAfterMinutes);
            }
        }

        protected abstract bool UploadIsOnIndexer(UploadEntry upload);
    }
}