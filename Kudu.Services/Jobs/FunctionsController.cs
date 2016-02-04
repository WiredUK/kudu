using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Infrastructure;
using Kudu.Core.Tracing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;

namespace Kudu.Services.Jobs
{
    public class FunctionsController : ApiController
    {
        private readonly ITracer _tracer;
        private readonly IEnvironment _environment;

        public FunctionsController(ITracer tracer, IEnvironment environment)
        {
            _tracer = tracer;
            _environment = environment;
        }

        [HttpPut]
        public async Task<HttpResponseMessage> CreateOrUpdate(string name)
        {
            using (_tracer.Step($"FunctionsController.CreateOrUpdate({name})"))
            {
                var functionEnvelope = await Request.Content.ReadAsAsync<FunctionEnvelope>();
                var functionDir = Path.Combine(_environment.FunctionsPath, name);

                // Assert templateId and value are both present
                if (!string.IsNullOrEmpty(functionEnvelope?.TemplateId))
                {
                    // check to create template.
                    if (FileSystemHelpers.DirectoryExists(functionDir) &&
                        FileSystemHelpers.GetFileSystemEntries(functionDir).Any())
                    {
                        return Request.CreateResponse(HttpStatusCode.Conflict, $"Function {name} already exist");
                    }

                    var template = (await GetTemplates()).FirstOrDefault(e => e.name.Equals(functionEnvelope.TemplateId, StringComparison.OrdinalIgnoreCase));
                    if (template != null)
                    {
                        await DeployTemplateFromGithub(template, functionDir);
                    }
                    else
                    {
                        return Request.CreateResponse(HttpStatusCode.NotFound, $"template: {functionEnvelope.TemplateId} was not found");
                    }
                }
                else
                {
                    // Create or update fuckin function
                    if (!FileSystemHelpers.DirectoryExists(functionDir))
                    {
                        // create a new function
                        FileSystemHelpers.EnsureDirectory(functionDir);
                    }
                    await FileSystemHelpers.WriteAllTextToFileAsync(Path.Combine(functionDir, Constants.FunctionsConfigFile), JsonConvert.SerializeObject(functionEnvelope?.Config ?? new JObject()));
                }

                return Request.CreateResponse(HttpStatusCode.Created, await GetFunctionConfig(name));
            }
        }

        [HttpGet]
        public async Task<HttpResponseMessage> List()
        {
            using (_tracer.Step("FunctionsController.list()"))
            {
                var configList = await Task.WhenAll(
                    FileSystemHelpers
                    .GetDirectories(_environment.FunctionsPath)
                    .Select(d => Path.Combine(d, Constants.FunctionsConfigFile))
                    .Where(FileSystemHelpers.FileExists)
                    .Select(async f => { try { return await GetFunctionConfig(Path.GetFileName(Path.GetDirectoryName(f))); } catch { return null; } }));

                return Request.CreateResponse(HttpStatusCode.OK, configList.Where(c => c != null ));
            }
        }

        [HttpGet]
        public async Task<HttpResponseMessage> Get(string name)
        {
            using (_tracer.Step($"FunctionsController.Get({name})"))
            {
                return Request.CreateResponse(HttpStatusCode.OK, await GetFunctionConfig(name));
            }
        }

        [HttpDelete]
        public HttpResponseMessage Delete(string name)
        {
            using (_tracer.Step($"FunctionsController.Delete({name})"))
            {
                var path = GetFunctionPath(name);
                FileSystemHelpers.DeleteDirectorySafe(path, ignoreErrors: false);
                return Request.CreateResponse(HttpStatusCode.NoContent);
            }
        }

        [HttpGet]
        public async Task<HttpResponseMessage> GetHostSettings()
        {
            using (_tracer.Step("FunctionsController.GetHostSettings()"))
            {
                return Request.CreateResponse(HttpStatusCode.OK, await GetHostConfig());
            }
        }

