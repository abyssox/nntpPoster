using ExternalProcessWrappers;
using log4net;
using nntpPoster.yEncLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Util;
using Util.Configuration;

namespace nntpPoster
{
    public class UsenetPoster
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public event EventHandler<UploadSpeedReport> NewUploadSpeedReport;

        private readonly Settings configuration;
        private readonly WatchFolderSettings folderConfiguration;

        public UsenetPoster(Settings configuration, WatchFolderSettings folderConfiguration)
        {
            this.configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            this.folderConfiguration = folderConfiguration ?? throw new ArgumentNullException(nameof(folderConfiguration));
        }

        private int TotalPartCount { get; set; }
        private readonly object UploadLock = new object();
        private int UploadedPartCount { get; set; }
        private long TotalUploadedBytes { get; set; }
        private DateTime UploadStartTimeUtc { get; set; }

        public XDocument PostToUsenet(FileSystemInfo toPost, string rarPassword, bool saveNzb = true, FileInfo nfoFile = null, bool keepProcessingFolderAfterError = false)
        {
            var title = toPost.NameWithoutExtension();
            return PostToUsenet(toPost, title, rarPassword, saveNzb, nfoFile, keepProcessingFolderAfterError);
        }

        public XDocument PostToUsenet(FileSystemInfo toPost, string title, string rarPassword, bool saveNzb = true, FileInfo nfoFile = null, bool keepProcessingFolderAfterError = false)
        {
            if (toPost == null) throw new ArgumentNullException(nameof(toPost));
            if (toPost.Size() == 0)
                throw new Exception(string.Format("File/Folder [{0}] is 0 bytes in size, aborting post", toPost.FullName));

            var nameNoExt = toPost.NameWithoutExtension();

            using (var poster = new NntpMessagePoster(configuration, folderConfiguration))
            {
                poster.PartPosted += Poster_PartPosted;
                DirectoryInfo processedFiles = null;
                var hasError = true;

                try
                {
                    var startTimeUtc = DateTime.UtcNow;

                    processedFiles = MakeProcessingFolder(nameNoExt);
                    MakeRarAndParFiles(toPost, nameNoExt, processedFiles, rarPassword);

                    // Optional .nfo
                    if (nfoFile != null && nfoFile.Exists)
                    {
                        var processedNfo = Path.Combine(processedFiles.FullName, nameNoExt + ".nfo");
                        nfoFile.CopyTo(processedNfo, overwrite: true);
                    }

                    // Build posting plan once
                    var fileInfos = processedFiles.GetFiles().OrderBy(f => f.Name).ToArray();
                    var filesToPost = fileInfos.Select(f => new FileToPost(configuration, folderConfiguration, f)).ToArray();

                    var postedFiles = new List<PostedFileInfo>(filesToPost.Length);
                    TotalPartCount = filesToPost.Sum(f => f.TotalParts);
                    UploadedPartCount = 0;
                    TotalUploadedBytes = 0;
                    UploadStartTimeUtc = DateTime.UtcNow;

                    var fileCount = 1;
                    var rand = new Random();

                    foreach (var fileToPost in filesToPost)
                    {
                        var fileStartUtc = DateTime.UtcNow;

                        string comment1;
                        if (folderConfiguration.UseRandomMessageSubjects)
                        {
                            var randomTotal = rand.Next(40) + 1;
                            var randomIndex = rand.Next(randomTotal) + 1;
                            var randomTitle = RandomStringGenerator.GetRandomString(15, 30);
                            comment1 = string.Format("{0} [{1}/{2}]", randomTitle, randomIndex, randomTotal);
                        }
                        else
                        {
                            comment1 = string.Format("{0} [{1}/{2}]", title, fileCount++, filesToPost.Length);
                        }

                        var postInfo = fileToPost.PostYEncFile(poster, comment1, "");

                        if (log.IsInfoEnabled)
                        {
                            var dt = DateTime.UtcNow - fileStartUtc;
                            var fileSpeed = dt.TotalSeconds > 0 ? (double)fileToPost.File.Length / dt.TotalSeconds : 0d;
                            log.InfoFormat("Posted file {0} with a speed of {1}",
                                fileToPost.File.Name, UploadSpeedReport.GetHumanReadableSpeed(fileSpeed));
                        }

                        postedFiles.Add(postInfo);
                    }

                    poster.WaitTillCompletion();

                    if (log.IsInfoEnabled)
                    {
                        var totalElapsed = DateTime.UtcNow - startTimeUtc;
                        var uploadElapsed = DateTime.UtcNow - UploadStartTimeUtc;
                        var avgSpeed = totalElapsed.TotalSeconds > 0 ? TotalUploadedBytes / totalElapsed.TotalSeconds : 0d;
                        var ulSpeed = uploadElapsed.TotalSeconds > 0 ? TotalUploadedBytes / uploadElapsed.TotalSeconds : 0d;

                        log.InfoFormat("Upload of [{0}] has completed at {1} with an upload speed of {2}",
                            title,
                            UploadSpeedReport.GetHumanReadableSpeed(avgSpeed),
                            UploadSpeedReport.GetHumanReadableSpeed(ulSpeed));
                    }

                    Console.WriteLine();

                    var nzbDoc = GenerateNzbFromPostInfo(toPost.Name, postedFiles, rarPassword);
                    if (saveNzb && configuration.NzbOutputFolder != null)
                    {
                        var nzbPath = Path.Combine(configuration.NzbOutputFolder.FullName, nameNoExt + ".nzb");
                        nzbDoc.Save(nzbPath);
                    }

                    hasError = false;
                    return nzbDoc;
                }
                finally
                {
                    poster.PartPosted -= Poster_PartPosted;

                    if (keepProcessingFolderAfterError && hasError)
                    {
                        log.Info("Keeping processed folder after error.");
                    }
                    else
                    {
                        if (processedFiles != null)
                        {
                            try
                            {
                                if (processedFiles.Exists)
                                {
                                    log.Info("Deleting processed folder");
                                    processedFiles.Delete(true);
                                }
                            }
                            catch (Exception ex)
                            {
                                log.Warn("Failed to delete processed folder.", ex);
                            }
                        }
                    }
                }
            }
        }

