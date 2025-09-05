using System;
using System.IO;

namespace ExternalProcessWrappers
{
    public class RarWrapper : ExternalProcessWrapperBase
    {
        protected override string ProcessPathLocation => "rar";

        public RarWrapper(int inactiveProcessTimeout)
            : base(inactiveProcessTimeout)
        {
        }

        public RarWrapper(int inactiveProcessTimeout, string rarLocation)
            : base(inactiveProcessTimeout, rarLocation)
        {
        }

        public void Compress(FileSystemInfo source, DirectoryInfo destination, string archiveName,
                             int partSize, string password, string extraParams)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (string.IsNullOrWhiteSpace(archiveName)) throw new ArgumentException("Archive name is required.", nameof(archiveName));
            if (partSize <= 0) throw new ArgumentOutOfRangeException(nameof(partSize), "Part size must be > 0.");
            if (!destination.Exists) destination.Create();

            string inputArg;
            var attrs = File.GetAttributes(source.FullName);
            if ((attrs & FileAttributes.Directory) == FileAttributes.Directory)
            {
                inputArg = Path.Combine(source.FullName, "*");
            }
            else
            {
                inputArg = source.FullName;
            }

            string passwordArg = string.IsNullOrWhiteSpace(password) ? string.Empty : $"-hp{Quote(password)}";
            string extra = string.IsNullOrWhiteSpace(extraParams) ? string.Empty : extraParams.Trim();
            string archiveBase = Path.Combine(destination.FullName, archiveName);
            string args =
                $"a -ep1 {passwordArg} -m0 -r -v{partSize}b {extra} {Quote(archiveBase)} {Quote(inputArg)}";

            ExecuteProcess(args);
        }

        private static string Quote(string value)
        {
            if (value == null) return "\"\"";
            string escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }
    }
}
