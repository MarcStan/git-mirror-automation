using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Storage
{
    public class AzureBlobStorageProvider : IStorageProvider
    {
        private readonly CloudBlobClient _blobClient;
        private readonly string _container;

        public AzureBlobStorageProvider(
            string connectionString,
            string container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            var storageClient = CloudStorageAccount.Parse(connectionString ?? throw new ArgumentNullException(nameof(connectionString)));
            _blobClient = storageClient.CreateCloudBlobClient();
        }

        public string Escape(string fileName)
            => fileName;

        public async Task<bool> ExistsAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            var block = await GetBlockBlobAsync(fileName, false, cancellationToken);
            return await block.ExistsAsync(null, null, cancellationToken);
        }

        public async Task<string[]> ListAsync(string prefix, CancellationToken cancellationToken)
        {
            var container = await GetContainerAsync(true, cancellationToken);

            var list = new List<string>();
            BlobContinuationToken? token = null;
            do
            {
                var r = await container.ListBlobsSegmentedAsync(prefix, token);
                list.AddRange(r.Results.Select(b => b.Uri.GetLeftPart(UriPartial.Path).Substring(container.Uri.ToString().Length + 1)));
                token = r.ContinuationToken;
            }
            while (token != null);

            return list.ToArray();
        }

        public async Task<string> GetAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, false, cancellationToken);
            return await blob.DownloadTextAsync(Encoding.UTF8, null, null, null, cancellationToken);
        }

        public async Task SetAsync(
            string fileName,
            string content,
            CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, true, cancellationToken);
            await blob.UploadTextAsync(content, null, null, null, null, cancellationToken);
        }

        public async Task DeleteAsync(string fileName, CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, false, cancellationToken);
            await blob.DeleteIfExistsAsync();
        }

        private async Task<CloudBlobContainer> GetContainerAsync(
            bool createContainerIfNotExists,
            CancellationToken cancellationToken)
        {
            var container = _blobClient.GetContainerReference(_container);
            if (createContainerIfNotExists)
                await container.CreateIfNotExistsAsync(BlobContainerPublicAccessType.Off, null, null, cancellationToken);

            return container;
        }

        private async Task<CloudBlockBlob> GetBlockBlobAsync(
            string filePath,
            bool createContainerIfNotExists,
            CancellationToken cancellationToken)
        {
            var container = await GetContainerAsync(createContainerIfNotExists, cancellationToken);

            return container.GetBlockBlobReference(filePath);
        }
    }
}
