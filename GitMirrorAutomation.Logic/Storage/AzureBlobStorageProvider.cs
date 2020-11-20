using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitMirrorAutomation.Logic.Storage
{
    public class AzureBlobStorageProvider : IStorageProvider
    {
        private BlobContainerClient _blobContainerClient;

        public AzureBlobStorageProvider(
            string connectionString,
            string container)
        {
            _blobContainerClient = new BlobContainerClient(connectionString, container);
        }

        public AzureBlobStorageProvider(TokenCredential msiCredentials, string account, string container)
        {
            _blobContainerClient = new BlobContainerClient(new Uri($"https://{account}.blob.core.windows.net/{container}"), msiCredentials);
        }

        public string Escape(string fileName)
            => fileName;

        public async Task<bool> ExistsAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            var block = await GetBlockBlobAsync(fileName, false, cancellationToken);
            return await block.ExistsAsync(cancellationToken);
        }

        public async Task<string[]> ListAsync(string prefix, CancellationToken cancellationToken)
        {
            var container = await GetContainerAsync(true, cancellationToken);

            var list = new List<string>();
            await foreach (var file in container.GetBlobsAsync(prefix: prefix, cancellationToken: cancellationToken))
            {
                list.Add(file.Name);
            }

            return list.ToArray();
        }

        public async Task<string> GetAsync(
            string fileName,
            CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, false, cancellationToken);
            var response = await blob.DownloadAsync(cancellationToken: cancellationToken);
            using (var streamReader = new StreamReader(response.Value.Content))
                return await streamReader.ReadToEndAsync();
        }

        public async Task SetAsync(
            string fileName,
            string content,
            CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, true, cancellationToken);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                await blob.UploadAsync(ms, new BlobUploadOptions(), cancellationToken);
        }

        public async Task DeleteAsync(string fileName, CancellationToken cancellationToken)
        {
            var blob = await GetBlockBlobAsync(fileName, false, cancellationToken);
            await blob.DeleteIfExistsAsync();
        }

        private async Task<BlobContainerClient> GetContainerAsync(
            bool createContainerIfNotExists,
            CancellationToken cancellationToken)
        {
            if (createContainerIfNotExists)
                await _blobContainerClient.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);

            return _blobContainerClient;
        }

        private async Task<BlockBlobClient> GetBlockBlobAsync(
            string filePath,
            bool createContainerIfNotExists,
            CancellationToken cancellationToken)
        {
            var container = await GetContainerAsync(createContainerIfNotExists, cancellationToken);

            return container.GetBlockBlobClient(filePath);
        }
    }
}
