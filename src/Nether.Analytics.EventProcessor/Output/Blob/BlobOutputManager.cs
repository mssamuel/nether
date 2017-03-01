// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System.Diagnostics;

namespace Nether.Analytics.EventProcessor.Output.Blob
{
    public class BlobOutputManager
    {
        private ConcurrentDictionary<string, ConcurrentQueue<string>> _writeQueues = new ConcurrentDictionary<string, ConcurrentQueue<string>>();

        private ConcurrentDictionary<string, CloudAppendBlob> _tmpBlobs = new ConcurrentDictionary<string, CloudAppendBlob>();

        private const string BlobNameFormat = "D6";
        private readonly Dictionary<string, string> _tmpBlobNameCache = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _currentFolders = new Dictionary<string, string>();
        private readonly long _maxBlobSize;
        private readonly string _storageAccountConnectionString;
        private readonly string _webJobDashboardAndStorageConnectionString;
        private CloudBlobContainer _tmpContainer;
        private CloudBlobContainer _outputContainer;
        private readonly string _outputContainerName;
        private readonly string _tmpContainerName;

        public BlobOutputManager(string storageAccountConnectionString, string webJobDashboardAndStorageConnectionString, string tmpContainerName, string outputContainerName, long maxBlobBlobSize)
        {
            _storageAccountConnectionString = storageAccountConnectionString;
            _webJobDashboardAndStorageConnectionString = webJobDashboardAndStorageConnectionString;
            _maxBlobSize = maxBlobBlobSize;

            _tmpContainerName = tmpContainerName;
            _outputContainerName = outputContainerName;
        }

        public async Task SetupAsync()
        {
            var cloudStorageAccount = CloudStorageAccount.Parse(_storageAccountConnectionString);
            var cloudBlobClient = cloudStorageAccount.CreateCloudBlobClient();

            _tmpContainer = cloudBlobClient.GetContainerReference(_tmpContainerName);
            _outputContainer = cloudBlobClient.GetContainerReference(_outputContainerName);

            var createTmpContainerTask = _tmpContainer.CreateIfNotExistsAsync();
            var createOutputContainerTask = _outputContainer.CreateIfNotExistsAsync();

            if (await createTmpContainerTask)
                Console.WriteLine($"Container {_tmpContainer.Name} created");
            if (await createOutputContainerTask)
                Console.WriteLine($"Container {_outputContainer.Name} created");
        }

        public Task QueueAppendToBlobAsync(GameEventData data, string line)
        {
            var queue = GetQueueForGameEvent(data);
            queue.Enqueue(line);
            return Task.CompletedTask;
        }

        private ConcurrentQueue<string> GetQueueForGameEvent(GameEventData data)
        {
            var writeToFolderName = GetTmpFolderForGameEvent(data.VersionedType, data.EnqueuedTime);

            if (_writeQueues.TryGetValue(writeToFolderName, out ConcurrentQueue < string > queue))
                return queue;

            queue = new ConcurrentQueue<string>();
            _writeQueues[writeToFolderName] = queue;

            return queue;
        }

        private string GetTmpFolderForGameEvent(string versionedType, DateTime enqueueTime)
        {
            return $"{versionedType}/{enqueueTime.Year:D4}/{enqueueTime.Month:D2}/{enqueueTime.Day:D2}/";
        }

        public async Task FlushWriteQueuesAsync()
        {
            ConcurrentDictionary<string, ConcurrentQueue<string>> writeQueuesToFlush;

            lock (_writeQueues)
            {
                if (_writeQueues.Count == 0)
                    return;

                writeQueuesToFlush = _writeQueues;
                _writeQueues = new ConcurrentDictionary<string, ConcurrentQueue<string>>();
            }

            var tasks = writeQueuesToFlush.Select(async q =>
            {
                var folder = q.Key;
                var queue = q.Value;

                await FlushQueueAsync(folder, queue);
            });

            await Task.WhenAll(tasks);
        }

