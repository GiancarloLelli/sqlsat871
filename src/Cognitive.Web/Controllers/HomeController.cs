using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Cognitive.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Cognitive.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly IConfiguration m_conf;

        public HomeController(IConfiguration configuration)
        {
            m_conf = configuration;
        }

        public async Task<IActionResult> Index(string id)
        {
            // Pass a list of blob URIs in ViewBag
            CloudStorageAccount account = CloudStorageAccount.Parse(m_conf.GetConnectionString("Storage"));
            CloudBlobClient client = account.CreateCloudBlobClient();
            CloudBlobContainer container = client.GetContainerReference("photos");
            List<BlobInfo> blobs = new List<BlobInfo>();

            var ct = new BlobContinuationToken();
            var segmentedBlobs = await container.ListBlobsSegmentedAsync(ct);

            foreach (IListBlobItem item in segmentedBlobs.Results)
            {
                var blob = item as CloudBlockBlob;

                if (blob != null)
                {
                    await blob.FetchAttributesAsync(); // Get blob metadata

                    if (string.IsNullOrEmpty(id) || HasMatchingMetadata(blob, id))
                    {
                        var caption = blob.Metadata.ContainsKey("Caption") ? blob.Metadata["Caption"] : blob.Name;

                        blobs.Add(new BlobInfo()
                        {
                            ImageUri = blob.Uri.ToString(),
                            ThumbnailUri = blob.Uri.ToString().Replace("/photos/", "/thumbnails/"),
                            Caption = caption
                        });
                    }
                }
            }

            ViewBag.Blobs = blobs.ToArray();
            ViewBag.Search = id;
            return View();
        }

        [HttpPost]
        public ActionResult Search(string term)
        {
            return RedirectToAction("Index", new { id = term });
        }

        [HttpPost]
        public async Task<ActionResult> Upload(IFormFile file)
        {
            if (file != null && file.Length > 0)
            {
                // Make sure the user selected an image file
                if (!file.ContentType.StartsWith("image"))
                {
                    TempData["Message"] = "Only image files may be uploaded";
                }
                else
                {
                    // Save the original image in the "photos" container
                    var fileStream = file.OpenReadStream();

                    CloudStorageAccount account = CloudStorageAccount.Parse(m_conf.GetConnectionString("Storage"));
                    CloudBlobClient client = account.CreateCloudBlobClient();
                    CloudBlobContainer container = client.GetContainerReference("photos");
                    CloudBlockBlob photo = container.GetBlockBlobReference(Path.GetFileName(file.FileName));
                    await photo.UploadFromStreamAsync(fileStream);
                    fileStream.Seek(0L, SeekOrigin.Begin);

                    using (var thumbnailStream = new MemoryStream())
                    {
                        using (var image = new Bitmap(fileStream))
                        {
                            var resized = new Bitmap(192, 192);
                            using (var graphics = Graphics.FromImage(resized))
                            {
                                graphics.CompositingQuality = CompositingQuality.HighSpeed;
                                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                graphics.CompositingMode = CompositingMode.SourceCopy;
                                graphics.DrawImage(image, 0, 0, 192, 192);
                                resized.Save(thumbnailStream, ImageFormat.Png);
                                thumbnailStream.Seek(0L, SeekOrigin.Begin);

                                // Submit the image to Azure's Computer Vision API
                                ComputerVisionClient vision = new ComputerVisionClient(
                                    new ApiKeyServiceClientCredentials(m_conf.GetValue<string>("AppSettings:VisionKey"))
                                );
                                vision.Endpoint = m_conf.GetValue<string>("AppSettings:VisionEndpoint");

                                VisualFeatureTypes[] features = new VisualFeatureTypes[] { VisualFeatureTypes.Description };
                                var result = await vision.AnalyzeImageAsync(photo.Uri.ToString(), features);

                                // Record the image description and tags in blob metadata
                                photo.Metadata.Add("Caption", result.Description.Captions[0].Text);

                                for (int i = 0; i < result.Description.Tags.Count; i++)
                                {
                                    string key = String.Format("Tag{0}", i);
                                    photo.Metadata.Add(key, result.Description.Tags[i]);
                                }

                                await photo.SetMetadataAsync();

                                container = client.GetContainerReference("thumbnails");
                                CloudBlockBlob thumbnail = container.GetBlockBlobReference(Path.GetFileName(file.FileName));
                                await thumbnail.UploadFromStreamAsync(thumbnailStream);
                            }
                        }
                    }
                }
            }

            // redirect back to the index action to show the form once again
            return RedirectToAction("Index");
        }

        private bool HasMatchingMetadata(CloudBlockBlob blob, string term)
        {
            foreach (var item in blob.Metadata)
            {
                if (item.Key.StartsWith("Tag") && item.Value.Equals(term, StringComparison.InvariantCultureIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
