using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kudu.Contracts.Jobs
{
    public class WebJobScriptHostInvokeMessage
    {
        public string Type { get; } = "CallAndOverride";
        public Guid Id { get; } = Guid.NewGuid();
        public string FunctionId { get; private set; }
        public Dictionary<string, string> Arguments { get; } = new Dictionary<string, string>();
        public string Reason { get; } = "Portal";

        public WebJobScriptHostInvokeMessage(string functionName)
        {
            FunctionId = $"Host.Functions.{functionName}";
        }
    }
}
