# A practical guide to Distributed Tracing in Azure

<h2>Intro</h2>

This guide describes a pattern to combine native logging options from different Azure services into a single Workbook that gives an overview of how data flowed through the system as well as a drill down into specific logs per Service.
As there are some existing (and transitioning) correlation techniques already in Azure (Application Insights), I've chosen to make use of a custom set of properties to keep track of things, so it does not interfere with the existing ones. 
These properties are:
CustomID: 	A custom identifier like a Project Name, ID or any other identifier you want to use for tracing
BatchID:	A custom identifier for the current run. For example if there are multiple runs for the same CustomID, this field can be used to uniquely identify the current run.
BatchItem:	If a batch/run consists of more than one item and you want to check for completeness, use this field incrementally for each of the different items.
BatchTotal:	 If a batch/run consists of more than one item and you want to check for completeness, use this field to hold the total number of Items.

<h2>Architecture</h2>
This guide is based on below architecture
 
The architecture clearly shows that different services use both different means of transporting metadata/properties as well as different Logging endpoints and formats/content. This document describes the configuration of each of the services used to log the properties so that they can be combined / consumed.
A Workbook is used to combine a number of queries that aggregate all logs into a single trace for a unique batch and allows for a drilldown into the logs of each specific service.
The starting point of this flow is an API that accepts the Custom Properties as Header and a string as Body and can be called like this: 
 
<h2>Services</h2>
API Management
The entry-point of this system is Azure API Management. It is configured as a wrapper around a Service Bus Queue, allowing to apply a set of Policies, including some to both log the incoming Headers into Log Analytics, as well as putting those headers as Custom Properties on the Service Bus Queue Message.
Configuration
API Management is configured to use App Insights for logging: 
https://docs.microsoft.com/en-us/azure/api-management/api-management-howto-app-insights 
In the Settings -> Diagnostic Settings -> Additional Settings of the used API, the Headers for the Custom Properties are set to Log
 
The API is also configured to take the Custom Properties as Query parameters
 
In inbound policy, either the Header or the Querystring is used to propagate the Custom Properties to Properties of the Service Bus Message.
<set-header name="CustomId" exists-action="skip">           
 <value>
  @(context.Request.Headers.GetValueOrDefault("CustomId",context.Request.Url.Query.GetValueOrDefault("CustomId")))
</value>
</set-header>
            

 
<h2>Azure Function - Move to Queue</h2>
One of the Consumers of the Service Bus is an Azure Function that moves the message to another Service Bus Queue (just to show how one can persist the Service Bus Message Properties). 
The code of this Function is included in the Appendix. It basically takes a Service Bus Message as input, uses Message.Clone() to create a copy including the Custom Properties and outputs the cloned Message to another Queue. It logs the Custom Properties to App Insights.
Logic Apps
The second consumer of the Service Bus Queue is Azure Logic Apps. It has Log Analytics enabled:
 
It is triggered by the second Service Bus Queue and uses the Service Bus Send Message to send the Message body and all metadata copied from the incoming message to yet another Service Bus Queue (left picture)
. It has Tracked Properties defined to Log the Custom Properties (right picture) 
Clone a Message to another Queue using Send Message	Setting of the Action Step with Tracked Properties
 	 

<h2>Azure Function - Move to Blob</h2>
The third Consumer of the Service Bus is an Azure Function that moves the message to Blob Storage. This function takes the Custom Properties of the Message and converts it to Metadata on the Blob for further processing/tracking. The code is included in the Appendix.
 
Azure Workbook - Stitching it all together
After all Services are configured for the logging of the Custom Properties, it is now time to tie them all together into a simple overview. I've used an Azure Workbook as user interface, but this can also be exported to PowerBI.
The Workbook looks as follows: 
 
It shows a list of all incoming calls into API Management and shows the BathcId and CustomId's from the Headers.
If you select one of the Batches, the second Query is fired that uses the BatchId to find all Log Items where this BatchId is used. Passing Parameters is configured in the Advanced Settings of a Query item:
 
The exported parameters can be consumed in Sub Queries by encapsulating them in accolades: {BatchId}
The second Query is the Trace through the system. If a record is select, the third query is triggerd that shows all available logging for that specific Service, based on the internal RunId of that service, related to the BatchId.
In the Workbook I only show the sub queries based on the selection before using the Conditionally Visible option from the Advanced Settings:
 
