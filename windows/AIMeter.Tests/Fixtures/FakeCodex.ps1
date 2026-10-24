# SPDX-FileCopyrightText: 2026 Matt Curfman and AI Meter contributors
# SPDX-License-Identifier: Apache-2.0

param([string] $Mode, [string] $Directory)
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $Directory 'pid'), [string] $PID)

function Read-Request([string] $Method) {
    $line = [Console]::ReadLine()
    if ($null -eq $line) { throw 'Unexpected end of input.' }
    $message = ConvertFrom-Json $line
    if ($message.method -ne $Method) { throw "Expected $Method." }
    return $message
}

function Reply($Request, $Result) {
    [Console]::WriteLine((ConvertTo-Json -Depth 12 -Compress @{ id = $Request.id; result = $Result }))
}

function Finish {
    if ($null -ne [Console]::ReadLine()) { throw 'Expected stdin to close.' }
    [IO.File]::WriteAllText((Join-Path $Directory 'finished'), 'finished')
}

# Fill stderr beyond the pipe buffer before answering, to catch missing concurrent draining.
[Console]::Error.Write(('discarded-fixture-stderr-' * 5000))
$initialize = Read-Request 'initialize'
if ($initialize.params.clientInfo.name -ne 'ai_meter') { throw 'Missing client identity.' }
if ($Mode -eq 'early-exit') { exit 0 }
if ($Mode -eq 'invalid-json') {
    [Console]::WriteLine('{fixture-secret-invalid-json')
    Finish
    exit 0
}
if ($Mode -eq 'oversized') {
    [Console]::WriteLine(('x' * 1048577))
    Finish
    exit 0
}
Reply $initialize @{}
$notification = Read-Request 'initialized'
if ($null -ne $notification.id) { throw 'initialized must be a notification.' }
$account = Read-Request 'account/read'
if ($account.params.refreshToken -isnot [bool] -or $account.params.refreshToken -ne $false) {
    throw 'Token refresh must not be forced.'
}
if ($Mode -eq 'pending' -or $Mode -eq 'stubborn') {
    if ($Mode -eq 'stubborn') {
        $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 120'
        ) -WindowStyle Hidden -PassThru
        [IO.File]::WriteAllText((Join-Path $Directory 'child-pid'), [string] $child.Id)
    }
    [IO.File]::WriteAllText((Join-Path $Directory 'ready'), 'ready')
    if ($Mode -eq 'stubborn') { while ($true) { Start-Sleep -Seconds 120 } }
    Finish
    exit 0
}
if ($Mode -eq 'signed-out') {
    Reply $account @{ account = $null }
    Finish
    exit 0
}
if ($Mode -eq 'api-key') {
    Reply $account @{ account = @{ type = 'apiKey' } }
    Finish
    exit 0
}
Reply $account @{ account = @{ type = 'chatgpt' } }
$limits = Read-Request 'account/rateLimits/read'
if ($limits.params.excludeResetCreditDetails -isnot [bool] -or $limits.params.excludeResetCreditDetails -ne $true) {
    throw 'Reset credit details must be excluded.'
}
# Both unrelated notifications and unmatched response ids must be ignored.
[Console]::WriteLine('{"method":"account/updated","params":{"note":"fixture-notification"}}')
[Console]::WriteLine('{"id":999,"result":{"fixture":"unrelated"}}')
if ($Mode -eq 'rpc-error') {
    [Console]::WriteLine((ConvertTo-Json -Compress @{ id = $limits.id; error = @{ message = 'fixture-secret-auth-response' } }))
} elseif ($Mode -eq 'invalid-percent') {
    Reply $limits @{ rateLimits = @{ primary = @{ usedPercent = 'NaN' } } }
} elseif ($Mode -eq 'infinite-percent') {
    [Console]::WriteLine('{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":1e309}}}}')
} elseif ($Mode -eq 'secondary-only') {
    Reply $limits @{ rateLimits = @{ planType = 'pro'; primary = $null; secondary = @{
        usedPercent = 105; windowDurationMins = 1440; resetsAt = 9223372036854775807
    } } }
} elseif ($Mode -eq 'empty') {
    Reply $limits @{ rateLimits = @{ planType = 'pro'; primary = $null; secondary = $null } }
} else {
    Reply $limits @{ rateLimits = @{
        planType = 'pro'
        primary = @{ usedPercent = 21; windowDurationMins = 10080; resetsAt = 1792542244 }
        secondary = @{ usedPercent = 7.5; windowDurationMins = 300; resetsAt = $null }
    } }
}
Finish
