using log4net;
using System;
using System.IO;
using Util;
using Util.Configuration;

namespace nntpAutoposter
{
    public class UploadEntry
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public long ID { get; set; }
        public string Name { get; set; }
        public long Size { get; set; }
        public string CleanedName { get; set; }
        public string ObscuredName { get; set; }
        public bool RemoveAfterVerify { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UploadedAt { get; set; }
        public DateTime? NotifiedIndexerAt { get; set; }
        public DateTime? SeenOnIndexAt { get; set; }
        public bool Cancelled { get; set; }
        public string WatchFolderShortName { get; set; }
        public long UploadAttempts { get; set; }
        public string RarPassword { get; set; }
        public long PriorityNum { get; set; }
        public string NzbContents { get; set; }
        public bool IsRepost { get; set; }
        public long NotificationCount { get; set; }
        public Location CurrentLocation { get; set; }
        public bool HasNfo { get; set; }

        public void Move(Settings configuration, Location newLocation)
        {
            if (CurrentLocation == newLocation)
            {
                log.WarnFormat("Upload is already at the '{0}' location, cancelling move.", CurrentLocation);
                return;
            }

            var targetFolder = DetermineTargetLocation(configuration, newLocation);
            EnsureDirectory(targetFolder);

            var sourceFullPath = GetCurrentPath(configuration, Name);

            try
            {
                FileSystemInfo fso = GetFso(sourceFullPath, out string nameWithoutExtension);
                fso.Move(targetFolder);

                if (HasNfo)
                {
                    try
                    {
                        var nfoFullPath = GetCurrentPath(configuration, nameWithoutExtension + ".nfo");
                        var sourceNfo = new FileInfo(nfoFullPath);
                        if (sourceNfo.Exists)
                        {
                            sourceNfo.Move(targetFolder);
                        }
                        else
                        {
                            log.Warn("Can no longer find the .nfo for this upload, removing HasNfo tag.");
                            HasNfo = false;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        log.Warn("Can no longer find the .nfo for this upload, removing HasNfo tag.");
                        HasNfo = false;
                    }
                }

                CurrentLocation = newLocation;
            }
            catch (FileNotFoundException)
            {
                log.WarnFormat("Can no longer find '{0}', cancelling move.", sourceFullPath);
                CurrentLocation = Location.None;
            }
            catch (DirectoryNotFoundException ex)
            {
                log.Warn("Directory not found during move operation.", ex);
                CurrentLocation = Location.None;
            }
            catch (IOException ex)
            {
                log.Warn("I/O error during move operation.", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn("Access denied during move operation.", ex);
            }
        }

        public void Delete(Settings configuration)
        {
            var fullPath = GetCurrentPath(configuration, Name);

            try
            {
                FileSystemInfo fso = GetFso(fullPath, out string nameWithoutExtension);

                if (fso is DirectoryInfo)
                {
                    Directory.Delete(fullPath, true);
                }
                else
                {
                    File.Delete(fullPath);
                }

                if (HasNfo)
                {
                    try
                    {
                        var nfoFullPath = GetCurrentPath(configuration, nameWithoutExtension + ".nfo");
                        if (File.Exists(nfoFullPath))
                        {
                            File.Delete(nfoFullPath);
                        }
                        else
                        {
                            log.Warn("Can no longer find the .nfo for this upload, removing HasNfo tag. cannot delete.");
                            HasNfo = false;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        log.Warn("Can no longer find the .nfo for this upload, removing HasNfo tag. cannot delete.");
                        HasNfo = false;
                    }
                }

                CurrentLocation = Location.None;
            }
            catch (FileNotFoundException)
            {
                log.WarnFormat("Can no longer find '{0}', cannot delete.", fullPath);
                CurrentLocation = Location.None;
            }
            catch (DirectoryNotFoundException ex)
            {
                log.Warn("Directory not found during delete operation.", ex);
                CurrentLocation = Location.None;
            }
            catch (IOException ex)
            {
                log.Warn("I/O error during delete operation.", ex);
                // Location becomes None since the main file is likely gone or broken
                CurrentLocation = Location.None;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn("Access denied during delete operation.", ex);
                // Don't change CurrentLocation in this case
            }
        }

        public string GetCurrentPath(Settings configuration, string fileName)
        {
            switch (CurrentLocation)
            {
                case Location.Watch:
                    return Path.Combine(configuration.GetWatchFolderSettings(WatchFolderShortName).Path.FullName, fileName);
                case Location.Queue:
                    return Path.Combine(configuration.QueueFolder.FullName, WatchFolderShortName, fileName);
                case Location.Backup:
                    return Path.Combine(configuration.BackupFolder.FullName, WatchFolderShortName, fileName);
                case Location.Failed:
                    return Path.Combine(configuration.PostFailedFolder.FullName, WatchFolderShortName, fileName);
                default:
                    throw new Exception($"Current path of '{fileName}' cannot be determined.");
            }
        }

        private DirectoryInfo DetermineTargetLocation(Settings configuration, Location newLocation)
        {
            switch (newLocation)
            {
                case Location.Queue:
                    return new DirectoryInfo(Path.Combine(configuration.QueueFolder.FullName, WatchFolderShortName));
                case Location.Backup:
                    return new DirectoryInfo(Path.Combine(configuration.BackupFolder.FullName, WatchFolderShortName));
                case Location.Failed:
                    return new DirectoryInfo(Path.Combine(configuration.PostFailedFolder.FullName, WatchFolderShortName));
                default:
                    throw new Exception("Target path can only be Queue, Backup or Failed");
            }
        }

        private static void EnsureDirectory(DirectoryInfo dir)
        {
            if (!dir.Exists) dir.Create();
        }

        /// <summary>
        /// Resolve a path to FileInfo/DirectoryInfo and return nameWithoutExtension without extra allocations later.
        /// </summary>
        private static FileSystemInfo GetFso(string fullPath, out string nameWithoutExtension)
        {
            var attrs = File.GetAttributes(fullPath);
            if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
            {
                var di = new DirectoryInfo(fullPath);
                nameWithoutExtension = di.NameWithoutExtension();
                return di;
            }
            else
            {
                var fi = new FileInfo(fullPath);
                nameWithoutExtension = fi.NameWithoutExtension();
                return fi;
            }
        }
    }
}