Al the Queries from the Workbook are included in the Appendix.
 
<h2>Azure SQL - Saving to Database</h2>

CREATE TABLE Batches
( batch_db_id [int] IDENTITY(1,1) NOT NULL,
  batchId char(50) NOT NULL,
  CustomId char(50),
  batchItem int,  
  batchTotal int,
  message text,
  CONSTRAINT batch_id_pk PRIMARY KEY (batch_db_id)
);
 

 

<h2>Appendix A - Azure Functions Code</h2>
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
            ILogger log)      {
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
                MemoryStream msWrite = new MemoryStream(Encoding.UTF8.GetBytes("aaaaaa"));
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
    }
}

<h2>Appendix B - Workbook Queries</h2>

Query 1 - API Calls with BatchId and CustomID in Header:
ApiManagementGatewayLogs
|project TimeGenerated, RequestHeaders["BatchId"], RequestHeaders["CustomId"] 
| project-rename  BatchId = RequestHeaders_BatchId, CustomId = RequestHeaders_CustomId
| where BatchId != ''
| top 20 by TimeGenerated desc  

Query 2 - Union of all logs based on BatchId
let apimLogs = workspace('correlationloganalytics').ApiManagementGatewayLogs 
|project TimeGenerated, OperationName, IsRequestSuccess, RequestHeaders["BatchId"], RequestHeaders["CustomId"], RequestHeaders["BatchItem"], RequestHeaders["BatchTotal"]
|project-rename operation_Name = OperationName, timestamp = TimeGenerated, BatchId = RequestHeaders_BatchId, CustomId = RequestHeaders_CustomId, BatchItem = RequestHeaders_BatchItem, BatchTotal = RequestHeaders_BatchTotal
| extend BatchId = tostring(BatchId), BatchItem = tostring(BatchItem), CustomId=tostring(CustomId), BatchTotal= tostring(BatchTotal), 
IsRequestSuccess = tostring(IsRequestSuccess);


let appLogs = union app('correlationapp').traces
|extend customprops = parse_json(message)
|extend BatchId = tostring(customprops.BatchId), CustomId = tostring(customprops.CustomId), BatchItem = tostring(customprops.BatchItem), BatchTotal = tostring(customprops.BatchTotal), IsRequestSuccess = tostring(customprops.Success) 
|project timestamp, operation_Name, BatchId, CustomId, BatchItem, BatchTotal, IsRequestSuccess;

let logicAppLogs = workspace('correlationloganalytics').AzureDiagnostics
|project TimeGenerated, OperationName, status_s, trackedProperties_BatchId_s, trackedProperties_CustomId_s, trackedProperties_BatchTotal_s, trackedProperties_BatchItem_s, columnifexists('trackedProperties_BatchItem_s_ssic', '')
|project-rename operation_Name = OperationName, timestamp = TimeGenerated, IsRequestSuccess = status_s, BatchId = trackedProperties_BatchId_s, CustomId = trackedProperties_CustomId_s, BatchItem = trackedProperties_BatchItem_s, BatchTotal = trackedProperties_BatchTotal_s
| extend BatchId = tostring(BatchId), BatchItem = tostring(BatchItem), CustomId=tostring(CustomId), BatchTotal= tostring(BatchTotal), IsRequestSuccess = tostring(IsRequestSuccess);

 apimLogs | union logicAppLogs, appLogs
 |where BatchId == {BatchId}
| order by timestamp asc

Query 3a - All APIM logs based on CorrelationId
ApiManagementGatewayLogs 
|where CorrelationId == "{ServiceRunId}"
|top 20 by TimeGenerated asc

Query 3b - All logs for Azure Functions based on InvocationId
union traces | union exceptions | where timestamp > ago(30d) | where customDimensions['InvocationId'] == "{ServiceRunId}" | order by timestamp asc

