param(
    [string]$ApiBase = 'http://127.0.0.1:5097',
    [string]$CredentialsPath = '.data/renderer-integration/.data/demo/credentials.json'
)

$ErrorActionPreference = 'Stop'
if (-not ([Uri]$ApiBase).IsLoopback) { throw 'Smoke test ini hanya boleh dijalankan ke API lokal dengan database pengujian.' }
$credentials = Get-Content -Raw -LiteralPath $CredentialsPath | ConvertFrom-Json -AsHashtable
$email = 'pengaju-himpunan-informatika-sains-data@demo.signit.example'
$login = Invoke-RestMethod "$ApiBase/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{
    email = $email; password = $credentials[$email]
} | ConvertTo-Json)
$headers = @{ Authorization = 'Bearer ' + $login.accessToken }
try {
    $templates = Invoke-RestMethod "$ApiBase/api/v1/templates" -Headers $headers
    $org = (Invoke-RestMethod "$ApiBase/api/v1/routing/organizations" -Headers $headers) | Select-Object -First 1
    $candidates = Invoke-RestMethod "$ApiBase/api/v1/routing/organizations/$($org.id)/candidates" -Headers $headers
    $committee = $candidates | Where-Object positionCode -eq 'Ketupel'
    $chair = $candidates | Where-Object positionCode -eq 'KetuaOrganisasi'
    $facility = (Invoke-RestMethod "$ApiBase/api/v1/routing/facilities" -Headers $headers) | Where-Object code -eq 'PS'
    $resource = (Invoke-RestMethod "$ApiBase/api/v1/routing/resources?facilityId=$($facility.id)" -Headers $headers) | Select-Object -First 1
    $results = @()
    foreach ($template in $templates) {
        $fields = @{}
        foreach ($field in $template.fields | Where-Object valueSource -eq 'user') { $fields[$field.key] = 'Uji API ' + $field.label }
        $draft = Invoke-RestMethod "$ApiBase/api/v1/letters/drafts" -Headers $headers -Method Post -ContentType 'application/json' -Body (@{
            typeId = $template.typeId; title = 'Uji API Renderer ' + $template.typeId; fields = $fields
        } | ConvertTo-Json -Depth 8)
        $parameters = @{
            expectedVersion = $draft.version; expectedRevisionId = $draft.revisionId; expectedContentHash = $draft.contentHash
            organizationId = $org.id; committeeChairId = $committee.userId; organizationChairId = $chair.userId; resourceId = $null
        }
        if ($template.typeId -eq 'peminjaman-ruangan') { $parameters.resourceId = $resource.id }
        $queued = Invoke-WebRequest "$ApiBase/api/v1/letters/$($draft.id)/preview" -Headers $headers -Method Post -ContentType 'application/json' -Body ($parameters | ConvertTo-Json)
        $preview = $queued.Content | ConvertFrom-Json
        for ($poll = 0; $poll -lt 30 -and $preview.state -ne 'Ready'; $poll++) {
            if ($preview.state -in @('Failed', 'Superseded')) { throw ('Preview failed: ' + $preview.errorCode) }
            Start-Sleep -Milliseconds 300
            $preview = Invoke-RestMethod "$ApiBase/api/v1/letters/$($draft.id)/previews/$($preview.jobId)" -Headers $headers
        }
        if ($preview.state -ne 'Ready') { throw 'Preview timeout.' }
        $download = Invoke-WebRequest "$ApiBase$($preview.downloadUrl)" -Headers $headers
        if ($download.StatusCode -ne 200 -or $download.Headers['Cache-Control'] -notcontains 'no-store') { throw 'Download contract invalid.' }
        $submit = $parameters.Clone()
        $submit.reviewDocumentId = $preview.reviewDocumentId
        $submit.expectedReviewHash = $preview.reviewHash
        $submit.slots = @($preview.slots)
        $submitHeaders = @{ Authorization = $headers.Authorization; 'Idempotency-Key' = 'api-smoke-' + $draft.id }
        $submission = Invoke-RestMethod "$ApiBase/api/v1/letters/$($draft.id)/submit" -Headers $submitHeaders -Method Post -ContentType 'application/json' -Body ($submit | ConvertTo-Json -Depth 8)
        $results += [PSCustomObject]@{
            Template = $template.typeId; QueueStatus = $queued.StatusCode; Preview = $preview.state
            PdfStatus = $download.StatusCode; Slots = $preview.slots.Count; Submission = $submission.status
        }
    }
    $results | Format-Table -AutoSize
}
finally {
    Invoke-RestMethod "$ApiBase/api/v1/auth/logout" -Method Post -Headers $headers | Out-Null
}
