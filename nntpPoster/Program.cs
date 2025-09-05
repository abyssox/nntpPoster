using log4net;
using System;
using System.IO;
using Util.Configuration;

namespace nntpPoster
{
    class Program
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const int ExitOk = 0;
        private const int ExitMissingArg = 1;
        private const int ExitPathNotFound = 2;
        private const int ExitUnhandledError = 3;

        static int Main(string[] args)
        {
            try
            {
                if (args == null || args.Length < 1 || IsHelp(args[0]))
                {
                    PrintUsage();
                    return ExitMissingArg;
                }

                string watchShortName = "Default";
                string explicitTitle = null;
                string pathArg = null;

                for (int i = 0; i < args.Length; i++)
                {
                    var a = args[i];
                    if (a.Equals("--watch", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        watchShortName = args[++i];
                    }
                    else if (a.Equals("--title", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        explicitTitle = args[++i];
                    }
                    else if (!a.StartsWith("--"))
                    {
                        pathArg = a;
                    }
                }

                if (string.IsNullOrWhiteSpace(pathArg))
                {
                    Console.Error.WriteLine("Error: please supply a file or folder to upload.");
                    PrintUsage();
                    return ExitMissingArg;
                }

                string fullPath = GetFullPathSafe(pathArg);
                FileSystemInfo toUpload = ResolveFileSystemInfo(fullPath);
                if (toUpload == null || !toUpload.Exists)
                {
                    Console.Error.WriteLine("Error: the supplied file or folder does not exist: {0}", fullPath);
                    return ExitPathNotFound;
                }

                var config = Settings.LoadSettings();
                var watch = config.GetWatchFolderSettings(watchShortName);
                var poster = new UsenetPoster(config, watch);
                poster.NewUploadSpeedReport += Poster_NewUploadSpeedReport;

                if (!string.IsNullOrWhiteSpace(explicitTitle))
                {
                    poster.PostToUsenet(toUpload, explicitTitle, null);
                }
                else
                {
                    poster.PostToUsenet(toUpload, (string)null);
                }

                poster.NewUploadSpeedReport -= Poster_NewUploadSpeedReport;
                Console.WriteLine();
                return ExitOk;
            }
            catch (Exception ex)
            {
                log.Fatal("Fatal exception in uploader.", ex);
                Console.Error.WriteLine("Fatal: " + ex.Message);
                return ExitUnhandledError;
            }
        }

        private static void Poster_NewUploadSpeedReport(object sender, UploadSpeedReport e)
        {
            Console.Write("\r{0}          ", e.ToString());
        }

        private static bool IsHelp(string arg)
        {
            return string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "/?", StringComparison.OrdinalIgnoreCase);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  nntpPoster <path> [--watch <ShortName>] [--title <Title>]");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  nntpPoster \"D:\\Uploads\\MyFolder\"");
            Console.WriteLine("  nntpPoster \"C:\\file.iso\" --watch Movies --title \"Interesting.Movie.2024\"");
        }

        private static string GetFullPathSafe(string path)
        {
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        private static FileSystemInfo ResolveFileSystemInfo(string fullPath)
        {
            try
            {
                if (Directory.Exists(fullPath)) return new DirectoryInfo(fullPath);
                if (File.Exists(fullPath)) return new FileInfo(fullPath);
            }
            catch
            {
                // swallow and return null → caller handles as "not found"
            }
            return null;
        }
    }
}
