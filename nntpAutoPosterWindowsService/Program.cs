using System.ServiceProcess;

namespace nntpAutoPosterWindowsService
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the service application.
        /// </summary>
        static void Main()
        {
            ServiceBase.Run(new ServiceBase[]
            {
                new Service()
            });
        }
    }
}
