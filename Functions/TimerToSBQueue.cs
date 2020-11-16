using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.ServiceBus;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Company.Function
{
    public static class TimerToSBQueue
    {
        [FunctionName("MoveQueueMessageToQueue")]
        public static async Task MoveQueueMessageToQueue(
            [ServiceBusTrigger("correlationqueue", Connection = "correlationservicebus_SERVICEBUS")] Message myQueueItem,
            [ServiceBus("correlationqueue_copy", Connection = "correlationservicebus_SERVICEBUS")] IAsyncCollector<Message> output,
            ILogger log)
        {
            log.LogInformation($"Moving message: {Encoding.UTF8.GetString(myQueueItem.Body)}");
            bool Success = true;
            string CustomId = "CID not Found";
            string BatchId = "BID not Found";
            string BatchItem = "BIt not Found";
            string BatchTotal = "BTo not Found";
            try
            {
                Message outmessage = myQueueItem.Clone(); //use clone to get a hold of all Properties, including the Custom ones
                if (myQueueItem.UserProperties != null)
                {
                    try { CustomId = myQueueItem.UserProperties["CustomId"].ToString(); } catch { };
                    try { BatchId = myQueueItem.UserProperties["BatchId"].ToString(); } catch { };
                    try { BatchItem = myQueueItem.UserProperties["BatchItem"].ToString(); } catch { };
                    try { BatchTotal = myQueueItem.UserProperties["BatchTotal"].ToString(); } catch { };
                }
                await output.AddAsync(outmessage);
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.Message);
                Success = false;
            }
            LogActivity(CustomId, BatchId, BatchItem, BatchTotal, Success, log);
            return;
        }

        [FunctionName("MoveQueueMessageToBlob")]
        public static async Task MoveQueueMessageToBlob( 
    [ServiceBusTrigger("correlationqueue_process", Connection = "correlationservicebus_SERVICEBUS")] Message myQueueItem, ILogger log)
        {
            log.LogInformation($"Moving message to Blob: {Encoding.UTF8.GetString(myQueueItem.Body)}");
            string CustomId = "CID_not_Found";
            string BatchId = "BID_not_Found";
            string BatchItem = "BIt_not_Found";
            string BatchTotal = "BTo_not_Found";
            bool Success = true;
            try
            {
                var blobconn = System.Environment.GetEnvironmentVariable("correlationstorage_STORAGE", EnvironmentVariableTarget.Process);
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse(blobconn);
                CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
                CloudBlobContainer container = blobClient.GetContainerReference("mycontainer");
                if (myQueueItem.UserProperties != null)
                {
                    try { CustomId = myQueueItem.UserProperties["CustomId"].ToString(); } catch { };
                    try { BatchId = myQueueItem.UserProperties["BatchId"].ToString(); } catch { };
                    try { BatchItem = myQueueItem.UserProperties["BatchItem"].ToString(); } catch { };
                    try { BatchTotal = myQueueItem.UserProperties["BatchTotal"].ToString(); } catch { };
                }
                string body = Encoding.UTF8.GetString(myQueueItem.Body);
                await container.CreateIfNotExistsAsync();
                var blob = container.GetAppendBlobReference(string.Format("{0}.txt", CustomId));
                if (!blob.ExistsAsync().Result)
                {
                    await blob.CreateOrReplaceAsync();
                }
                MemoryStream msWrite = new MemoryStream(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(myQueueItem.Body)));
                msWrite.Position = 0;
                blob.Metadata["CustomId"] = CustomId;
                blob.Metadata["BatchId"] = BatchId;
                blob.Metadata["BatchItem"] = BatchItem;
                blob.Metadata["BatchTotal"] = BatchTotal;
                using (msWrite)
                {
                    await blob.UploadFromStreamAsync(msWrite);
                }
            }
            catch(Exception ex)
            {
                log.LogInformation(ex.Message);
                Success = false;
            }

            LogActivity(CustomId, BatchId, BatchItem, BatchTotal, Success, log);
        }


        private static void LogActivity(string CustomId, string BatchId, string BatchItem, string BatchTotal, bool IsCompletedSuccessfully, ILogger log)
        {
            string logstring = string.Format("{{\"CustomId\":\"{0}\",\"BatchId\":\"{1}\",\"BatchItem\":\"{2}\",\"BatchTotal\":\"{3}\",\"Success\":\"{4}\"}}", CustomId, BatchId, BatchItem,BatchTotal, IsCompletedSuccessfully);
            log.LogInformation(logstring);
            Console.WriteLine(logstring);
        }

        [FunctionName("Move")]
        public static async Task Move(
            [ServiceBusTrigger("correlationqueue", Connection = "correlationservicebus_SERVICEBUS")] string myQueueItem,
            [ServiceBus("correlationqueue_copy", Connection = "correlationservicebus_SERVICEBUS")] IAsyncCollector<string> output,
            ILogger log)
        {
            log.LogInformation($"Moving message: {myQueueItem}");
            Console.WriteLine($"Moving message: {myQueueItem}");

            //DumpActivity(CustomActivityId, ProjectId, NrOfTotal, log);            
            await output.AddAsync(myQueueItem);
        }
        [FunctionName("Consume")]
        public static void Consume([ServiceBusTrigger("correlationqueue_process", Connection = "correlationservicebus_SERVICEBUS")] string myQueueItem, ILogger log)
        {
            log.LogInformation($"Consuming message: {myQueueItem}");
        }

        [FunctionName("Timer")]
        public static async Task Run(
   [TimerTrigger("0 */5 * * * *")] TimerInfo myTimer,
   [ServiceBus("correlationqueue", Connection = "correlationservicebus_SERVICEBUS")] IAsyncCollector<string> output,
   ILogger log)
        {
            string message = $"{DateTime.Now}";
            log.LogInformation($"Timer generated message at: {message}");

            await output.AddAsync(message);
        }


    }
}
