using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Microsoft.WindowsAzure.Storage;
using System.Configuration;
using ImageUploadAPI.Controllers;
using Swashbuckle.Swagger.Annotations;
using Microsoft.SharePoint.Client;
using System.Security;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Emotion.Contract;

namespace ImageUpload.Controllers
{
    public class UploadImageController : ApiController
    {
        [HttpPost]
        [SwaggerResponse(
            HttpStatusCode.OK,
            Description = "Saved successfully",
            Type = typeof(UploadedFileInfo))]
        [SwaggerResponse(
            HttpStatusCode.BadRequest,
            Description = "Could not find file to upload")]
        [SwaggerOperation("UploadImage")]

        public async Task<IHttpActionResult> UploadImage(string fileName = "")
        {
            //Use a GUID in case the fileName is not specified
            if (fileName == "")
            {
                fileName = Guid.NewGuid().ToString();
            }

            //Check if submitted content is of MIME Multi Part Content with Form-data in it?
            if (!Request.Content.IsMimeMultipartContent("form-data"))
            {
                return BadRequest("Could not find file to upload");
            }

            //Read the content in a InMemory Muli-Part Form Data format
            var provider = await Request.Content.ReadAsMultipartAsync(new InMemoryMultipartFormDataStreamProvider());

            //Get the first file
            var files = provider.Files;
            var uploadedFile = files[0];

            //Extract the file extention
            var extension = ExtractExtension(uploadedFile);
            //Get the file's content type
            var contentType = uploadedFile.Headers.ContentType.ToString();

            //create the full name of the image with the fileName and extension
            var imageName = string.Concat(fileName, extension);

            //Get the reference to the Blob Storage and upload the file there
            var storageConnectionString = ConfigurationManager.AppSettings["StorageConnectionString"];
            var storageAccount = CloudStorageAccount.Parse(storageConnectionString);
            var blobClient = storageAccount.CreateCloudBlobClient();
            var container = blobClient.GetContainerReference("images");
            container.CreateIfNotExists();

            var blockBlob = container.GetBlockBlobReference(imageName);
            blockBlob.Properties.ContentType = contentType;
            using (var fileStream = await uploadedFile.ReadAsStreamAsync()) //as Stream is IDisposable
            {
                blockBlob.UploadFromStream(fileStream);
            }

            var airesult = await MakeRequest(blockBlob.Uri.ToString());
            //insert to sharepoint list
            string siteUrl = ConfigurationManager.AppSettings["SiteURL"].ToString();
            using (ClientContext ctx = new ClientContext(siteUrl))
            {
                SecureString passWord = new SecureString();
                string passwordText = ConfigurationManager.AppSettings["SitePassword"].ToString();
                string usernameText = ConfigurationManager.AppSettings["SiteUsername"].ToString();
                foreach (char c in passwordText.ToCharArray()) passWord.AppendChar(c);
                ctx.Credentials = new SharePointOnlineCredentials(usernameText, passWord);

                List list = ctx.Web.Lists.GetByTitle("PowerJoy");
                ListItemCreationInformation info = new ListItemCreationInformation();
                ListItem image = list.AddItem(info);
                image["Title"] = imageName;
                image["Url"] = blockBlob.Uri.ToString();
                image["TrumpRating"] = string.Format("You look {0} like the President of America", airesult.TrumpMatch);
                image["Feeling"] = string.Format("Someone looks {0}", airesult.Emotion);

                image.Update();
                ctx.ExecuteQuery();
            }

            
            var fileInfo = new UploadedFileInfo
            {
                FileName = fileName,
                FileExtension = extension,
                ContentType = contentType,
                FileURL = blockBlob.Uri.ToString(),
                Emotion = airesult.Emotion,
                TrumpFactor = airesult.TrumpMatch
            };
            return Ok(fileInfo);

        }

        private string emotionKey = ConfigurationManager.AppSettings["EmotionKey"].ToString();
        private string faceKey = ConfigurationManager.AppSettings["FaceKey"].ToString();
        private string faceList = ConfigurationManager.AppSettings["FaceList"].ToString();

