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
        public abstract ImageGeneratorApiType ApiType { get; }
        public abstract string GetFilenamePart(PromptDetails pd);

        /// return just the parts on the right, we know everything else.
        public abstract List<string> GetRightParts();

        // This is yet another type of description of the generator. Not the filename, not the full details. And not the name... but why not? yes name if provided does supercede.  So if no name, default to something senseible to describe API/company/remote endpoint style + meaningful config options
        public abstract string GetGeneratorSpecPart();
        
        /// I suppose we should tell/show users how much images cost.
        public abstract decimal GetCost();

        public abstract Task<TaskProcessResult> ProcessPromptAsync(IImageGenerator generator, PromptDetails promptDetails);

        /// when this jobspec is run, how should it be mapped to the filename?
    }
}

