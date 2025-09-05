using log4net;

namespace VideoFileRenamer
{
    class Renamer
    {
        private static readonly ILog log = LogManager.GetLogger(
            System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private RenamerConfiguration configuration;


        public Renamer(RenamerConfiguration configuration)
        {
            this.configuration = configuration;
        }
    }
}
