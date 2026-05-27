param(
    [string]$BaseUrl = "http://localhost:8080",
    [int]$RepositoryId = 101,
    [string]$RepositoryName = "SmokeRepo",
    [switch]$AsJson
)

$ErrorActionPreference = "Stop"

function New-Sha256Hex {
    param([byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hashBytes = $sha.ComputeHash($Bytes)
    }
    finally {
        $sha.Dispose()
    }

    return [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToLowerInvariant()
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw "ASSERT FAILED: $Message"
    }
}

function Invoke-WebRequestCompat {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][hashtable]$Headers
    )

    if ($PSVersionTable.PSVersion.Major -ge 6) {
        return Invoke-WebRequest -Method $Method -Uri $Uri -Headers $Headers
    }

    return Invoke-WebRequest -Method $Method -UseBasicParsing -Uri $Uri -Headers $Headers
}

$timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$username = "smoke_$timestamp"
$password = "SmokeTest#12345"

$registerBody = @{
    username = $username
    password = $password
} | ConvertTo-Json

$register = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/register" -ContentType "application/json" -Body $registerBody
Assert-True ($register.ok -eq $true) "register response was not ok"
Assert-True (-not [string]::IsNullOrWhiteSpace($register.accessToken)) "register did not return access token"
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$register.expiresAtUtc)) "register did not return expiresAtUtc"

$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/login" -ContentType "application/json" -Body $registerBody
Assert-True ($login.ok -eq $true) "login response was not ok"
Assert-True (-not [string]::IsNullOrWhiteSpace($login.accessToken)) "login did not return access token"
Assert-True (-not [string]::IsNullOrWhiteSpace([string]$login.expiresAtUtc)) "login did not return expiresAtUtc"

$headers = @{ Authorization = "Bearer $($login.accessToken)"; "X-Veyra-Sync-Protocol" = "1" }
$listBefore = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/sync/repositories" -Headers $headers

$contentText = "Hello cloud snapshot $(Get-Date -Format o)"
$contentBytes = [System.Text.Encoding]::UTF8.GetBytes($contentText)
$contentSha = New-Sha256Hex -Bytes $contentBytes
$blockHash = "sha256-$contentSha"

$snapshotId = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$nowIso = [DateTime]::UtcNow.ToString("o")

$pushPayload = @{
    repository = @{
        id = $RepositoryId
        name = $RepositoryName
        description = "e2e smoke"
    }
    snapshot = @{
        id = $snapshotId
        title = "smoke_$snapshotId"
        trigger = "manual"
        createdAt = $nowIso
        totalEntries = 1
        fileEntries = 1
        directoryEntries = 0
        totalFileBytes = $contentBytes.Length
        payloadSha256 = $contentSha
    }
    entries = @(
        @{
            relativePath = "docs/hello.txt"
            parentRelativePath = "docs"
            name = "hello.txt"
            isDirectory = $false
            extension = ".txt"
            sizeBytes = $contentBytes.Length
            lastWriteUtc = $nowIso
            contentHashSha256 = $contentSha
        }
    )
    fileVersions = @(
        @{
            relativePath = "docs/hello.txt"
            fileVersionId = 1
            contentHashSha256 = $contentSha
            sizeBytes = $contentBytes.Length
            isDeletionMarker = $false
            createdAt = $nowIso
            blocks = @(
                @{
                    sequence = 0
                    blockHash = $blockHash
                    lengthBytes = $contentBytes.Length
                    storedSizeBytes = $contentBytes.Length
                }
            )
        }
    )
} | ConvertTo-Json -Depth 20

$pushHeadersPhase1 = @{}
$headers.GetEnumerator() | ForEach-Object { $pushHeadersPhase1[$_.Key] = $_.Value }
$pushHeadersPhase1["X-Idempotency-Key"] = "smoke-$snapshotId-phase1"

$pushBeforeBlock = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/sync/repositories/$RepositoryId/snapshots" -Headers $pushHeadersPhase1 -ContentType "application/json" -Body $pushPayload
Assert-True ($pushBeforeBlock.ok -eq $true) "first snapshot push failed"
Assert-True ((@($pushBeforeBlock.missingBlockHashes).Count -ge 1)) "first push should report missing block hashes"

$putBlock = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/sync/blocks/$blockHash" -Headers $headers -ContentType "application/octet-stream" -Body $contentBytes
Assert-True ($putBlock.ok -eq $true) "block upload failed"

$pushHeadersPhase2 = @{}
$headers.GetEnumerator() | ForEach-Object { $pushHeadersPhase2[$_.Key] = $_.Value }
$pushHeadersPhase2["X-Idempotency-Key"] = "smoke-$snapshotId-phase2"

$pushAfterBlock = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/sync/repositories/$RepositoryId/snapshots" -Headers $pushHeadersPhase2 -ContentType "application/json" -Body $pushPayload
Assert-True ($pushAfterBlock.ok -eq $true) "second snapshot push failed"
Assert-True ((@($pushAfterBlock.missingBlockHashes).Count -eq 0)) "second push should have zero missing block hashes"

$headBlock = Invoke-WebRequestCompat -Method "Head" -Uri "$BaseUrl/api/sync/blocks/$blockHash" -Headers $headers
Assert-True ($headBlock.StatusCode -eq 200) "HEAD /blocks did not return 200"

$getBlock = Invoke-WebRequestCompat -Method "Get" -Uri "$BaseUrl/api/sync/blocks/$blockHash" -Headers $headers
Assert-True ($getBlock.StatusCode -eq 200) "GET /blocks did not return 200"
Assert-True ($getBlock.RawContentLength -eq $contentBytes.Length) "GET /blocks returned unexpected size"

$latest = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/sync/repositories/$RepositoryId/latest" -Headers $headers
Assert-True ($latest.ok -eq $true) "latest snapshot query failed"
Assert-True ($latest.snapshot.id -eq $snapshotId) "latest snapshot id mismatch"

$listAfter = Invoke-RestMethod -Method Get -Uri "$BaseUrl/api/sync/repositories" -Headers $headers

$result = [ordered]@{
    username = $username
    repository_id = $RepositoryId
    snapshot_id = $snapshotId
    register_ok = $register.ok
    login_ok = $login.ok
    initial_repository_count = @($listBefore.repositories).Count
    first_push_missing = @($pushBeforeBlock.missingBlockHashes)
    uploaded_block_hash = $blockHash
    uploaded_block_size = $putBlock.size_bytes
    second_push_missing = @($pushAfterBlock.missingBlockHashes)
    latest_ok = $latest.ok
    latest_entries = @($latest.entries).Count
    latest_file_versions = @($latest.fileVersions).Count
    final_repository_count = @($listAfter.repositories).Count
}

if ($AsJson) {
    $result | ConvertTo-Json -Depth 20
}
else {
    "Cloud API smoke test passed."
    "User: $($result.username)"
    "RepositoryId: $($result.repository_id), SnapshotId: $($result.snapshot_id)"
    "Missing blocks before upload: $(@($result.first_push_missing).Count)"
    "Missing blocks after upload:  $(@($result.second_push_missing).Count)"
    "Latest entries: $($result.latest_entries), versions: $($result.latest_file_versions)"
    "Repositories visible after sync: $($result.final_repository_count)"
}

