using System;
using System.Security.Cryptography;
using System.Text;

namespace Util
{
    public static class RandomStringGenerator
    {
        private const string Alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890_-";
        
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        static RandomStringGenerator()
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Rng.Dispose(); } catch { } };
        }

        public static string GetRandomString(int length)
        {
            if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));

            var data = new byte[length];
            Rng.GetBytes(data);

            var sb = new StringBuilder(length);
            foreach (var b in data)
            {
                sb.Append(Alphabet[b & 63]);
            }

            return sb.ToString();
        }

        public static string GetRandomString(int minLength, int maxLength)
        {
            if (minLength < 0) throw new ArgumentOutOfRangeException(nameof(minLength));
            if (maxLength <= minLength) throw new ArgumentOutOfRangeException(nameof(maxLength));
            int length = minLength + GetRandomInt(maxLength - minLength);

            return GetRandomString(length);
        }

        private static int GetRandomInt(int maxExclusive)
        {
            if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));

            var buffer = new byte[4];
            uint limit = (uint.MaxValue / (uint)maxExclusive) * (uint)maxExclusive;
            uint value;

            do
            {
                Rng.GetBytes(buffer);
                value = BitConverter.ToUInt32(buffer, 0);
            } while (value >= limit);

            return (int)(value % (uint)maxExclusive);
        }
    }
}
