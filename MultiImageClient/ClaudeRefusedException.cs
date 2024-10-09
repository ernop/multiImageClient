using System;

namespace MultiClientRunner
{
    public class ClaudeRefusedException : Exception
    {
        public ClaudeRefusedException(string message) : base(message)
        {
        }
    }
}