        private async Task FlushQueueAsync(string folder, ConcurrentQueue<string> queue)
        {
            var lines = new List<string>();

            while (queue.TryDequeue(out string line))
            {
                lines.Add(line);
            }

            // Only flush queues that had messages
            if (lines.Count > 0)
            {
                await AppendToFolderAsync(folder, lines.ToArray());
            }
        }

        private async Task AppendToFolderAsync(string folder, params string[] lines)
        {
            Console.WriteLine(folder);
            foreach (var line in lines)
            {
                Console.WriteLine($"  {line}");
            }

            var blob = GetTmpAppendBlob(folder);

            var data = string.Join("\n", lines) + "\n";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(data));

            while (true)
            {
                stream.Position = 0;
                if (AppendToBlob(blob, stream))
                    break;

                // Blob reached max size
                Console.WriteLine($"Blob {blob.Name} has reached max size of {_maxBlobSize}B");
                await HandleFullBlobAsync(blob);

                blob = GetNewTmpAppendBlob(folder);
                Console.WriteLine($"Rolling over to new blob {blob.Name}");
            }
        }

        private async Task HandleFullBlobAsync(CloudAppendBlob fullBlob)
        {
            // update blob metadata
            await FlagsAsFullAsync(fullBlob);
        }

        private async Task FlagsAsFullAsync(CloudAppendBlob fullBlob)
        {
            fullBlob.FetchAttributes();
            fullBlob.Metadata["full"] = DateTime.Now.ToString();
            await fullBlob.SetMetadataAsync();
        }

        private bool AppendToBlob(CloudAppendBlob blob, MemoryStream stream)
        {
            try
            {
                if (!blob.Exists()) return false;

                blob.AppendBlock(stream,
                    accessCondition: AccessCondition.GenerateIfMaxSizeLessThanOrEqualCondition(_maxBlobSize));
            }
            catch (StorageException ex)
            {
                if (ex.RequestInformation.HttpStatusCode == 412) // HttpStatusCode.PreconditionFailed
                {
                    return false;
                }
                else
                {
                    throw;
                }
            }

            return true;
        }

        private CloudAppendBlob GetTmpAppendBlob(string folderName)
        {
            // Look for cached blob names
            if (_tmpBlobNameCache.TryGetValue(folderName, out string cachedBlobName))
                return _tmpContainer.GetAppendBlobReference(cachedBlobName);

            // No cached name was found, look for last blobname in folder
            var lastBlobNameInFolder = GetLastBlobNameInFolder(_tmpContainer, folderName);
            if (!string.IsNullOrEmpty(lastBlobNameInFolder))
            {
                // Use the cached blob name to create a reference and return
                var lastFullBlobName = $"{folderName}{lastBlobNameInFolder}.csv";
                _tmpBlobNameCache[folderName] = lastFullBlobName;
                return _tmpContainer.GetAppendBlobReference(lastFullBlobName);
            }

            // No blob exist in folder, create new and return
            var newBlobName = $"{0:D6}";
            var newFullBlobName = $"{folderName}{newBlobName}.csv";
            _tmpBlobNameCache[folderName] = newFullBlobName;

            // Create blob and return the reference
            var blob = _tmpContainer.GetAppendBlobReference(newFullBlobName);
            CreateBlob(blob);
            return blob;
        }

        private CloudAppendBlob GetNewTmpAppendBlob(string folderName)
        {
            var lastBlobNameInFolder = GetLastBlobNameInFolder(_tmpContainer, folderName);
            var newBlobName = GetNextBlobName(lastBlobNameInFolder);
            var fullBlobName = $"{folderName}{newBlobName}.csv";
            _tmpBlobNameCache[folderName] = fullBlobName;

            var newBlob = _tmpContainer.GetAppendBlobReference(fullBlobName);
            CreateBlob(newBlob);
            return newBlob;
        }