Query 3c - All Logic App Logs based on resource_runId_s
AzureDiagnostics 
|where resource_runId_s  == "{ServiceRunId}" and Category == "WorkflowRuntime"
|top 20 by TimeGenerated asc
| project TenantId, TimeGenerated, ResourceId, ResourceGroup, SubscriptionId, Resource, ResourceType, OperationName, ResultType, CorrelationId, ResultDescription, status_s, startTime_t, endTime_t, workflowId_s, resource_location_s, resource_workflowId_g, resource_originRunId_s


 
<h2>Appendix C - Used References</h2>
https://docs.microsoft.com/en-us/azure/azure-monitor/app/correlation (HTTP Correlation Deprecated)
https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-end-to-end-tracing 
https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messages-payloads 
https://docs.microsoft.com/en-us/rest/api/storageservices/set-blob-metadata
https://docs.microsoft.com/en-us/azure/azure-monitor/platform/logicapp-flow-connector 
https://azure.microsoft.com/nl-nl/blog/query-azure-storage-analytics-logs-in-azure-log-analytics/ 
https://github.com/Azure/azure-functions-powershell-worker/issues/309 
https://github.com/Azure/azure-webjobs-sdk/issues/2154
https://github.com/Azure/azure-functions-servicebus-extension/issues/13
https://github.com/Azure/azure-sdk-for-net/blob/master/sdk/servicebus/Microsoft.Azure.ServiceBus/src/Core/MessageSender.cs
https://github.com/Azure/Azure-Functions/issues/693 
https://github.com/xstof/xstof-fta-distributedtracing 
https://www.pluralsight.com/guides/implementing-distributed-tracing-with-azure's-application-insights
https://medium.com/prospa-engineering/how-azures-application-insights-correlates-telemetry-a73731f30bbd 
https://medium.com/prospa-engineering/implementing-distributed-tracing-with-azures-application-insights-5a09cc1c200c 
https://brettmckenzie.net/2019/10/02/things-i-wish-i-knew-earlier-about-distributed-tracing-in-azure-application-insights/
https://brettmckenzie.net/2019/10/20/consider-using-service-bus-queues-instead-of-azure-storage-queues-when-using-application-insights/ 
https://www.serverlessnotes.com/docs/azure-functions-use-application-insights-for-logging 
https://dev.applicationinsights.io/documentation/Using-the-API/Power-BI
https://mihirkadam.wordpress.com/2019/06/27/azure-functions-how-to-write-a-custom-logs-in-application-insights/
https://feedback.azure.com/forums/287593-logic-apps/suggestions/34752475-set-blob-metadata-action
https://stackoverflow.com/questions/28637054/error-while-deserializing-azure-servicebus-queue-message-sent-from-node-js-azur
https://stackoverflow.com/questions/42117135/send-a-full-brokered-message-in-azure-service-bus-from-an-azure-function 
https://stackoverflow.com/questions/51883367/can-you-set-metadata-on-an-azure-cloudblockblob-at-the-same-time-as-uploading-it
https://stackoverflow.com/questions/52419414/brokeredmessage-send-and-message-consumer-in-azure-function-v2
https://stackoverflow.com/questions/56473786/how-to-upload-zip-file-from-api-management-to-blob-storage
https://yinlaurent.wordpress.com/2019/03/24/debug-your-azure-api-management-policies-with-custom-logs/
https://docs.microsoft.com/en-us/azure/data-explorer/kusto/query/parseoperator
https://stackoverflow.com/questions/59725608/unknown-function-app-while-merging-two-application-insights-resources
https://azure.github.io/AppService/2019/11/01/App-Service-Integration-with-Azure-Monitor.html
https://stackoverflow.com/questions/55538434/how-to-write-kusto-query-to-get-results-in-one-table
https://stackoverflow.com/questions/63779632/split-column-string-with-delimiters-into-separate-columns-in-azure-kusto
https://camerondwyer.com/2020/05/26/how-to-report-on-serialized-json-object-data-in-application-insights-azure-monitor-using-kusto/
https://docs.microsoft.com/nl-nl/azure/azure-monitor/platform/powerbi
https://docs.microsoft.com/en-us/azure/data-factory/connector-azure-blob-storage 
https://www.sqlshack.com/populate-azure-sql-database-from-azure-blob-storage-using-azure-data-factory/ 
https://docs.microsoft.com/en-us/azure/data-factory/copy-activity-preserve-metadata#preserve-metadata
https://knowledgeimmersion.wordpress.com/2020/02/26/custom-log-analytics-for-azure-data-factory/ 
https://github.com/Azure/azure-sdk-for-python/issues/12050   