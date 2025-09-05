using log4net;
using System;

namespace TestLoggingExtensions
{
    class Program
    {
        private static ILog Log = LogManager.GetLogger("Test");
        static void Main(string[] args)
        {
            for (int i = 0; i < 100; i++)
            {
                Log.Info("Repeating message");
            }
            Log.Info("A new message");
            for (int i = 0; i < 100; i++)
            {
                Log.Info("Another Repeating message");
            }
            Console.ReadKey();
        }
    }
}
