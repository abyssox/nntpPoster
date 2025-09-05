using System;
using System.IO;
using System.Text;

namespace ExternalProcessWrappers
{
    public class ParWrapper : ExternalProcessWrapperBase
    {
        private const int MaxNumberOfBlocks = short.MaxValue;
        private volatile bool _blockSizeTooSmall;

        protected override string ProcessPathLocation { get { return "par2"; } }

        public ParWrapper(int inactiveProcessTimeout) : base(inactiveProcessTimeout) { }

        public string CommandFormat { get; set; }

        public ParWrapper(int inactiveProcessTimeout, string parLocation, string commandFormat)
            : base(inactiveProcessTimeout, parLocation)
        {
            CommandFormat = commandFormat;
        }

        public void CreateParFilesInDirectory(
            DirectoryInfo workingFolder,
            string nameWithoutExtension,
            int blockSize,
            int redundancyPercentage,
            string extraParams)
        {
            if (workingFolder == null) throw new ArgumentNullException(nameof(workingFolder));
            if (string.IsNullOrWhiteSpace(nameWithoutExtension)) throw new ArgumentException("Name (without extension) is required.", nameof(nameWithoutExtension));
            if (blockSize <= 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be > 0.");
            if (redundancyPercentage < 0 || redundancyPercentage > 100) throw new ArgumentOutOfRangeException(nameof(redundancyPercentage), "Redundancy must be 0..100.");
            if (string.IsNullOrWhiteSpace(CommandFormat)) throw new InvalidOperationException("Par command format is not configured.");

            _blockSizeTooSmall = false;

            string fmt = NormalizeParCommandFormat(CommandFormat);
            string parBase = nameWithoutExtension.Replace("\"", string.Empty) + ".par2";
            string parBaseArg = Quote(parBase);
            string filesArg = GetFileList(workingFolder);

            string extra = string.IsNullOrWhiteSpace(extraParams) ? string.Empty : extraParams.Trim();

            string parParameters = string.Format(
                fmt,
                blockSize,
                redundancyPercentage,
                parBaseArg,   // {2}
                filesArg,     // {3}
                extra         // {4}
            );

            try
            {
                ExecuteProcess(parParameters, workingFolder.FullName);
            }
            catch (Exception ex)
            {
                if (_blockSizeTooSmall)
                    throw new Par2BlockSizeTooSmallException("Block size too small", ex);
                throw;
            }
        }

        private static string NormalizeParCommandFormat(string raw)
        {
            string fmt = raw
                .Replace("\"{2}{3}\"", "{2}{3}")
                .Replace("\"{2}\"{3}", "{2}{3}")
                .Replace("{2}\"{3}\"", "{2}{3}")
                .Replace("\"{2}\"", "{2}")
                .Replace("\"{3}\"", "{3}");

            fmt = fmt.Replace("{2}{3}", "{2} {3}");

            return fmt;
        }

        private string GetFileList(DirectoryInfo workingFolder)
        {
            var files = workingFolder.EnumerateFiles("*", SearchOption.AllDirectories);
            var sb = new StringBuilder();

            foreach (var file in files)
            {
                string rel = GetRelativePath(workingFolder.FullName, file.FullName);
                sb.Append(' ').Append(Quote(rel));
            }

            return sb.ToString();
        }

        protected override void Process_ErrorDataReceived(object sender, string outputLine)
        {
            base.Process_ErrorDataReceived(sender, outputLine);

            if (string.IsNullOrEmpty(outputLine)) return;

            var line = outputLine.ToLowerInvariant();
            if (line.Contains("block size is too small")
                || line.Contains("too many input slices")
                || (line.Contains("too many") && line.Contains("slices")))
            {
                _blockSizeTooSmall = true;
            }
        }

        public int CalculatePartSize(long sizeOfFiles, int yEncPartSize)
        {
            if (yEncPartSize <= 0) throw new ArgumentOutOfRangeException(nameof(yEncPartSize), "yEnc part size must be > 0.");
            if (sizeOfFiles <= 0) return yEncPartSize;

            decimal calcPartSize = (decimal)sizeOfFiles / MaxNumberOfBlocks;
            decimal multiplier = Math.Ceiling(calcPartSize / yEncPartSize);
            if (multiplier < 1) multiplier = 1;

            var result = (long)(yEncPartSize * multiplier);
            if (result > int.MaxValue) return int.MaxValue;
            return (int)result;
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            string escaped = value.Replace("\"", "\"\"");
            return "\"" + escaped + "\"";
        }

        // .NET 4.7.1 lacks Path.GetRelativePath
        private static string GetRelativePath(string basePath, string fullPath)
        {
            if (string.IsNullOrEmpty(basePath)) return fullPath ?? string.Empty;
            if (string.IsNullOrEmpty(fullPath)) return string.Empty;

            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) &&
                !basePath.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                basePath += Path.DirectorySeparatorChar;
            }

            try
            {
                var baseUri = new Uri(AppendDirectorySeparator(basePath));
                var fullUri = new Uri(fullPath);
                var rel = baseUri.MakeRelativeUri(fullUri).ToString();
                rel = Uri.UnescapeDataString(rel.Replace('/', Path.DirectorySeparatorChar));
                return rel;
            }
            catch
            {
                if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                    return fullPath.Substring(basePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullPath;
            }
        }

        private static string AppendDirectorySeparator(string path)
        {
            if (string.IsNullOrEmpty(path)) return Path.DirectorySeparatorChar.ToString();
            char c = path[path.Length - 1];
            if (c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
                return path + Path.DirectorySeparatorChar;
            return path;
        }
    }
}