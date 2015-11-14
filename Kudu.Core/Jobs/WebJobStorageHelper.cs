using Kudu.Contracts.Settings;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Queue;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kudu.Core.Jobs
{
    public class WebJobStorageHelper
    {
        private readonly IDeploymentSettingsManager _settings;
        private readonly CloudStorageAccount _storageAccount;
        private readonly CloudQueueClient _queueClient;
        private readonly CloudBlobClient _blobClient;

        public WebJobStorageHelper(IDeploymentSettingsManager settings)
        {
            _settings = settings;
            _settings.GetCommandIdleTimeout();
            _storageAccount = CloudStorageAccount.Parse(_settings.GetAzureWebJobsStorageConnectionString());
            _queueClient = _storageAccount.CreateCloudQueueClient();
            _blobClient = _storageAccount.CreateCloudBlobClient();
        }

        public async Task AddQueueMessage(string queueName, object message)
        {
            var queue = _queueClient.GetQueueReference(queueName);
            await queue.CreateIfNotExistsAsync();
            await queue.AddMessageAsync(new CloudQueueMessage(JsonConvert.SerializeObject(message)));
        }

        public async Task<string> GetBlobContent(string containerName, string blobName)
        {
            var container = _blobClient.GetContainerReference(containerName);
            var blob = container.GetBlobReference(blobName);
            using (var stream = new MemoryStream())
            {
                await blob.DownloadToStreamAsync(stream);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }
}