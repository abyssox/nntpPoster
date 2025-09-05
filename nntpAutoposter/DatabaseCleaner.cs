using log4net;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Util.Configuration;

namespace nntpAutoposter
{
    public class DatabaseCleaner
    {
        protected static readonly ILog log = LogManager.GetLogger(
            MethodBase.GetCurrentMethod().DeclaringType);

        private readonly Settings configuration;
        private Task _workerTask;
        private CancellationTokenSource _cts;

        public DatabaseCleaner(Settings configuration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _workerTask = Task.Factory.StartNew(
                () => CleanupLoop(_cts.Token),
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
                if (!localTask.Wait(millisecondsTimeout))
                {
                    log.Warn("Stop timed out waiting for database cleaner to finish.");
                }
            }
            catch (AggregateException ae)
            {
                ae.Handle(ex =>
                {
                    if (ex is OperationCanceledException) return true;
                    log.Error("Database cleaner task faulted while stopping.", ex);
                    return true;
                });
            }
        }

        private void CleanupLoop(CancellationToken ct)
        {
            var hours = configuration.DatabaseCleanupHours;
            var interval = TimeSpan.FromHours(hours > 0 ? hours : 1);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    log.InfoFormat("Cleaning up database, removing entries older than {0} days",
                        configuration.DatabaseCleanupKeepdays);

                    DBHandler.Instance.CleanUploadEntries(configuration.DatabaseCleanupKeepdays);
                    DBHandler.Instance.Vacuum();
                }
                catch (Exception ex)
                {
                    log.Fatal("Fatal exception in the database cleanup task.", ex);
                    Environment.Exit(1);
                }

                if (ct.IsCancellationRequested) break;
                if (ct.WaitHandle.WaitOne(interval)) break;
            }
        }
    }
}
