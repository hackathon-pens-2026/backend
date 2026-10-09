param(
    [string]$ApiBase = 'http://127.0.0.1:5097',
    [string]$CredentialsPath = '.data/renderer-integration/.data/demo/credentials.json'
)

# Run only against a local API configured with the isolated *_tests database and email disabled.
$ErrorActionPreference = 'Stop'
if (-not ([Uri]$ApiBase).IsLoopback) { throw 'Workflow smoke test hanya boleh memakai API lokal/database uji.' }
$workflowCredentials = Get-Content -Raw -LiteralPath $CredentialsPath | ConvertFrom-Json -AsHashtable
$workflowSessions = @()
function Login-Workflow([string]$email) {
    $session = Invoke-RestMethod "$ApiBase/api/v1/auth/login" -Method Post -ContentType 'application/json' -Body (@{
        email = $email; password = $workflowCredentials[$email]
    } | ConvertTo-Json)
    $headers = @{ Authorization = 'Bearer ' + $session.accessToken }
    $script:workflowSessions += $headers
    return $headers
}
function Call-Workflow([string]$method, [string]$path, [hashtable]$headers, $payload = $null, [string]$key = '') {
    $callHeaders = $headers.Clone()
    if ($key) { $callHeaders['Idempotency-Key'] = $key }
    $arguments = @{ Uri = "$ApiBase$path"; Method = $method; Headers = $callHeaders }
    if ($null -ne $payload) { $arguments.ContentType = 'application/json'; $arguments.Body = $payload | ConvertTo-Json -Depth 10 }
    return Invoke-RestMethod @arguments
}
function Get-WorkflowPreview($draft, $selection, $headers) {
    $previewRequest = $selection.Clone()
    $previewRequest.expectedVersion = $draft.version
    $previewRequest.expectedRevisionId = $draft.revisionId
    $previewRequest.expectedContentHash = $draft.contentHash
    $preview = Call-Workflow Post "/api/v1/letters/$($draft.id)/preview" $headers $previewRequest
    for ($i = 0; $i -lt 40 -and $preview.state -ne 'Ready'; $i++) {
        if ($preview.state -in @('Failed', 'Superseded')) { throw 'Workflow preview failed.' }
        Start-Sleep -Milliseconds 300
        $preview = Call-Workflow Get "/api/v1/letters/$($draft.id)/previews/$($preview.jobId)" $headers
    }
    if ($preview.state -ne 'Ready') { throw 'Workflow preview timeout.' }
    $submit = $previewRequest.Clone()
    $submit.reviewDocumentId = $preview.reviewDocumentId
    $submit.expectedReviewHash = $preview.reviewHash
    $submit.slots = @($preview.slots)
    return $submit
}
try {
    $owner = Login-Workflow 'pengaju-himpunan-informatika-sains-data@demo.signit.example'
    $committee = Login-Workflow 'ketupel@demo.signit.example'
    $chair = Login-Workflow 'ketua@demo.signit.example'
    $org = (Call-Workflow Get '/api/v1/routing/organizations' $owner) | Select-Object -First 1
    $candidates = Call-Workflow Get "/api/v1/routing/organizations/$($org.id)/candidates" $owner
    $selectedCommittee = $candidates | Where-Object positionCode -eq 'Ketupel'
    $selectedChair = $candidates | Where-Object positionCode -eq 'KetuaOrganisasi'
    $template = (Call-Workflow Get '/api/v1/templates' $owner) | Where-Object typeId -eq 'proposal'
    $fields = @{}
    foreach ($field in $template.fields | Where-Object valueSource -eq 'user') { $fields[$field.key] = 'Workflow QA ' + $field.label }
    $draft = Call-Workflow Post '/api/v1/letters/drafts' $owner @{ typeId = 'proposal'; title = 'Workflow API QA'; fields = $fields }
    $selection = @{ organizationId = $org.id; committeeChairId = $selectedCommittee.userId; organizationChairId = $selectedChair.userId; resourceId = $null }
    $submit = Get-WorkflowPreview $draft $selection $owner
    $submitted = Call-Workflow Post "/api/v1/letters/$($draft.id)/submit" $owner $submit "submit-$($draft.id)"
    $tasks = Call-Workflow Get "/api/v1/letters/$($draft.id)/workflow" $owner
    $first = $tasks.tasks | Where-Object order -eq 1
    $task = Call-Workflow Get "/api/v1/tasks/$($first.id)" $committee
    $queue = Call-Workflow Get '/api/v1/tasks?page=1&pageSize=100' $committee
    if (-not ($queue.items | Where-Object id -eq $first.id)) { throw 'Active task missing from authorized queue.' }
    $pdf = Invoke-WebRequest "$ApiBase$($task.documentUrl)" -Headers $committee
    if ($pdf.StatusCode -ne 200) { throw 'Review PDF download failed.' }
    $action = @{ expectedRevisionId = $task.revisionId; expectedContentHash = $task.contentHash; expectedTaskVersion = $task.version; comment = 'Ditinjau pada uji API' }
    $signed = Call-Workflow Post "/api/v1/tasks/$($task.id)/sign" $committee $action "sign-$($task.id)"
    $replayed = Call-Workflow Post "/api/v1/tasks/$($task.id)/sign" $committee $action "sign-$($task.id)"
    if ($signed.evidenceId -ne $replayed.evidenceId) { throw 'Duplicate signing evidence.' }
    $tasks = Call-Workflow Get "/api/v1/letters/$($draft.id)/workflow" $chair
    $second = $tasks.tasks | Where-Object order -eq 2
    $revision = @{ expectedRevisionId = $second.revisionId; expectedContentHash = $second.contentHash; expectedTaskVersion = $second.version; reason = 'Perbaiki isi kegiatan' }
    $requested = Call-Workflow Post "/api/v1/tasks/$($second.id)/request-revision" $chair $revision "revision-$($second.id)"
    if ($requested.letterStatus -ne 'NeedsRevision') { throw 'Revision state invalid.' }
    $current = Call-Workflow Get "/api/v1/letters/$($draft.id)" $owner
    $fields['nama_kegiatan'] = 'Kegiatan versi revisi'
    $edited = Call-Workflow Put "/api/v1/letters/$($draft.id)/draft" $owner @{
        expectedVersion = $current.version; expectedRevisionId = $current.revisionId; expectedContentHash = $current.contentHash
        title = 'Workflow API revisi'; fields = $fields
    } "edit-$($draft.id)"
    $resubmit = Get-WorkflowPreview $edited $selection $owner
    $resubmitted = Call-Workflow Post "/api/v1/letters/$($draft.id)/resubmit" $owner $resubmit "resubmit-$($draft.id)"
    if ($submitted.number -ne $resubmitted.number) { throw 'Resubmit changed the letter number.' }
    $tasks = Call-Workflow Get "/api/v1/letters/$($draft.id)/workflow" $committee
    $newFirst = $tasks.tasks | Where-Object order -eq 1
    if ($newFirst.id -eq $first.id -or $newFirst.status -ne 'Active') { throw 'New revision must restart the complete chain.' }
    $deferRequest = @{ expectedRevisionId = $newFirst.revisionId; expectedContentHash = $newFirst.contentHash; expectedTaskVersion = $newFirst.version; reason = 'Tunda untuk tinjauan'; until = [DateTimeOffset]::UtcNow.AddHours(2).ToString('O') }
    $deferred = Call-Workflow Post "/api/v1/tasks/$($newFirst.id)/defer" $committee $deferRequest "defer-$($newFirst.id)"
    $deferRequest.expectedTaskVersion = $deferred.taskVersion
    $resumed = Call-Workflow Post "/api/v1/tasks/$($newFirst.id)/resume" $committee $deferRequest "resume-$($newFirst.id)"
    if ($resumed.status -ne 'Active') { throw 'Resume did not restore the same task.' }
    $current = Call-Workflow Get "/api/v1/letters/$($draft.id)" $owner
    $cancelled = Call-Workflow Post "/api/v1/letters/$($draft.id)/cancel" $owner @{
        expectedVersion = $current.version; expectedRevisionId = $current.revisionId; expectedContentHash = $current.contentHash; reason = 'Selesai uji API'
    } "cancel-$($draft.id)"
    if ($cancelled.status -ne 'Cancelled') { throw 'Cancellation state invalid.' }
    [PSCustomObject]@{ Queue = 'OK'; ReviewPdf = $pdf.StatusCode; SignReplay = 'OK'; Revision = 'OK'; ResubmitRestart = 'OK'; DeferResume = 'OK'; Cancel = 'OK' } | Format-List
}
finally {
    foreach ($sessionHeaders in $workflowSessions) {
        try { Call-Workflow Post '/api/v1/auth/logout' $sessionHeaders | Out-Null } catch { Write-Warning 'Test session logout failed.' }
    }
}
