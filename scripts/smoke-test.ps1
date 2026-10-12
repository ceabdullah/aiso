<#
.SYNOPSIS
    On-premises smoke test for the AISO platform.

.DESCRIPTION
    Verifies, in order:
      1. .NET 10 SDK and the workspace folder layout
      2. claude.exe is installed and answers through the LiteLLM gateway (headless JSON mode)
      3. EF Core migrations + seeding succeed against the configured database (runs the migration node)
      4. The expected AISO_* tables exist and are seeded (SQL Server only; uses Invoke-Sqlcmd if present)

    Exit code 0 = all checks passed.

.EXAMPLE
    $env:ConnectionStrings__aisodb = "Server=localhost,1433;Database=aisodb;User Id=sa;Password=***;TrustServerCertificate=true"
    $env:Aiso__Llm__ApiKey        = "<gateway key>"
    .\scripts\smoke-test.ps1 -GatewayUrl https://teb.com.tr
#>
[CmdletBinding()]
param(
    [string] $Workspace   = 'C:\PROJECTS\LOCAL LLM DEMO',
    [string] $GatewayUrl  = 'https://teb.com.tr',
    [string] $Model       = 'claude-3-5-sonnet-20241022',
    [string] $ClaudeExe   = 'claude.exe',
    [ValidateSet('SqlServer', 'Oracle')] [string] $Provider = 'SqlServer',
    [switch] $SkipClaude,
    [switch] $SkipDatabase
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$repoRoot = Split-Path -Parent $PSScriptRoot

function Step([string] $name, [scriptblock] $body) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    try { & $body; Write-Host "PASS  $name" -ForegroundColor Green }
    catch { Write-Host "FAIL  $name :: $($_.Exception.Message)" -ForegroundColor Red; $failures.Add($name) }
}

Step '.NET SDK' {
    $sdks = & dotnet --list-sdks
    if (-not ($sdks -match '^10\.')) { throw ".NET 10 SDK not found. Installed: $($sdks -join ', ')" }
    $sdks | Where-Object { $_ -match '^10\.' } | ForEach-Object { Write-Host "  $_" }
}

Step 'Workspace layout' {
    if (-not (Test-Path $Workspace)) { throw "Workspace '$Workspace' does not exist." }
    foreach ($dir in 'data', 'reports') {
        $p = Join-Path $Workspace $dir
        if (-not (Test-Path $p)) { New-Item -ItemType Directory -Path $p | Out-Null; Write-Host "  created $p" }
    }
    if (-not (Test-Path (Join-Path $Workspace 'CLAUDE.md'))) { throw "CLAUDE.md missing from workspace root — claude.exe will not know the output contract." }
    $probe = Join-Path $Workspace 'reports\.write-test'
    Set-Content -Path $probe -Value 'ok'; Remove-Item $probe
    Write-Host "  data/, reports/ writable; CLAUDE.md present"
}

if (-not $SkipClaude) {
    Step 'claude.exe via LiteLLM gateway' {
        if (-not $env:Aiso__Llm__ApiKey) { throw 'Set $env:Aiso__Llm__ApiKey to the gateway key.' }
        $version = & $ClaudeExe --version 2>&1
        Write-Host "  claude version: $version"

        # Same environment contract as ClaudeCliOrchestrator.BuildStartInfo.
        $env:ANTHROPIC_BASE_URL = $GatewayUrl.TrimEnd('/')
        $env:ANTHROPIC_API_KEY = $env:Aiso__Llm__ApiKey
        foreach ($v in 'ANTHROPIC_MODEL', 'ANTHROPIC_SMALL_FAST_MODEL', 'ANTHROPIC_DEFAULT_HAIKU_MODEL', 'ANTHROPIC_DEFAULT_SONNET_MODEL', 'ANTHROPIC_DEFAULT_OPUS_MODEL') {
            Set-Item "env:$v" $Model
        }
        $env:CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1'

        Push-Location $Workspace
        try {
            $raw = 'Reply with exactly the word OK.' | & $ClaudeExe -p --output-format json --model $Model --max-turns 1 --allowedTools Read 2>&1
        } finally { Pop-Location }

        $json = ($raw | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') } | Select-Object -Last 1) | ConvertFrom-Json
        if ($json.is_error -or $json.subtype -ne 'success') { throw "CLI returned error: $raw" }
        Write-Host "  result: $($json.result)  (session $($json.session_id), $($json.num_turns) turn(s))"
    }
}

if (-not $SkipDatabase) {
    Step "Migrations + seed ($Provider)" {
        if (-not $env:ConnectionStrings__aisodb) { throw 'Set $env:ConnectionStrings__aisodb.' }
        $env:Aiso__Database__Provider = $Provider
        & dotnet run --project (Join-Path $repoRoot 'src\Aiso.MigrationService') -c Release
        if ($LASTEXITCODE -ne 0) { throw "Migration node exited with $LASTEXITCODE." }
        # Second run proves idempotency (0 pending migrations, 0 seed rows).
        & dotnet run --project (Join-Path $repoRoot 'src\Aiso.MigrationService') -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw "Second (idempotent) migration run exited with $LASTEXITCODE." }
    }

    if ($Provider -eq 'SqlServer' -and (Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue)) {
        Step 'Schema verification' {
            $rows = Invoke-Sqlcmd -ConnectionString $env:ConnectionStrings__aisodb -Query @"
SELECT t.name AS TableName, SUM(p.rows) AS Rows
FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
WHERE t.name LIKE 'AISO[_]%' GROUP BY t.name ORDER BY t.name
"@
            $rows | Format-Table -AutoSize | Out-String | Write-Host
            $expected = 'AISO_AUDIT_LOG', 'AISO_EF_MIGRATIONS', 'AISO_REMEDIATION_PLAN', 'AISO_ROLE_MAPPING', 'AISO_SYSTEM_SETTING', 'AISO_TARGET_PROFILE', 'AISO_TEST_PROMPT'
            $missing = $expected | Where-Object { $_ -notin $rows.TableName }
            if ($missing) { throw "Missing tables: $($missing -join ', ')" }
            if (($rows | Where-Object TableName -eq 'AISO_SYSTEM_SETTING').Rows -lt 7) { throw 'Settings were not seeded.' }
        }
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "SMOKE TEST FAILED: $($failures -join '; ')" -ForegroundColor Red
    exit 1
}
Write-Host 'SMOKE TEST PASSED' -ForegroundColor Green
exit 0