        private void CreateBlob(CloudAppendBlob blob)
        {
            Console.WriteLine($"Creating new blob: {blob.Name}");
            try
            {
                blob.CreateOrReplace(AccessCondition.GenerateIfNotExistsCondition());
            }
            catch (StorageException)
            {
            }
        }

        private string GetLastBlobNameInFolder(CloudBlobContainer container, string folderName)
        {
            var blobs = ListBlobs(container, folderName);

            var lastBlobPath = (from b in blobs
                                orderby b.Uri.AbsolutePath descending
                                select b.Uri.AbsolutePath).FirstOrDefault();

            if (lastBlobPath == "")
                return ""; // No blob found

            return Path.GetFileNameWithoutExtension(lastBlobPath);
        }

        private IEnumerable<IListBlobItem> ListBlobs(CloudBlobContainer container, string folderName)
        {
            var dir = container.GetDirectoryReference(folderName);
            var blobs = dir.ListBlobs();
            return blobs;
        }

        private string GetNextBlobName(string name)
        {
            // Assume name has format xxxxxx_yyyyyy or nnnnnn
            // where xxxxxx and yyyyyy can be arbitary string
            // and nnnnnn is an integer representation

            var nameParts = name.Split('_');

            if (nameParts.Length == 1)
            {
                // name is of format xxxxxx or nnnnnn
                int n;

                if (int.TryParse(name, out n))
                    return (++n).ToString("D6");
                return $"{name}_{0:D6}";
            }

            // name is of format xxxxxx_yyyyyy
            var xxxxxx = string.Concat(nameParts.Take(nameParts.Length - 1));
            var yyyyyy = nameParts.Last();

            // recursive call to self
            return $"{xxxxxx}_{GetNextBlobName(yyyyyy)}";
        }

        public async Task AppendBlobCleanupAsync()
        {
            foreach (IListBlobItem b in _tmpContainer.ListBlobs(null, true))
            {
                // list all blob in temp container, filter the ones that have "copied" metadata
                if (b.GetType() == typeof(CloudAppendBlob))
                {
                    CloudAppendBlob item = (CloudAppendBlob)b;
                    item.FetchAttributes();
                    if (item.Metadata.Keys.Contains("copied"))
                    {
                        Console.WriteLine($"Delete blob {item.Uri.ToString()}");
                        item.Delete(DeleteSnapshotsOption.IncludeSnapshots);
                    }
                    else
                    {
                        if (item.Metadata.Keys.Contains("full"))
                        {
                            var targetBlob = GetTargetBlockBlob(item);
                            await CopyBlobAsync(item, targetBlob);
                            await FlagAsCopiedAysnc(item);
                        }
                    }
                }
            }
        }
        private async Task FlagAsCopiedAysnc(CloudAppendBlob blob)
        {
            blob.FetchAttributes();
            blob.Metadata["copied"] = DateTime.Now.ToString();
            await blob.SetMetadataAsync();
        }

        private CloudBlockBlob GetTargetBlockBlob(CloudAppendBlob source)
        {
            var name = source.Name;
            var target = _outputContainer.GetBlockBlobReference(name);
            if (target.Exists()) // if the file already exists
            {
                target = _outputContainer.GetBlockBlobReference(name + "v2");
            }
            return target;
        }

        private async Task CopyBlobAsync(CloudAppendBlob source, CloudBlockBlob target)
        {
            Console.WriteLine($"Copying {source.Container.Name}/{source.Name} to {target.Container.Name}/{target.Name}");
            var sw = new Stopwatch();
            sw.Start();
            using (var sourceStream = await source.OpenReadAsync())
            {
                await target.UploadFromStreamAsync(sourceStream);
            }

            sw.Stop();
            Console.WriteLine($"Copy operation finished in {sw.Elapsed.TotalSeconds} second(s)");
        }
    }
}