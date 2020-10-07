// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.IO;
using System.Web;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure;
using Azure.Core;
using Azure.Storage;

namespace CognitiveSearch.UI.Controllers
{
    public class StorageController : Controller
    {
        private IConfiguration _configuration { get; set; }
        private DocumentSearchClient _docSearch { get; set; }

        public StorageController(IConfiguration configuration)
        {
            _configuration = configuration;
            _docSearch = new DocumentSearchClient(configuration);
        }

        [HttpPost("upload")]
        public async Task<IActionResult> Upload()
        {
            if (Request.Form.Files.Any())
            {
                var container = GetStorageContainer(0);

                foreach (var formFile in Request.Form.Files)
                {
                    if (formFile.Length > 0)
                    {
                        var cloudBlockBlob = container.GetBlobClient(formFile.FileName);
                        await cloudBlockBlob.UploadAsync(formFile.OpenReadStream());
                    }
                }
            }

            await _docSearch.RunIndexer();

            return new JsonResult("ok");
        }

        /// <summary>
        ///  Returns the requested document with an 'inline' content disposition header.
        ///  This hints to a browser to show the file instead of downloading it.
        /// </summary>
        /// <param name="storageIndex">The storage connection string index.</param>
        /// <param name="fileName">The storage blob filename.</param>
        /// <param name="mimeType">The expected mime content type.</param>
        /// <returns>The file data with inline disposition header.</returns>
        [HttpGet("preview/{storageIndex}/{fileName}/{mimeType}")]
        public async Task<FileContentResult> GetDocumentInline(int storageIndex, string fileName, string mimeType)
        {
            var decodedFilename = HttpUtility.UrlDecode(fileName);
            var container = GetStorageContainer(storageIndex);
            var cloudBlockBlob = container.GetBlobClient(decodedFilename);
            using (var ms = new MemoryStream())
            {
                await cloudBlockBlob.DownloadToAsync(ms);
                Response.Headers.Add("Content-Disposition", "inline; filename=" + decodedFilename);
                return File(ms.ToArray(), HttpUtility.UrlDecode(mimeType));
            }
        }

        private BlobContainerClient GetStorageContainer(int storageIndex)
        {
            string accountName = _configuration.GetSection("StorageAccountName")?.Value;
            string accountKey = _configuration.GetSection("StorageAccountKey")?.Value;

            var containerKey = "StorageContainerAddress";
            if (storageIndex > 0)
                containerKey += (storageIndex+1).ToString();
            var containerAddress = _configuration.GetSection(containerKey)?.Value.ToLower();

            var container = new BlobContainerClient(new Uri(containerAddress), new StorageSharedKeyCredential(accountName, accountKey));
            return container;
        }

        [HttpGet("containerTree")]
        public async Task<ContentResult> GetContainerTree()
        {
            // TODO: Change this to reference a blob file, and use the code below in a function to generate that file
            var container = GetStorageContainer(0);
            int id = 1;
            var containerTree = ListBlobsHierarchicalListing(container, ref id, null, null);

            // Add root
            JObject root = new JObject();
            root.Add("name", "root");
            root.Add("id", 1);
            root.Add("url", container.Uri.AbsoluteUri);
            root.Add("children", containerTree);
            JArray completedTree = new JArray();
            completedTree.Add(root);

            return new ContentResult()
            {
                StatusCode = 200,
                Content = completedTree.ToString(),
                ContentType = "application/json"
            };
        }

        private static JArray ListBlobsHierarchicalListing(BlobContainerClient container, ref int id, string? prefix, int? segmentSize)
        {
            string continuationToken = null;
            JArray currentNode = new JArray();

            try
            {
                // Call the listing operation and enumerate the result segment.
                // When the continuation token is empty, the last segment has been returned and
                // execution can exit the loop.
                do
                {
                    var resultSegment = container.GetBlobsByHierarchy(prefix: prefix, delimiter: "/")
                        .AsPages(continuationToken, segmentSize);

                    foreach (Azure.Page<BlobHierarchyItem> blobPage in resultSegment)
                    {
                        // A hierarchical listing may return both virtual directories and blobs.
                        foreach (BlobHierarchyItem blobhierarchyItem in blobPage.Values)
                        {
                            if (blobhierarchyItem.IsPrefix)
                            {
                                id++;
                                JObject currentItem = new JObject();
                                var folders = blobhierarchyItem.Prefix.Split("/");
                                currentItem.Add("name", folders[folders.Length - 2]);
                                currentItem.Add("id", id);
                                currentItem.Add("url", container.Uri.AbsoluteUri + "/" + blobhierarchyItem.Prefix);

                                // Call recursively with the prefix to traverse the virtual directory.
                                var children = ListBlobsHierarchicalListing(container, ref id, blobhierarchyItem.Prefix, null);
                                if (children != null && children.Count > 0)
                                {
                                    currentItem.Add("children", children);
                                }

                                // add to the array
                                currentNode.Add(currentItem);
                            }
                        }

                        // Get the continuation token and loop until it is empty.
                        continuationToken = blobPage.ContinuationToken;
                    }


                } while (continuationToken != "");

                return currentNode;
            }
            catch (RequestFailedException e)
            {
                throw;
            }
        }
    }
}
