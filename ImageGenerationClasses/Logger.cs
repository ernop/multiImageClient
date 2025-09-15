using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public static class Logger
    {
        private static string _logFilePath = "log.txt";
        private static StreamWriter _logWriter = new StreamWriter(_logFilePath, true);

        public static void Log(string message)
        {
            Console.WriteLine(message);
            _logWriter.WriteLine(message);
        }
    }
}
