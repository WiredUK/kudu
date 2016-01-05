using Kudu.Contracts.Jobs;
using Kudu.Contracts.Settings;
using Kudu.Contracts.Tracing;
using Kudu.Core;
using Kudu.Core.Infrastructure;
using Kudu.Core.Jobs;
using Kudu.Core.Tracing;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Web.Http;

namespace Kudu.Services.Jobs
{
    public class FunctionsController : ApiController
    {
        private readonly ITracer _tracer;
        private readonly IEnvironment _environment;
        private readonly IDeploymentSettingsManager _settings;
        private const int BufferSize = 32 * 1024;
        private readonly static string WebJobScriptHostPath = Path.Combine(Path.GetTempPath(), "WebJobs.Script.Host", "WebJobs.Script.Host.exe");

        static FunctionsController()
        {
            if (!File.Exists(WebJobScriptHostPath))
            {
                try
                {
                    EnsureScriptHostOnDisk(Path.GetDirectoryName(WebJobScriptHostPath));
                }
                catch (Exception e)
                {
                    using (var writer = new StreamWriter("D:\\home\\site\\downloadWebJobHost.log"))
                    {
                        writer.WriteLine(e);
                    }
                }
            }
        }

        public FunctionsController(ITracer tracer, IEnvironment environment, IDeploymentSettingsManager settings)
        {
            _tracer = tracer;
            _environment = environment;
            _settings = settings;
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

        [HttpPost]
        public async Task<HttpResponseMessage> Run(string name)
        {
            using (_tracer.Step($"FunctionsController.Run({name})"))
            {
                var storageHelper = new WebJobStorageHelper(_settings);
                var functionConfig = await GetFunctionConfig(name);
                var hostConfig = await GetHostConfig();
                var queueName = $"azure-webjobs-host-{hostConfig["id"]}";
                var queueMessage = new WebJobScriptHostInvokeMessage(functionConfig.Name);

                var inputName = functionConfig.Config["bindings"]?["input"]?.FirstOrDefault()?["name"]?.ToString() ?? "input";
                queueMessage.Arguments[inputName] = await Request.Content.ReadAsStringAsync();

                await storageHelper.AddQueueMessage(queueName, queueMessage);
                return Request.CreateResponse(HttpStatusCode.Created, new { id = queueMessage.Id });
            }
        }

        [HttpGet]
        public async Task<HttpResponseMessage> GetRunStatus(string name, string id)
        {
            using (_tracer.Step($"FunctionsController.GetRunStatus({id})"))
            {
                var functionConfig = await GetFunctionConfig(name);
                var hostConfig = await GetHostConfig();
                var blob = $"invocations/{hostConfig["id"]}/Host.Functions.{functionConfig.Name}/{id}";
                var storageHelper = new WebJobStorageHelper(_settings);

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(await storageHelper.GetBlobContent("azure-webjobs-hosts", blob))
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                return response;
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
        public async Task<HttpResponseMessage> GetFunctionsTemplates()
        {
            using (_tracer.Step("FunctionsController.GetFunctionsTemplates()"))
            {
                return Request.CreateResponse(HttpStatusCode.OK, await GetTemplates());
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
                ScriptRootPathHref = FilePathToVfsUri(GetFunctionPath(functionName)),
                ScriptHref = FilePathToVfsUri(GetFunctionScriptPath(functionName, config)),
                ConfigHref = FilePathToVfsUri(Path.Combine(GetFunctionPath(functionName), Constants.FunctionsConfigFile)),
                TestDataHref = FilePathToVfsUri(GetFunctionSampleDataFile(functionName)),
                Href = GetFunctionHref(functionName),
                Config = config
            };
        }

        private string GetFunctionSampleDataFile(string functionName)
        {
            return Path.Combine(_environment.JobsDataPath, Constants.Functions, functionName, Constants.SampleFunctionData);
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

        public Uri FilePathToVfsUri(string filePath)
        {
            var baseUrl = Request.RequestUri.GetLeftPart(UriPartial.Authority);
            filePath = filePath.Substring(_environment.RootPath.Length).Trim('\\').Replace("\\", "/");
            return new Uri($"{baseUrl}/api/vfs/{filePath}");
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

        public static async Task<IEnumerable<GitHubContent>> GetTemplates()
        {
            using (var client = GetHttpClient())
            {
                var response = await client.GetAsync("https://api.github.com/repos/Azure/azure-webjobs-sdk-script/contents/sample");
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

        private static void EnsureScriptHostOnDisk(string path)
        {
            using (var client = new WebClient())
            {
                Directory.CreateDirectory(path);
                var downloadArchivePath = Path.Combine(path, "WebJobs.Script.Host.zip");
                client.DownloadFile("https://github.com/Azure/azure-webjobs-sdk-script/releases/download/v1.0.0-alpha1-10021/WebJobs.Script.Host.zip", downloadArchivePath);
                using (var archive = new ZipArchive(new FileStream(downloadArchivePath, FileMode.Open)))
                {
                    archive.Extract(path);
                }
            }
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