        private void Poster_PartPosted(object sender, YEncFilePart e)
        {
            int uploadedParts;
            long totalBytes;

            lock (UploadLock)
            {
                UploadedPartCount++;
                uploadedParts = UploadedPartCount;
                TotalUploadedBytes += e.Size;
                totalBytes = TotalUploadedBytes;
            }

            var elapsed = DateTime.UtcNow - UploadStartTimeUtc;
            var speed = elapsed.TotalSeconds > 0 ? (double)totalBytes / elapsed.TotalSeconds : 0d;

            OnNewUploadSpeedReport(new UploadSpeedReport
            {
                TotalParts = TotalPartCount,
                UploadedParts = uploadedParts,
                BytesPerSecond = speed,
                CurrentlyPostingName = e.SourcefileName
            });
        }

        protected virtual void OnNewUploadSpeedReport(UploadSpeedReport e)
        {
            Console.Write("\r" + e + "   ");
            var handler = NewUploadSpeedReport;
            if (handler != null) handler(this, e);
        }

        private DirectoryInfo MakeProcessingFolder(string nameWithoutExtension)
        {
            var processedFolder = new DirectoryInfo(Path.Combine(configuration.WorkingFolder.FullName, nameWithoutExtension + "_readyToPost"));
            if (!processedFolder.Exists) processedFolder.Create();
            return processedFolder;
        }