        [HttpPut]
        public async Task<HttpResponseMessage> PutHostSettings()
        {
            using (_tracer.Step("FunctionsController.PutHostSettings()"))
            {
                var path = Path.Combine(_environment.FunctionsPath, Constants.FunctionsHostConfigFile);
                var content = JsonConvert.SerializeObject(await Request.Content.ReadAsAsync<JObject>());
                await FileSystemHelpers.WriteAllTextToFileAsync(path, content);
                return Request.CreateResponse(HttpStatusCode.Created);
            }
        }

        [HttpGet]
        public HttpResponseMessage GetFunctionsTemplates()
        {
            using (_tracer.Step("FunctionsController.GetFunctionsTemplates()"))
            {
                return Request.CreateResponse(HttpStatusCode.OK, GetMockTemplates());
            }
        }

        public async Task<FunctionEnvelope> GetFunctionConfig(string name)
        {
            var path = Path.Combine(GetFunctionPath(name), Constants.FunctionsConfigFile);
            if (FileSystemHelpers.FileExists(path))
            {
                return CreateFunctionConfig(await FileSystemHelpers.ReadAllTextFromFileAsync(path), name);
            }

            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        public async Task<JObject> GetHostConfig()
        {
            var path = Path.Combine(_environment.FunctionsPath, Constants.FunctionsHostConfigFile);
            if (FileSystemHelpers.FileExists(path))
            {
                return JObject.Parse(await FileSystemHelpers.ReadAllTextFromFileAsync(path));
            }

            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        public FunctionEnvelope CreateFunctionConfig(string configContent, string functionName)
        {
            var config = JObject.Parse(configContent);
            return new FunctionEnvelope
            {
                Name = functionName,
                ScriptRootPathHref = FilePathToVfsUri(GetFunctionPath(functionName), isDirectory: true),
                ScriptHref = FilePathToVfsUri(GetFunctionScriptPath(functionName, config)),
                ConfigHref = FilePathToVfsUri(Path.Combine(GetFunctionPath(functionName), Constants.FunctionsConfigFile)),
                TestDataHref = FilePathToVfsUri(GetFunctionSampleDataFile(functionName)),
                SecretsFileHref = FilePathToVfsUri(GetFunctionSecretsFile(functionName)),
                Href = GetFunctionHref(functionName),
                Config = config
            };
        }

        private string GetFunctionSampleDataFile(string functionName)
        {
            return Path.Combine(_environment.DataPath, Constants.Functions, Constants.SampleData, $"{functionName}.dat");
        }

        private string GetFunctionSecretsFile(string functionName)
        {
            return Path.Combine(_environment.DataPath, Constants.Functions, Constants.Secrets, $"{functionName}.json");
        }

        // Logic for this function is copied from here
        // https://github.com/Azure/azure-webjobs-sdk-script/blob/e0a783e882dd8680bf23e3c8818fb9638071c21d/src/WebJobs.Script/Config/ScriptHost.cs#L113-L150
        private string GetFunctionScriptPath(string functionName, JObject functionInfo)
        {
            var functionPath = GetFunctionPath(functionName);
            var functionFiles = FileSystemHelpers.GetFiles(functionPath, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p => Path.GetFileName(p).ToLowerInvariant() != "function.json").ToArray();

            if (functionFiles.Length == 0)
            {
                return functionPath;
            }
            else if (functionFiles.Length == 1)
            {
                // if there is only a single file, that file is primary
                return functionFiles[0];
            }
            else
            {
                // if there is a "run" file, that file is primary
                string functionPrimary = null;
                functionPrimary = functionFiles.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).ToLowerInvariant() == "run");
                if (string.IsNullOrEmpty(functionPrimary))
                {
                    // for Node, any index.js file is primary
                    functionPrimary = functionFiles.FirstOrDefault(p => Path.GetFileName(p).ToLowerInvariant() == "index.js");
                    if (string.IsNullOrEmpty(functionPrimary))
                    {
                        // finally, if there is an explicit primary file indicated
                        // in config, use it
                        JToken token = functionInfo["source"];
                        if (token != null)
                        {
                            string sourceFileName = (string)token;
                            functionPrimary = Path.Combine(functionPath, sourceFileName);
                        }
                    }
                }

                if (string.IsNullOrEmpty(functionPrimary))
                {
                    // TODO: should this be an error?
                    return functionPath;
                }
                return functionPrimary;
            }
        }

