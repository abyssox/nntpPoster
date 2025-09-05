using log4net;
using System;
using System.IO;

namespace Util
{
    public static class Extensions
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static long Size(this FileSystemInfo fsi)
        {
            var attrs = File.GetAttributes(fsi.FullName);
            return (attrs & FileAttributes.Directory) == FileAttributes.Directory
                ? new DirectoryInfo(fsi.FullName).Size()
                : new FileInfo(fsi.FullName).Length;
        }

        public static long Size(this DirectoryInfo d)
        {
            long size = 0;

            try
            {
                foreach (var fi in d.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
                {
                    try { size += fi.Length; }
                    catch (Exception) { /* skip unreadable file */ }
                }
            }
            catch (Exception) { /* skip unreadable directory */ }

            try
            {
                foreach (var sub in d.EnumerateDirectories("*", SearchOption.TopDirectoryOnly))
                {
                    try { size += sub.Size(); }
                    catch (Exception) { /* skip unreadable subtree */ }
                }
            }
            catch (Exception) { /* skip unreadable directory */ }

            return size;
        }

        public static string GetRelativePath(this DirectoryInfo d, FileInfo f)
        {
            string fileFull = Path.GetFullPath(f.FullName);
            string dirFull = Path.GetFullPath(d.FullName).WithEnding(Path.DirectorySeparatorChar.ToString());

            if (!fileFull.StartsWith(dirFull, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The file is not in the directory or a subdirectory.");

            var rel = fileFull.Substring(dirFull.Length);
            if (rel.StartsWith(Path.DirectorySeparatorChar.ToString())) rel = rel.Substring(1);
            return rel;
        }

        public static string NameWithoutExtension(this FileSystemInfo fsi)
        {
            var attrs = File.GetAttributes(fsi.FullName);
            if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                return new DirectoryInfo(fsi.FullName).Name;

            return Path.GetFileNameWithoutExtension(fsi.FullName) ?? string.Empty;
        }

        public static string NameWithoutExtension(this FileInfo f)
        {
            return Path.GetFileNameWithoutExtension(f.Name) ?? string.Empty;
        }

        public static void Copy(this DirectoryInfo sourceDirectory, string destDirName, bool copySubDirs)
        {
            if (!sourceDirectory.Exists)
                throw new DirectoryNotFoundException("Source directory does not exist or could not be found: " + sourceDirectory.FullName);

            if (!Directory.Exists(destDirName))
                Directory.CreateDirectory(destDirName);

            foreach (var file in sourceDirectory.GetFiles())
            {
                var target = Path.Combine(destDirName, file.Name);
                file.CopyTo(target, overwrite: false);
            }

            if (copySubDirs)
            {
                foreach (var subdir in sourceDirectory.GetDirectories())
                {
                    var subDest = Path.Combine(destDirName, subdir.Name);
                    subdir.Copy(subDest, copySubDirs);
                }
            }
        }

        public static FileSystemInfo Move(this FileSystemInfo fsi, DirectoryInfo destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (!destination.Exists) Directory.CreateDirectory(destination.FullName);

            var attrs = File.GetAttributes(fsi.FullName);
            if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
                return MoveFolder(fsi.FullName, destination);
            else
                return MoveFile(fsi.FullName, destination);
        }

        private static FileSystemInfo MoveFolder(string sourceFolder, DirectoryInfo destination)
        {
            var toMove = new DirectoryInfo(sourceFolder);
            var destinationFolder = Path.Combine(destination.FullName, toMove.Name);
            var moved = new DirectoryInfo(destinationFolder);

            if (string.Equals(toMove.FullName.TrimEnd('\\', '/'),
                              moved.FullName.TrimEnd('\\', '/'),
                              StringComparison.OrdinalIgnoreCase))
            {
                log.WarnFormat("Folder '{0}' was already at destination location.", toMove.Name);
                moved.Refresh();
                return moved;
            }

            if (moved.Exists)
            {
                log.WarnFormat("Folder '{0}' already existed. Overwriting!", toMove.Name);
                moved.Delete(true);
            }

            log.DebugFormat("Moving folder [{0}] to [{1}]", sourceFolder, destinationFolder);
            Directory.Move(sourceFolder, destinationFolder);
            moved.Refresh();
            return moved;
        }

        private static FileSystemInfo MoveFile(string sourceFile, DirectoryInfo destination)
        {
            var toMove = new FileInfo(sourceFile);
            var destinationFile = Path.Combine(destination.FullName, toMove.Name);
            var moved = new FileInfo(destinationFile);

            if (string.Equals(toMove.FullName, moved.FullName, StringComparison.OrdinalIgnoreCase))
            {
                log.WarnFormat("File '{0}' was already at destination location.", toMove.Name);
                moved.Refresh();
                return moved;
            }

            if (moved.Exists)
            {
                log.WarnFormat("File '{0}' already existed. Overwriting!", toMove.Name);
                moved.Delete();
            }

            log.DebugFormat("Moving file [{0}] to [{1}]", sourceFile, destinationFile);
            File.Move(sourceFile, destinationFile);
            moved.Refresh();
            return moved;
        }

        public static bool IsSubPathOf(this string path, string baseDirPath)
        {
            string normalizedPath = Path.GetFullPath((path ?? string.Empty).Replace('/', '\\')
                .WithEnding("\\"));

            string normalizedBaseDirPath = Path.GetFullPath((baseDirPath ?? string.Empty).Replace('/', '\\')
                .WithEnding("\\"));

            return normalizedPath.StartsWith(normalizedBaseDirPath, StringComparison.OrdinalIgnoreCase);
        }

        public static string WithEnding(this string str, string ending)
        {
            if (ending == null) ending = string.Empty;
            if (str == null) return ending;

            if (str.EndsWith(ending)) return str;

            for (int i = 1; i <= ending.Length; i++)
            {
                var tmp = str + ending.Right(i);
                if (tmp.EndsWith(ending))
                    return tmp;
            }
            return str + ending;
        }

        public static string Right(this string value, int length)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (length < 0) throw new ArgumentOutOfRangeException("length", length, "Length is less than zero");
            return (length < value.Length) ? value.Substring(value.Length - length) : value;
        }
    }
}
