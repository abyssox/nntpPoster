using System;
using System.Globalization;

namespace nntpPoster
{
    public class UploadSpeedReport
    {
        public int TotalParts { get; set; }
        public int UploadedParts { get; set; }
        public double BytesPerSecond { get; set; }
        public string CurrentlyPostingName { get; set; }

        public override string ToString()
        {
            int tpl = TotalParts > 0 ? TotalParts.ToString(CultureInfo.InvariantCulture).Length : 1;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0," + tpl + "} of {1} parts uploaded at {2}",
                UploadedParts,
                TotalParts,
                GetHumanReadableSpeed(BytesPerSecond));
        }

        public static string GetHumanReadableSpeed(double bytesPerSecond)
        {
            if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond) || bytesPerSecond < 0)
                bytesPerSecond = 0d;

            return GetHumanReadableSize(bytesPerSecond) + "/sec";
        }

        public static string GetHumanReadableSize(double bytes)
        {
            const double KB = 1024d;
            const double MB = 1024d * 1024d;

            double value;
            string unit;

            if (bytes > MB)
            {
                value = Math.Round(bytes / MB, 2, MidpointRounding.AwayFromZero);
                unit = "MB";
            }
            else if (bytes > KB)
            {
                value = Math.Round(bytes / KB, 0, MidpointRounding.AwayFromZero);
                unit = "KB";
            }
            else
            {
                value = Math.Round(bytes, 0, MidpointRounding.AwayFromZero);
                unit = "Bytes";
            }

            return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + unit;
        }
    }
}