        class AiResult
        {
            public string Emotion;
            public string TrumpMatch;
        }

        async Task<AiResult> MakeRequest(string imageToCheck)
        {
            AiResult res = new AiResult();
            // imageToCheck = "https://www.liberationnews.org/wp-content/uploads/2015/07/donaldtrump61815.jpg";

            EmotionServiceClient emotionServiceClient = new EmotionServiceClient(emotionKey);
            Emotion[] imageEmotion = await emotionServiceClient.RecognizeAsync(imageToCheck);

            Console.WriteLine("Feeling: " + imageEmotion[0].Scores.ToRankedList().First().Key);
            Console.WriteLine("Top score: " + imageEmotion[0].Scores.ToRankedList().First().Value);

            res.Emotion= string.Format("Unknwn ({0:P2})", 0);
            float bestScore = 0;
            foreach(var em in imageEmotion[0].Scores.ToRankedList())
            {
                if(em.Value > bestScore)
                {
                    bestScore = em.Value;
                    res.Emotion = res.Emotion = string.Format("{0} ({1:P2})", em.Key, em.Value); 
                }

            }

            FaceServiceClient faceServiceClient = new FaceServiceClient(faceKey);
            FaceList trumpList = null;
            try
            {
                trumpList = await faceServiceClient.GetFaceListAsync(faceList);
            }
            catch (FaceAPIException apiExp)
            {
                if (apiExp.ErrorCode == "FaceListNotFound")
                {
                    await faceServiceClient.CreateFaceListAsync(faceList, faceList, "A collection of trumps");
                    trumpList = await faceServiceClient.GetFaceListAsync(faceList);
                }
                else
                {
                    throw apiExp;
                }
            }
            if (trumpList.PersistedFaces.Count() < 5)
            {

                await faceServiceClient.AddFaceToFaceListAsync(faceList, "https://www.liberationnews.org/wp-content/uploads/2015/07/donaldtrump61815.jpg");
                await faceServiceClient.AddFaceToFaceListAsync(faceList, "http://thefederalist.com/wp-content/uploads/2016/02/trumpie.jpg");
                await faceServiceClient.AddFaceToFaceListAsync(faceList, "http://www.redstate.com/uploads/2016/02/donald-trump-is-still-soaring-in-iowa-but-there-are-now-some-clear-warning-signs.jpg");
                await faceServiceClient.AddFaceToFaceListAsync(faceList, "http://i.huffpost.com/gen/3706868/images/o-DONALD-TRUMP-FUNNY-facebook.jpg");
                await faceServiceClient.AddFaceToFaceListAsync(faceList, "http://media.salon.com/2015/04/donald_trump_thumbsup.jpg");
                trumpList = await faceServiceClient.GetFaceListAsync(faceList);
            }

            Face[] faceToCompare = await faceServiceClient.DetectAsync(imageToCheck);
            SimilarPersistedFace[] faces = await faceServiceClient.FindSimilarAsync(faceToCompare[0].FaceId, faceList, FindSimilarMatchMode.matchFace);

            res.TrumpMatch = String.Format("{0:P2}", 0);
            if (faces.Count() == 0)
            {
                Console.WriteLine("Sorry, nothing compares to you");
            }
            else
            {
                double totalConfidence = 0;
                foreach (SimilarPersistedFace matching in faces)
                {
                    totalConfidence += matching.Confidence;
                }
                double averageConfidence = totalConfidence / faces.Count();
                res.TrumpMatch = String.Format("{0:P2}", averageConfidence);
                Console.WriteLine("Trump comparison: " + res.TrumpMatch);
            }
            return res;
        }

        public static string ExtractExtension(HttpContent file)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var fileStreamName = file.Headers.ContentDisposition.FileName;
            var fileName = new string(fileStreamName.Where(x => !invalidChars.Contains(x)).ToArray());
            var extension = Path.GetExtension(fileName);
            return extension;
        }
    }
}