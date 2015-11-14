using System.Collections.Generic;
using System.IO;

namespace Kudu.Core.Jobs
{
    public class FunctionsScriptHost : ScriptHostBase
    {
        private static readonly string[] Supported = { Constants.FunctionsHostConfigFile };

        public FunctionsScriptHost()
            // TODO change to the final place of the script host.
            : base(Path.Combine(Path.GetTempPath(), "WebJobs.Script.Host", "WebJobs.Script.Host.exe"))
        {
        }

        public override IEnumerable<string> SupportedFileNames
        {
            get { return Supported; }
        }
    }
}