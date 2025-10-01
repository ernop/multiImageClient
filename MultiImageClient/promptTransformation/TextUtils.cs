using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Reflection;
using System.Timers;
using System.Text;
using System.Windows.Forms;

using IdeogramAPIClient;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using CommandLine;


namespace MultiImageClient
{
    public static class TextUtils
    {
        private static readonly object _lockObject = new object();
        private static readonly Dictionary<string, int> _filenameCounts = new Dictionary<string, int>();
        private static readonly float KEY_WIDTH_PROPORTION = 0.15f;
        private static readonly float VALUE_WIDTH_PROPORTION = 1f - KEY_WIDTH_PROPORTION;
        public static List<string> bads = new List<string>() { };// "[", "]", "(", ")", "{", "}","\\","\"","\'", ":", };
        //don't clean these here; if someone needs the prompt to be clean, then just do that yourself later. it's not the prompts problem.
        
        public static string CleanPrompt(string prompt)
        {
            foreach (var bad in bads)
            {
                prompt = prompt.Replace(bad, "");
            }
            return prompt.Trim();
        }

       
    }
}
