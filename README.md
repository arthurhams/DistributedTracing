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
The third Consumer of the Service Bus is an Azure Function that moves the message to Blob Storage. This function takes the Custom Properties of the Message and converts it to Metadata on the Blob for further processing/tracking. 
The code is included in this repo

<h2>Appendix A - Workbook Queries</h2>
Query 1 - API Calls with BatchId and CustomID in Header:
```
ApiManagementGatewayLogs
|project TimeGenerated, RequestHeaders["BatchId"], RequestHeaders["CustomId"] 
| project-rename  BatchId = RequestHeaders_BatchId, CustomId = RequestHeaders_CustomId
| where BatchId != ''
| top 20 by TimeGenerated desc  
```

Query 2 - Union of all logs based on BatchId
```
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
```

Query 3a - All APIM logs based on CorrelationId
```
ApiManagementGatewayLogs 
|where CorrelationId == "{ServiceRunId}"
|top 20 by TimeGenerated asc
```

Query 3b - All logs for Azure Functions based on InvocationId
```
union traces | union exceptions | where timestamp > ago(30d) | where customDimensions['InvocationId'] == "{ServiceRunId}" | order by timestamp asc
```

Query 3c - All Logic App Logs based on resource_runId_s
```
zureDiagnostics 
|where resource_runId_s  == "{ServiceRunId}" and Category == "WorkflowRuntime"
|top 20 by TimeGenerated asc
| project TenantId, TimeGenerated, ResourceId, ResourceGroup, SubscriptionId, Resource, ResourceType, OperationName, ResultType, CorrelationId, ResultDescription, status_s, startTime_t, endTime_t, workflowId_s, resource_location_s, resource_workflowId_g, resource_originRunId_s
```

 
<h2>Appendix B - Used References</h2>
https://docs.microsoft.com/en-us/azure/azure-monitor/app/correlation (HTTP Correlation Deprecated) <br />
https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-end-to-end-tracing<br />
https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messages-payloads<br />
https://docs.microsoft.com/en-us/rest/api/storageservices/set-blob-metadata<br />
https://docs.microsoft.com/en-us/azure/azure-monitor/platform/logicapp-flow-connector<br />
https://azure.microsoft.com/nl-nl/blog/query-azure-storage-analytics-logs-in-azure-log-analytics/<br />
https://github.com/Azure/azure-functions-powershell-worker/issues/309<br />
https://github.com/Azure/azure-webjobs-sdk/issues/2154<br />
https://github.com/Azure/azure-functions-servicebus-extension/issues/13<br />
https://github.com/Azure/azure-sdk-for-net/blob/master/sdk/servicebus/Microsoft.Azure.ServiceBus/src/Core/MessageSender.cs<br />
https://github.com/Azure/Azure-Functions/issues/693<br />
https://github.com/xstof/xstof-fta-distributedtracing <br />
https://www.pluralsight.com/guides/implementing-distributed-tracing-with-azure's-application-insights<br />
https://medium.com/prospa-engineering/how-azures-application-insights-correlates-telemetry-a73731f30bbd
https://medium.com/prospa-engineering/implementing-distributed-tracing-with-azures-application-insights-5a09cc1c200c<br />
https://brettmckenzie.net/2019/10/02/things-i-wish-i-knew-earlier-about-distributed-tracing-in-azure-application-insights/<br />
https://brettmckenzie.net/2019/10/20/consider-using-service-bus-queues-instead-of-azure-storage-queues-when-using-application-insights/<br />
https://www.serverlessnotes.com/docs/azure-functions-use-application-insights-for-logging<br />
https://dev.applicationinsights.io/documentation/Using-the-API/Power-BI<br />
https://mihirkadam.wordpress.com/2019/06/27/<br />azure-functions-how-to-write-a-custom-logs-in-application-insights/<br />
https://feedback.azure.com/forums/287593-logic-apps/suggestions/34752475-set-blob-metadata-action<br />
https://stackoverflow.com/questions/28637054/error-while-deserializing-azure-servicebus-queue-message-sent-from-node-js-azur<br />
https://stackoverflow.com/questions/42117135/send-a-full-brokered-message-in-azure-service-bus-from-an-azure-function<br />
https://stackoverflow.com/questions/51883367/can-you-set-metadata-on-an-azure-cloudblockblob-at-the-same-time-as-uploading-it<br />
https://stackoverflow.com/questions/52419414/brokeredmessage-send-and-message-consumer-in-azure-function-v2<br />
https://stackoverflow.com/questions/56473786/how-to-upload-zip-file-from-api-management-to-blob-storage<br />
https://yinlaurent.wordpress.com/2019/03/24/debug-your-azure-api-management-policies-with-custom-logs/<br />
https://docs.microsoft.com/en-us/azure/data-explorer/kusto/query/parseoperator<br />
https://stackoverflow.com/questions/59725608/unknown-function-app-while-merging-two-application-insights-resources<br />
https://azure.github.io/AppService/2019/11/01/App-Service-Integration-with-Azure-Monitor.html<br />
https://stackoverflow.com/questions/55538434/how-to-write-kusto-query-to-get-results-in-one-table<br />
https://stackoverflow.com/questions/63779632/split-column-string-with-delimiters-into-separate-columns-in-azure-kusto<br />
https://camerondwyer.com/2020/05/26/how-to-report-on-serialized-json-object-data-in-application-insights-azure-monitor-using-kusto/<br />
https://docs.microsoft.com/nl-nl/azure/azure-monitor/platform/powerbi<br />
https://docs.microsoft.com/en-us/azure/data-factory/connector-azure-blob-storage<br />
https://www.sqlshack.com/populate-azure-sql-database-from-azure-blob-storage-using-azure-data-factory/<br />
https://docs.microsoft.com/en-us/azure/data-factory/copy-activity-preserve-metadata#preserve-metadata<br />
https://knowledgeimmersion.wordpress.com/2020/02/26/custom-log-analytics-for-azure-data-factory/<br />
https://github.com/Azure/azure-sdk-for-python/issues/12050<br />
