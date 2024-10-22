using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MultiImageClient
{
    public enum GenericTextGenerationErrorType
    {
        RequestModerated = 1,
        ContentModerated = 2,
        NoMoneyLeft = 3,
        Unknown = 4, //refine this if it happens; this 
    }
}
