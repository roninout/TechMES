#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Check','Apply')][string]$Mode='Check',
    [ValidatePattern('^$|^[0-9]{1,20}$')][string]$TargetSystemIdentifier='',
    [switch]$PrepareTarget
)

Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$script:EmbeddedInitialStandby = ''

try {
    $setup=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($script:EmbeddedInitialStandby)) | ConvertFrom-Json
    $addresses=@(Get-NetIPAddress -AddressFamily IPv4 | Select-Object -ExpandProperty IPAddress)
    if ($setup.TargetAddress -notin $addresses -or $setup.SourceAddress -in $addresses -or ($setup.ComputerName -ine $env:COMPUTERNAME -and $setup.ComputerName -notin $addresses)) {
        throw ('Run the bundle on the new Standby '+$setup.TargetAddress+'.')
    }
    if ($Mode -eq 'Apply' -and !$TargetSystemIdentifier) { throw 'Run Check first and copy the exact -Mode Apply command it prints.' }

    if ($PrepareTarget -and ($Mode -ne 'Check' -or !$TargetSystemIdentifier)) { throw 'Target preparation requires Check and the verified target identifier.' }

    $operation=[guid]::NewGuid().ToString('N')
    $resultFile=Join-Path $PSScriptRoot ("result-"+$Mode+"-"+$operation+".json")
    $tools=Join-Path $PSScriptRoot 'Tools'
    $options=@{
        Mode=if ($Mode -eq 'Check') { 'CheckInitialStandby' } else { 'InitializeStandby' }
        From=[string]$setup.TargetAddress
        To=[string]$setup.SourceAddress
        ExpectedSystemIdentifier=[string]$setup.SystemIdentifier
        ExpectedTargetSystemIdentifier=$TargetSystemIdentifier
        ServiceName=[string]$setup.ServiceName
        PgBin=[string]$setup.BinDirectory
        DataDirectory=[string]$setup.DataDirectory
        Port=[int]$setup.Port
        OperationId=$operation
        ResultFile=$resultFile
        PrepareInitialTarget=$PrepareTarget
    }
    & (Join-Path $tools 'Switch-PostgreSQL18.ps1') @options
    $code=$LASTEXITCODE
    if (Test-Path -LiteralPath $resultFile) { Write-Host ('Bundle report: '+$resultFile) }
    if ($code -ne 0) { exit $code }

    $result=Get-Content -LiteralPath $resultFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$result.Success) { throw 'No successful report was produced.' }
    if ($Mode -eq 'Check') {
        $targetSid=[string]$result.Data.TargetSystemIdentifier
        if ($result.Data.State -ne 'Ready' -or $targetSid -notmatch '^[0-9]{1,20}$') { throw 'The preflight did not confirm a separate target cluster.' }

        Write-Host ('Primary: '+$setup.SourceAddress+' | New Standby: '+$setup.TargetAddress)
        Write-Host ('Existing target directory will be preserved beside '+$setup.DataDirectory+'.')
        Write-Host "powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup-Standby.ps1 -Mode Apply -TargetSystemIdentifier '$targetSid'" -ForegroundColor Cyan
    }
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
