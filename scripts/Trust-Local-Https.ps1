param([string]$StateDirectory=(Join-Path $env:LOCALAPPDATA 'DeadlockTranslate'),[switch]$ConfirmTrust,[switch]$RemoveTrust,[switch]$Renew,[string]$RuntimeFolder=$PSScriptRoot)
$ErrorActionPreference='Stop'
$taskExe=Join-Path $RuntimeFolder 'DeadlockTranslate.exe'
if($Renew -and $RemoveTrust){throw 'Renew and RemoveTrust cannot be combined.'}
if($Renew){
    $taskHealth=$null
    try {$taskHealth=Invoke-RestMethod http://localhost:17865/health -TimeoutSec 2} catch {}
    if($taskHealth -and $taskHealth.app -eq 'DeadlockTranslate'){throw 'Close the game and stop the translation backend before certificate renewal.'}
    if(-not $ConfirmTrust){
        $taskAnswer=Read-Host 'Renewal backs up the encrypted certificate and creates a new one; new Windows trust confirmation and a full Steam restart are required. Type YES to continue'
        if($taskAnswer -cne 'YES'){Write-Output 'No certificate changed.';exit 2}
    }
}
if(-not $RemoveTrust){
    $taskPrepareFlag=if($Renew){'--renew-https'}else{'--prepare-https'}
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @($taskPrepareFlag,'--data-dir',('"'+$StateDirectory+'"')) -WindowStyle Hidden -PassThru -Wait
    if($taskProcess.ExitCode -ne 0){throw 'Local HTTPS certificate preparation failed.'}
}
$taskPath=Join-Path $StateDirectory 'localhost-certificate.cer'
$taskCert=New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($taskPath)
if(-not $RemoveTrust){
    $taskRunningHealth=$null
    try {$taskRunningHealth=Invoke-RestMethod http://localhost:17865/health -TimeoutSec 2} catch {}
    if($taskRunningHealth -and $taskRunningHealth.certificateThumbprint -and $taskRunningHealth.certificateThumbprint -ne $taskCert.Thumbprint){
        throw 'Running backend uses a different data directory/certificate. Close it and run this installer from the same Windows user/environment. No certificate was trusted.'
    }
}
if($taskCert.Subject -ne 'CN=DeadlockTranslate localhost' -or $taskCert.Subject -ne $taskCert.Issuer){throw 'Unexpected certificate identity.'}
if(-not $RemoveTrust -and $taskCert.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow){throw 'Certificate expired. Use Renew-Local-Https.cmd after stopping the backend.'}
$taskConstraints=@($taskCert.Extensions | Where-Object {$_.Oid.Value -eq '2.5.29.19'})
if($taskConstraints.Count -ne 1 -or $taskConstraints[0].CertificateAuthority){throw 'Expected a non-CA server certificate.'}
Write-Output ('Certificate SHA-1 thumbprint: '+$taskCert.Thumbprint)
Write-Output ('Expires: '+$taskCert.NotAfter.ToString('u'))
if(-not $ConfirmTrust -and -not $Renew){
    $taskAnswer=Read-Host 'This changes the CURRENT USER Windows trusted certificate store for local HTTPS. Type YES to continue'
    if($taskAnswer -cne 'YES'){Write-Output 'No trust-store change.';exit 2}
}
$taskStore=New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser')
$taskStore.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try{
    if($RemoveTrust){
        $taskMatches=$taskStore.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$taskCert.Thumbprint,$false)
        foreach($taskMatch in $taskMatches){$taskStore.Remove($taskMatch)}
        Write-Output 'Removed trust only for this local certificate.'
    }else{
        $taskExisting=$taskStore.Certificates.Find([System.Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint,$taskCert.Thumbprint,$false)
        if($taskExisting.Count){Write-Output 'Certificate already trusted; no duplicate trust installation.'}
        else{$taskStore.Add($taskCert);Write-Output 'Trusted local HTTPS certificate for the current Windows user.'}
        Write-Output 'Fully exit and restart Steam after first-time trust installation so its HTML browser reloads the trust store.'
    }
}finally{$taskStore.Close();$taskCert.Dispose()}
