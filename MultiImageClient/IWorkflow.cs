using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public interface IWorkflow
    {
        Task RunAsync();
    }
}