        private void MakeRarAndParFiles(FileSystemInfo toPost, string nameWithoutExtension, DirectoryInfo processedFolder, string password)
        {
            var size = toPost.Size();
            var humanReadableSize = UploadSpeedReport.GetHumanReadableSize(size);
            if (log.IsDebugEnabled) log.DebugFormat("ToPost size: {0} ({1})", size, humanReadableSize);

            var rule = configuration.RarNParSettings
                .OrderByDescending(r => r.FromSize)
                .FirstOrDefault(r => r.FromSizeBytes < size) ?? configuration.RarNParSettings.OrderBy(r => r.FromSize).First();

            if (log.IsDebugEnabled)
                log.DebugFormat("Rar and par settings found: [{0},{1},{2}]", rule.FromSize, rule.RarSize, rule.Par2Percentage);

            var rarWrapper = new RarWrapper(configuration.InactiveProcessTimeout, configuration.RarLocation);
            var rarStartUtc = DateTime.UtcNow;

            rarWrapper.Compress(
                toPost,
                processedFolder,
                nameWithoutExtension,
                Settings.DetermineOptimalRarSize(rule.RarSize, configuration.YEncLineSize, configuration.YEncLinesPerMessage),
                password,
                configuration.RarExtraParameters);

            if (log.IsInfoEnabled)
            {
                var rarDt = DateTime.UtcNow - rarStartUtc;
                var rarSpeed = rarDt.TotalSeconds > 0 ? (double)size / rarDt.TotalSeconds : 0d;
                log.InfoFormat("Rarred {0} of file(s) with a speed of {1}", humanReadableSize, UploadSpeedReport.GetHumanReadableSpeed(rarSpeed));
            }

            var parWrapper = new ParWrapper(configuration.InactiveProcessTimeout, configuration.ParLocation, configuration.ParCommandFormat);

            var processedSize = processedFolder.Size();
            var partSize = parWrapper.CalculatePartSize(processedSize, configuration.YEncPartSize);

            var parStartUtc = DateTime.UtcNow;

            // guard against YEncPartSize == 0
            var yencPart = configuration.YEncPartSize > 0 ? configuration.YEncPartSize : 1;
            var loops = 0;
            var maxLoops = 10;

            while (loops < maxLoops)
            {
                try
                {
                    parWrapper.CreateParFilesInDirectory(processedFolder, nameWithoutExtension, partSize, rule.Par2Percentage, configuration.ParExtraParameters);
                    break;
                }
                catch (Par2BlockSizeTooSmallException)
                {
                    partSize += yencPart;
                    loops++;
                    if (loops >= maxLoops)
                        throw;
                }
            }

            if (log.IsInfoEnabled)
            {
                var parDt = DateTime.UtcNow - parStartUtc;
                var parSpeed = parDt.TotalSeconds > 0 ? (double)size / parDt.TotalSeconds : 0d;
                log.InfoFormat("Parred {0} of file(s) with a speed of {1}", UploadSpeedReport.GetHumanReadableSize(size), UploadSpeedReport.GetHumanReadableSpeed(parSpeed));
            }
        }

        private XDocument GenerateNzbFromPostInfo(string title, List<PostedFileInfo> postedFiles, string rarPassword)
        {
            XNamespace ns = "http://www.newzbin.com/DTD/2003/nzb";

            var doc = new XDocument(
                new XDeclaration("1.0", "utf-8", "yes"),
                new XDocumentType("nzb", "-//newzBin//DTD NZB 1.1//EN", "http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd", null),
                new XElement(ns + "nzb",
                    new XElement(ns + "head",
                        new XElement(ns + "meta", new XAttribute("type", "title"), title),
                        string.IsNullOrWhiteSpace(rarPassword)
                            ? null
                            : new XElement(ns + "meta", new XAttribute("type", "password"), rarPassword)
                    ),
                    postedFiles.Select(f =>
                        new XElement(ns + "file",
                            new XAttribute("poster", f.FromAddress),
                            new XAttribute("date", f.GetUnixPostedDateTime()),
                            new XAttribute("subject", f.NzbSubjectName),
                            new XElement(ns + "groups",
                                f.PostedGroups.Select(g => new XElement(ns + "group", g))
                            ),
                            new XElement(ns + "segments",
                                f.Segments
                                    .OrderBy(s => s.SegmentNumber)
                                    .Select(s =>
                                        new XElement(ns + "segment",
                                            new XAttribute("bytes", s.Bytes),
                                            new XAttribute("number", s.SegmentNumber),
                                            s.MessageIdWithoutBrackets
                                        )
                                    )
                            )
                        )
                    )
                )
            );

            return doc;
        }
    }
}