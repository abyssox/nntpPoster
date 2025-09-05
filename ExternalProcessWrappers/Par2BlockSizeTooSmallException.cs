using System;

namespace ExternalProcessWrappers
{
    [Serializable]
    public class Par2BlockSizeTooSmallException : Exception
    {
        public Par2BlockSizeTooSmallException() : base() { }

        public Par2BlockSizeTooSmallException(String message) : base(message) { }

        public Par2BlockSizeTooSmallException(String message, Exception innerException) : base(message, innerException) { }
    }
}
