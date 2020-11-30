
$resourceGroupName = "correlation_test_rg" 
$serverName = "correlationdemodb" 
$databaseName = "correlationdemodb" 
$WorkspaceResourceId = "/subscriptions/ce8f0ca1-212c-4572-9e98-ef56aaf20013/resourceGroups/correlation_test_rg/providers/Microsoft.OperationalInsights/workspaces/correlationloganalytics"

# get AZ
Write-Host Get AZ Module
Get-InstalledModule -Name "Az"


# Set an auditing policy
Set-AzSqlDatabaseAudit -LogAnalyticsTargetState Enabled -WorkspaceResourceId $WorkspaceResourceId `
    -ResourceGroupName $resourceGroupName `
    -ServerName $serverName `
    -DatabaseName $databaseName `
    

    Get-AzSqlDatabaseAudit `
    -ResourceGroupName $resourceGroupName `
    -ServerName $serverName `
    -DatabaseName $databaseName 
    