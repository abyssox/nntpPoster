using ExternalProcessWrappers;
using System.IO;

namespace rarTester
{
    class Program
    {
        static void Main(string[] args)
        {
            var source = new DirectoryInfo(@"/mnt/virtual/shareroot/ToSort/TestRarring/SourceFolder/");
            var target = new DirectoryInfo(@"/mnt/virtual/shareroot/ToSort/TestRarring/TargetFolder/");
            var targetName = "RarFilename";

            var rarWrapper = new RarWrapper(5);

            rarWrapper.Compress(source, target, targetName, 15 * 1000 * 1000, null, null);

#if DEBUG       //VS does not halt after execution in debug mode.
            Console.WriteLine("Finished");
            Console.ReadKey();
#endif
        }
    }
}
