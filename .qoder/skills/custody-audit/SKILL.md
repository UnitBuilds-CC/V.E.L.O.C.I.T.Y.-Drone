# Custody Audit

## Description
Audit the custody trail integrity and verify hash-chain correctness. Use when investigating potential custody chain breaks, verifying audit trail authenticity, or debugging custody-related issues.

## When to Use
- Investigating custody chain validation failures
- Verifying audit trail integrity after suspected tampering
- Debugging custody record persistence issues
- Reviewing custody trail for compliance or forensic analysis
- Testing custody system after code changes

## Audit Procedure

### Step 1: Verify Local Chain Integrity

Check the local custody log file for chain consistency:

```powershell
# Load and verify custody chain
$custodyPath = "$env:APPDATA\velocity-drone\custody\drone-custody.jsonl"
$records = Get-Content $custodyPath | ConvertFrom-Json

$prevHash = $null
$broken = @()

foreach ($record in $records) {
    if ($prevHash -and $record.PrevHash -ne $prevHash) {
        $broken += $record.Sequence
    }
    
    # Verify content hash
    $computedHash = $record | Select-Object DroneId, Sequence, EventType, Timestamp, Data | 
                    ConvertTo-Json -Compress | 
                    ForEach-Object { 
                        $sha256 = [System.Security.Cryptography.SHA256]::Create()
                        $bytes = [System.Text.Encoding]::UTF8.GetBytes($_)
                        $hash = $sha256.ComputeHash($bytes)
                        [BitConverter]::ToString($hash).Replace("-", "").ToLower()
                    }
    
    if ($computedHash -ne $record.ContentHash) {
        Write-Warning "Content hash mismatch at sequence $($record.Sequence)"
    }
    
    $prevHash = $record.ContentHash
}

if ($broken.Count -eq 0) {
    Write-Host "Chain integrity: VALID ($($records.Count) records)"
} else {
    Write-Warning "Chain broken at sequences: $($broken -join ', ')"
}
```

### Step 2: Check Sequence Monotonicity

Verify sequence numbers are strictly increasing:

```powershell
$sequences = $records | ForEach-Object { $_.Sequence }
$gaps = @()

for ($i = 1; $i -lt $sequences.Count; $i++) {
    $expected = $sequences[$i-1] + 1
    $actual = $sequences[$i]
    if ($actual -ne $expected) {
        $gaps += "Gap at $($sequences[$i-1]) → $actual (expected $expected)"
    }
}

if ($gaps.Count -eq 0) {
    Write-Host "Sequence monotonicity: VALID"
} else {
    Write-Warning "Sequence gaps detected:"
    $gaps | ForEach-Object { Write-Warning "  $_" }
}
```

### Step 3: Verify Correlation Tracking

Check cross-machine correlation IDs:

```powershell
$correlations = $records | 
    Where-Object { $_.CorrelationId } | 
    Group-Object CorrelationId |
    Select-Object Name, Count, @{N='FirstSeen';E={$_.Group[0].Timestamp}}, @{N='LastSeen';E={$_.Group[-1].Timestamp}}

Write-Host "Active correlations: $($correlations.Count)"
$correlations | Format-Table -AutoSize
```

### Step 4: Query CustodyServer

If CustodyServer is running, verify server-side chain:

```powershell
# Query recent records
$response = Invoke-RestMethod -Uri "http://localhost:5050/custody?drone=my-drone&from=$(Get-Date -AsUTC -Format 'o')"

# Verify server chain
$serverRecords = $response.Records
$valid = $true

for ($i = 1; $i -lt $serverRecords.Count; $i++) {
    if ($serverRecords[$i].PrevHash -ne $serverRecords[$i-1].ContentHash) {
        Write-Warning "Server chain broken at sequence $($serverRecords[$i].Sequence)"
        $valid = $false
    }
}

if ($valid) {
    Write-Host "Server chain integrity: VALID"
}
```

## Common Issues

### Chain Broken After Crash

**Symptom:** `VerifyChain()` returns false after agent crash
**Cause:** Incomplete record write during crash
**Fix:** 
```csharp
// LoadPersistedRecords() detects and truncates incomplete records
custodyLogger.LoadPersistedRecords();  // Auto-recovery
```

### Sequence Gap Detected

**Symptom:** Missing sequence numbers in custody log
**Cause:** Concurrent writes or manual file editing
**Fix:** Check for multiple agents writing to same file

### Content Hash Mismatch

**Symptom:** Content hash doesn't match computed hash
**Cause:** Record data modified after creation (tampering)
**Action:** This indicates potential security incident — investigate immediately

## Validation Checklist

- [ ] Local chain hashes match computed hashes
- [ ] Sequence numbers are strictly monotonic
- [ ] PrevHash links are consistent
- [ ] Correlation IDs are properly formatted (`corr-{guid}`)
- [ ] Timestamps are in UTC and monotonically increasing
- [ ] Server chain matches local chain (if CustodyServer used)

## Related Commands

```bash
# Run custody-related tests
dotnet test tests/Drone.Tests/Drone.Tests.csproj --filter "Custody"

# View custody log
Get-Content $env:DRONE_CUSTODY_PATH | Select-Object -Last 10

# Query CustodyServer API
curl "http://localhost:5050/custody?drone=my-drone&limit=10"
```
