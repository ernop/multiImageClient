using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    /// <summary>
    /// you can configure and create it (with a _service).
    /// so say you want a "landscape anime" image maker based on bfl and another like square and another depending on what claude thinks. you just make one generator for each then as you get your prompts, you send it in.
    /// you can make them all at first or as you go. The point is that we preconfigure everything?
    /// a thing you can send a prompt to, and get back a TaskProcessResult
    /// 
    /// so, they should have already been created with the options set how you'd like them to be sent out.
    /// </summary>
    public interface IImageGenerator
    {
        public abstract Task<TaskProcessResult> ProcessPromptAsync(PromptDetails promptDetails);

        /// when this jobspec is run, how should it be mapped to the filename?
        public abstract string GetFilenamePart(PromptDetails pd);

        /// return just the parts on the right, we know everything else.
        public abstract List<string> GetRightParts();
    }
}
