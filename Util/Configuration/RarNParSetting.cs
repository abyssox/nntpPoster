using System;
using System.Runtime.Serialization;

namespace Util.Configuration
{
    [DataContract(Namespace = "Util.Configuration")]
    public class RarNParSetting
    {
        [DataMember(Order = 0)]
        public Int32 FromSize { get; set; }

        public Int64 FromSizeBytes
        {
            get { return (Int64)FromSize * 1024 * 1024; }
        }

        [DataMember(Order = 1)]
        public Int32 RarSize { get; set; }

        [DataMember(Order = 2)]
        public Int32 Par2Percentage { get; set; }
    }
}
