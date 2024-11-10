using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface IImageGenerator
    {
        Task<TaskProcessResult> ProcessPromptAsync(PromptDetails pd, MultiClientRunStats stats);
    }
}
