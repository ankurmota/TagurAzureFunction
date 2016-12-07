#r "Microsoft.WindowsAzure.Storage"
#r "System.Net.Http"
#r "Newtonsoft.Json"

using System;
using System.Linq;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Configuration;
using Newtonsoft.Json;

public async static Task Run(Stream image, CloudBlobContainer container, CloudBlockBlob blob, TraceWriter log)
{
    var array = await image.ToByteArrayAsync();

    Guid documentId = new Guid(blob.Name.Split(".".ToCharArray()).FirstOrDefault());

    log.Info($"Analyzing uploaded image {blob.Name} for description, tabs, and adult content...");

    var imageAnalysisResult = await GetImageAnalysisAsync(array, documentId, log);

    bool isAdult = imageAnalysisResult.details.adult.isAdultContent;

    log.Info("Is Adult: " + isAdult.ToString());

    var thumbnail = await GenerateThumbnailAsync(array, documentId, blob.Name, log);

    AddMetadata(thumbnail, container.Name, blob.Name, imageAnalysisResult, log);

    if (!isAdult) await AddDocumentForIndexingAsync(blob.Name, imageAnalysisResult, array, log);

    await SendStreamToAnalyticsAsync(0.0, 0.0, imageAnalysisResult.tags.Count);

    log.Info($"Function complete for {blob.Name}.");
    log.Info($"---------------------------");
}

#region Helpers

// Add a new document for search indexing
private async static Task<bool> AddDocumentForIndexingAsync(string fileName, ImageAnalysisResult imageAnalysisResult, byte[] array, TraceWriter log)
{
    bool isAdded = false;

    var documentDbUrl = ConfigurationManager.AppSettings["DocumentDbUrl"].ToString();
    var documentDbKey = ConfigurationManager.AppSettings["DocumentDbKey"].ToString();

    var client = new DocumentClient(new Uri(documentDbUrl), documentDbKey);
    Uri collUri = UriFactory.CreateDocumentCollectionUri("outDatabase", "Uploads");

    Document createdDocument = await client.CreateDocumentAsync(collUri, imageAnalysisResult);

    log.Info($"Added document at {createdDocument.SelfLink}.");

    Attachment attachment = null;

    using (MemoryStream ms = new MemoryStream(array))
    {
        attachment = await client.CreateAttachmentAsync(createdDocument.AttachmentsLink, ms, new MediaOptions { ContentType = "image/jpg", Slug = fileName });

        log.Info($"Added {fileName} as attachment at {createdDocument.AttachmentsLink}.");
    }

    return isAdded;
}

// Apply analysis metadata to the target object
private static bool AddMetadata(Stream image, string containerName, string fileName, ImageAnalysisResult imageAnalysisResult, TraceWriter log)
{
    var storageAccountConnectionString = ConfigurationManager.AppSettings["tagur_STORAGE"].ToString();

    CloudStorageAccount storageAccount = CloudStorageAccount.Parse(storageAccountConnectionString);

    log.Info($"Intializing app-downloads");

    CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
    CloudBlobContainer container = blobClient.GetContainerReference($"app-downloads");

    try
    {
        log.Info($"Creating {fileName}...");

        CloudBlockBlob blob = container.GetBlockBlobReference(fileName);

        image.Position = 0;

        blob.UploadFromStream(image);

        if (blob != null)
        {
            blob.FetchAttributes();

            blob.Metadata["isAdultContent"] = imageAnalysisResult.details.adult.isAdultContent.ToString();
            blob.Metadata["adultScore"] = imageAnalysisResult.details.adult.adultScore.ToString("P0").Replace(" ", "");
            blob.Metadata["isRacyContent"] = imageAnalysisResult.details.adult.isRacyContent.ToString();
            blob.Metadata["racyScore"] = imageAnalysisResult.details.adult.racyScore.ToString("P0").Replace(" ", "");

            blob.Metadata["caption"] = imageAnalysisResult.caption + " ";
            blob.Metadata["tags"] = string.Join(",", imageAnalysisResult.tags);

            blob.Metadata["foregroundColor"] = imageAnalysisResult.details.color.dominantColorForeground;
            blob.Metadata["backgroundColor"] = imageAnalysisResult.details.color.dominantColorBackground;

            blob.Metadata["accentColor"] = imageAnalysisResult.details.color.accentColor;

            blob.SetMetadata();
        }

        log.Info($"Metadata added for {blob.Name}.");

    }
    catch (Exception ex)
    {
        log.Info(ex.Message);
    }

    return true;
}

