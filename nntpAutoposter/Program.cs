using log4net;
using System;
using System.Threading;
using Util.Configuration;

namespace nntpAutoposter
{
    class Program
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
        private const int DefaultStopTimeoutMs = Timeout.Infinite;

        static void Main(string[] args)
        {
            Watcher watcher = null;
            AutoPoster poster = null;
            IndexerNotifierBase notifier = null;
            IndexerVerifierBase verifier = null;
            DatabaseCleaner cleaner = null;

            var exitRequested = false;
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                exitRequested = true;
            };

            try
            {
                var configuration = Settings.LoadSettings();

                watcher = new Watcher(configuration);
                watcher.Start();
                log.Info("FileSystemWatcher started");
                Console.WriteLine("FileSystemWatcher started");

                poster = new AutoPoster(configuration);
                poster.Start();
                log.Info("Autoposter started");
                Console.WriteLine("Autoposter started");

                if (configuration.NotificationEnabled)
                {
                    notifier = IndexerNotifierBase.GetActiveNotifier(configuration);
                    if (notifier != null)
                    {
                        notifier.Start();
                        log.Info("Notifier started");
                        Console.WriteLine("Notifier started");
                    }
                    else
                    {
                        log.Info("No notifier");
                        Console.WriteLine("No notifier");
                    }
                }
                else
                {
                    log.Info("Notification disabled");
                    Console.WriteLine("Notification disabled");
                }

                if (configuration.VerificationEnabled)
                { 
                    verifier = IndexerVerifierBase.GetActiveVerifier(configuration);
                    if (verifier != null)
                    {
                        verifier.Start();
                        log.Info("Verifier started");
                        Console.WriteLine("Verifier started");
                    }
                    else
                    {
                        log.Info("No verifier");
                        Console.WriteLine("No verifier");
                    }
                }
                else
                {
                    log.Info("Verification disabled");
                    Console.WriteLine("Verification disabled");
                }

                cleaner = new DatabaseCleaner(configuration);
                cleaner.Start();
                log.Info("DB Cleaner started");
                Console.WriteLine("DB Cleaner started");

                Console.WriteLine("Press 's' to stop gracefully, or press Ctrl+C.");

                while (!exitRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(intercept: true);
                        if (keyInfo.KeyChar == 's' || keyInfo.KeyChar == 'S')
                        {
                            exitRequested = true;
                        }
                    }
                    else
                    {
                        Thread.Sleep(75);
                    }
                }
            }
            catch (Exception ex)
            {
                log.Fatal("Fatal exception when starting the autoposter.", ex);
                Console.Error.WriteLine("Fatal exception: " + ex.Message);
                Environment.ExitCode = -1;
            }
            finally
            {
                if (cleaner != null)
                {
                    SafeStop("DB Cleaner", () => cleaner.Stop(DefaultStopTimeoutMs));
                }

                if (verifier != null)
                {
                    SafeStop("Verifier", () => verifier.Stop());
                }

                if (notifier != null)
                {
                    SafeStop("Notifier", () => notifier.Stop());
                }

                if (poster != null)
                {
                    SafeStop("Autoposter", () => poster.Stop(DefaultStopTimeoutMs));
                }

                if (watcher != null)
                {
                    SafeStop("FileSystemWatcher", () => watcher.Stop());
                }
            }
        }

        private static void SafeStop(string name, Action stopAction)
        {
            try
            {
                stopAction();
                LogManager.GetLogger(typeof(Program)).Info(name + " stopped");
                Console.WriteLine(name + " stopped");
            }
            catch (Exception ex)
            {
                LogManager.GetLogger(typeof(Program)).Warn(name + " failed to stop cleanly.", ex);
                Console.WriteLine(name + " failed to stop cleanly. See log for details.");
            }
        }
    }
}