        public Uri FilePathToVfsUri(string filePath, bool isDirectory = false)
        {
            var baseUrl = Request.RequestUri.GetLeftPart(UriPartial.Authority);
            filePath = filePath.Substring(_environment.RootPath.Length).Trim('\\').Replace("\\", "/");
            return new Uri($"{baseUrl}/api/vfs/{filePath}{(isDirectory ? "/" : string.Empty)}");
        }

        public Uri GetFunctionHref(string functionName)
        {
            var baseUrl = Request.RequestUri.GetLeftPart(UriPartial.Authority).Trim('/');
            return new Uri($"{baseUrl}/api/functions/{functionName}");
        }

        public string GetFunctionPath(string name)
        {
            var path = Path.Combine(_environment.FunctionsPath, name);
            if (FileSystemHelpers.DirectoryExists(path))
            {
                return path;
            }

            throw new HttpResponseException(HttpStatusCode.NotFound);
        }

        public static IEnumerable<FunctionTemplate> GetMockTemplates()
        {
            return new[]
            {
                new FunctionTemplate { Id = "HttpTrigger", Language = "JavaScript", Trigger = "Http" },
                new FunctionTemplate { Id = "HttpTrigger-Batch", Language = "Batch", Trigger = "Http" },
                new FunctionTemplate { Id = "BlobTrigger", Language = "JavaScript", Trigger = "Blob" },
                new FunctionTemplate { Id = "QueueTrigger-Bash", Language = "Bash", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger-Batch", Language = "Batch", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger-FSharp", Language = "F#", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger-Php", Language = "Php", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger-Powershell", Language = "PowerShell", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger-Python", Language = "Python", Trigger = "Queue" },
                new FunctionTemplate { Id = "QueueTrigger", Language = "JavaScript", Trigger = "Queue" },
                new FunctionTemplate { Id = "ResizeImage", Language = "exe", Trigger = "Queue" },
                new FunctionTemplate { Id = "ServiceBusQueueTrigger", Language = "JavaScript", Trigger = "ServiceBus" },
                new FunctionTemplate { Id = "TimerTrigger", Language = "JavaScript", Trigger = "Timer" },
                new FunctionTemplate { Id = "WebHook-Generic", Language = "JavaScript", Trigger = "WebHook-Generic" },
                new FunctionTemplate { Id = "WebHook-GitHub", Language = "JavaScript", Trigger = "WebHook-GitHub" }
            };
        }

        public static async Task<IEnumerable<GitHubContent>> GetTemplates()
        {
            using (var client = GetHttpClient())
            {
                var response = await client.GetAsync("https://api.github.com/repos/Azure/azure-webjobs-sdk-script/contents/sample?ref=0fc45ab7b5168588fe40955c12033a2d0ae3c8e0");
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsAsync<IEnumerable<GitHubContent>>();
                return content.Where(s => s.type.Equals("dir", StringComparison.OrdinalIgnoreCase));
            }
        }

        private static HttpClient GetHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent", "Kudu/Api");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
            return client;
        }

        private async static Task DeployTemplateFromGithub(GitHubContent template, string functionDir)
        {
            //deploy template from github
            FileSystemHelpers.EnsureDirectory(functionDir);
            using (var webClient = new WebClient())
            using (var httpClient = GetHttpClient())
            {
                var response = await httpClient.GetAsync(template.url);
                response.EnsureSuccessStatusCode();
                var files = await response.Content.ReadAsAsync<IEnumerable<GitHubContent>>();
                foreach (var file in files.Where(s => s.type.Equals("file", StringComparison.OrdinalIgnoreCase)))
                {
                    await webClient.DownloadFileTaskAsync(new Uri(file.download_url), Path.Combine(functionDir, file.name));
                }
            }
        }
    }

    public class GitHubContent
    {
        public string name { get; set; }
        public string path { get; set; }
        public string sha { get; set; }
        public int size { get; set; }
        public string url { get; set; }
        public string html_url { get; set; }
        public string git_url { get; set; }
        public string download_url { get; set; }
        public string type { get; set; }
    }
}