// Call the Cognitive Services Computer API for analysis
private async static Task<ImageAnalysisResult> GetImageAnalysisAsync(byte[] bytes, Guid documentId, TraceWriter log)
{

    HttpClient client = new HttpClient();

    var subscriptionKey = ConfigurationManager.AppSettings["SubscriptionKey"].ToString();
    client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

    HttpContent payload = new ByteArrayContent(bytes);
    payload.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

    string analysisFeatures = "Color,ImageType,Tags,Categories,Description,Adult";

    var results = await client.PostAsync($"https://api.projectoxford.ai/vision/v1.0/analyze?visualFeatures={analysisFeatures}", payload);
    ImageAnalysisInfo imageAnalysisResult = await results.Content.ReadAsAsync<ImageAnalysisInfo>();

    ImageAnalysisResult result = new ImageAnalysisResult()
    {

        id = documentId.ToString(),
        details = imageAnalysisResult,
        caption = imageAnalysisResult.description.captions.FirstOrDefault().text.AsCleanString(log),
        tags = imageAnalysisResult.description.tags.Select(s => s.AsCleanString(log)).ToList(),
    };

    return result;
}

// Generate a small thumbnail for preview performance
private async static Task<Stream> GenerateThumbnailAsync(byte[] bytes, Guid documentId, string fileName, TraceWriter log)
{

    HttpClient client = new HttpClient();

    var subscriptionKey = ConfigurationManager.AppSettings["SubscriptionKey"].ToString();
    client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", subscriptionKey);

    HttpContent payload = new ByteArrayContent(bytes);
    payload.Headers.ContentType = new MediaTypeWithQualityHeaderValue("application/octet-stream");

    var results = await client.PostAsync("https://api.projectoxford.ai/vision/v1.0/generateThumbnail?width=150&height=150&smartCropping=true", payload);
    var result = await results.Content.ReadAsStreamAsync();

    log.Info($"Thumbnail created for {fileName}, size: {result.Length}...");

    return result;
}

//Send the timestamp and tag count to Power BI
private async static Task<bool> SendStreamToAnalyticsAsync(double latitude, double longitude, int count)
{
    HttpClient client = new HttpClient();

    string analyticsUrl = "[YOUR POWER BI STREAM URL]";

    string postData = String.Format("[{{ \"latitude\": {0}, \"longitude\": {1}, \"count\": {2}, \"ts\": \"{3}\" }}]", latitude, longitude, count++, DateTime.Now);
    var response = await client.PostAsync(analyticsUrl, new StringContent(postData));

    return true;

}

// Converts a stream to a byte array 
private async static Task<byte[]> ToByteArrayAsync(this Stream stream)
{
    Int32 length = stream.Length > Int32.MaxValue ? Int32.MaxValue : Convert.ToInt32(stream.Length);
    byte[] buffer = new Byte[length];
    await stream.ReadAsync(buffer, 0, length);
    return buffer;
}

// Make sure the string is clean
public static string AsCleanString(this string value, TraceWriter log)
{
    log.Info($"clean string: {value}.");
    return (value + "").Trim();
}

#endregion

#region classes

public class ImageAnalysisResult
{
    public string id { get; set; }
    public string caption { get; set; }
    public List<string> tags { get; set; }
    public ImageAnalysisInfo details { get; set; }
}

public class ImageAnalysisInfo
{
    public Category[] categories { get; set; }
    public Adult adult { get; set; }
    public Tag[] tags { get; set; }
    public Description description { get; set; }
    public string requestId { get; set; }
    public Metadata metadata { get; set; }
    public Color color { get; set; }
    public Imagetype imageType { get; set; }
}

public class Adult
{
    public bool isAdultContent { get; set; }
    public bool isRacyContent { get; set; }
    public float adultScore { get; set; }
    public float racyScore { get; set; }
}

public class Description
{
    public string[] tags { get; set; }
    public Caption[] captions { get; set; }
}

public class Caption
{
    public string text { get; set; }
    public float confidence { get; set; }
}

public class Metadata
{
    public int width { get; set; }
    public int height { get; set; }
    public string format { get; set; }
}

public class Color
{
    public string dominantColorForeground { get; set; }
    public string dominantColorBackground { get; set; }
    public string[] dominantColors { get; set; }
    public string accentColor { get; set; }
    public bool isBWImg { get; set; }
}

public class Imagetype
{
    public int clipArtType { get; set; }
    public int lineDrawingType { get; set; }
}

public class Category
{
    public string name { get; set; }
    public float score { get; set; }
}

public class Tag
{
    public string name { get; set; }
    public float confidence { get; set; }
    public string hint { get; set; }
}

#endregion