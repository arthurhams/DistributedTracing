
# Replace with your Workspace ID
$CustomerId = ""  

# Replace with your Primary Key
$SharedKey = ""

#Dot Sourced the params so they wont show up in github
. "C:\Users\arhallen\Source\Repos\Params\DistributedTracingParams.ps1"

# Specify the name of the record type that you'll be creating (Log Analytics will appen _CL as Custom Log indication)
$LogType = "CustomLogForAPICalls"

# You can use an optional field to specify the timestamp from the data. If the time field is not specified, Azure Monitor assumes the time is the message ingestion time
$TimeStampField = ""

$datestr = Get-Date -format "yyyy/MM/dd HH:mm:ss" 
$CustomId = "CustomIdForDemo" + $datestr
$BatchID = New-Guid
$BatchItem = 1
$BatchTotal = 1
$Content = "Contenline1;Contentline2;Contentline3;"

$results = @() 

$tmp = New-Object -TypeName PSobject
$tmp | Add-Member -Name "CustomId" -Type NoteProperty -Value $CustomId
$tmp | Add-Member -Name "BatchId" -Type NoteProperty -Value $BatchId
$tmp | Add-Member -Name "BatchItem" -Type NoteProperty -Value $BatchItem
$tmp | Add-Member -Name "BatchTotal" -Type NoteProperty -Value $BatchTotal

$results += $tmp

    $Headers = @{
        "CustomId" = $CustomId;
        "BatchId" = "$BatchId";
        "BatchItem" = "$BatchItem";
        "BatchTotal" = "$BatchTotal";
    }



$results

#convert to json to send to LA API
$jsonResults=$results | ConvertTo-Json



Function Post-APICall($content, $headers)
{
    $method = "POST"
    $contentType = "application/json"
    $uri = "https://correlationapim.azure-api.net/correlationqueue/messages"


    $response = Invoke-WebRequest -Uri $uri -Method $method -ContentType $contentType -Headers $headers -Body $content -UseBasicParsing
    return $response.StatusCode

}




#Below code is based on this artice: https://docs.microsoft.com/en-us/azure/azure-monitor/platform/data-collector-api
#--------------------------------------------------------------------------------------------------------------------
# Function to create the authorization signature
Function Build-Signature ($customerId, $sharedKey, $date, $contentLength, $method, $contentType, $resource)
{
    $xHeaders = "x-ms-date:" + $date
    $stringToHash = $method + "`n" + $contentLength + "`n" + $contentType + "`n" + $xHeaders + "`n" + $resource

    $bytesToHash = [Text.Encoding]::UTF8.GetBytes($stringToHash)
    $keyBytes = [Convert]::FromBase64String($sharedKey)

    $sha256 = New-Object System.Security.Cryptography.HMACSHA256
    $sha256.Key = $keyBytes
    $calculatedHash = $sha256.ComputeHash($bytesToHash)
    $encodedHash = [Convert]::ToBase64String($calculatedHash)
    $authorization = 'SharedKey {0}:{1}' -f $customerId,$encodedHash
    return $authorization
}




# Function to create and post the request
Function Post-LogAnalyticsData($customerId, $sharedKey, $body, $logType)
{
    $method = "POST"
    $contentType = "application/json"
    $resource = "/api/logs"
    $rfc1123date = [DateTime]::UtcNow.ToString("r")
    $contentLength = $body.Length
    $signature = Build-Signature `
        -customerId $customerId `
        -sharedKey $sharedKey `
        -date $rfc1123date `
        -contentLength $contentLength `
        -method $method `
        -contentType $contentType `
        -resource $resource
    $uri = "https://" + $customerId + ".ods.opinsights.azure.com" + $resource + "?api-version=2016-04-01"

    $headers = @{
        "Authorization" = $signature;
        "Log-Type" = $logType;
        "x-ms-date" = $rfc1123date;
        "time-generated-field" = $TimeStampField;
    }

    $response = Invoke-WebRequest -Uri $uri -Method $method -ContentType $contentType -Headers $headers -Body $body -UseBasicParsing
    return $response.StatusCode

}

# Submit the data to the API endpoint
Post-LogAnalyticsData -customerId $CustomerId -sharedKey $SharedKey -body ([System.Text.Encoding]::UTF8.GetBytes($jsonResults)) -logType $logType  
# Write an information log with the current time.
Write-Host "Posted to Log Analytics"

Post-APICall -content $Content -headers $Headers
Write-Host "Posted to API Management"


