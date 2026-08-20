# Exercises the installer's decision logic against throwaway files.
# Nothing here touches a real Steam install: every case runs against a
# temporary copy handed over with --pak.
#
#   powershell -ExecutionPolicy Bypass -File selftest.ps1

$ErrorActionPreference = 'Stop'
$exe    = Join-Path $PSScriptRoot 'PippiHotFix-Installer.exe'
$stock  = Join-Path $PSScriptRoot '..\backup\Pippi.4.0.6.original.pak'
$fixed  = Join-Path $PSScriptRoot '..\out\Pippi.pak'
$work   = Join-Path ([IO.Path]::GetTempPath()) ("phf_test_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

$pass = 0; $fail = 0

function Check($name, $pak, $wantVerdict, $wantExit) {
    $args = @('/check')
    if ($pak) { $args += @('--pak', $pak) }
    $out  = & $exe @args 2>&1 | Out-String
    $code = $LASTEXITCODE
    $verdictLine = ($out -split "`n" | Where-Object { $_ -match 'verdict|not found at|could not read it' } | Select-Object -First 1)
    if (-not $verdictLine) { $verdictLine = '<none>' }
    $okV = $verdictLine -match [regex]::Escape($wantVerdict)
    $okE = ($code -eq $wantExit)
    if ($okV -and $okE) {
        $script:pass++
        "  PASS  {0,-22} exit={1}  {2}" -f $name, $code, $verdictLine.Trim()
    } else {
        $script:fail++
        "  FAIL  {0,-22} exit={1} (want {2})  got: {3}" -f $name, $code, $wantExit, $verdictLine.Trim()
    }
}

try {
    "Test matrix (all against temporary copies):"

    # the unpatched Workshop file
    $p = Join-Path $work 'stock\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    Copy-Item $stock $p
    Check 'stock file' $p 'NEEDS FIX' 1

    # the current hot fix
    $p = Join-Path $work 'fixed\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    Copy-Item $fixed $p
    Check 'current hot fix' $p 'UP TO DATE' 0

    # something we have never published - stands in for an official update
    $p = Join-Path $work 'foreign\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    $bytes = New-Object byte[] 1024
    (New-Object Random 42).NextBytes($bytes)
    [IO.File]::WriteAllBytes($p, $bytes)
    Check 'unknown version' $p 'FOREIGN' 1

    # a file the size of the stock pak but different contents: must NOT be
    # mistaken for the broken file (the size heuristic used to do exactly that)
    $p = Join-Path $work 'sizecollide\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    $fs = [IO.File]::Create($p); $fs.SetLength(45970706); $fs.WriteByte(1); $fs.Close()
    Check 'stock size, not stock' $p 'FOREIGN' 1

    # a path that does not exist
    Check 'missing file' (Join-Path $work 'nope\Pippi.pak') 'not found at' 3

    # locked so it cannot be read
    $p = Join-Path $work 'locked\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    Copy-Item $stock $p
    $lock = [IO.File]::Open($p, 'Open', 'Read', 'None')
    try   { Check 'locked file' $p 'could not read it' 3 }
    finally { $lock.Close() }

    # read-only must still be readable and classified
    $p = Join-Path $work 'readonly\Pippi.pak'
    New-Item -ItemType Directory -Path (Split-Path $p) | Out-Null
    Copy-Item $stock $p
    Set-ItemProperty $p -Name IsReadOnly -Value $true
    Check 'read-only file' $p 'NEEDS FIX' 1
    Set-ItemProperty $p -Name IsReadOnly -Value $false

    ""
    "  {0} passed, {1} failed" -f $pass, $fail
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
if ($fail -gt 0) { exit 1 }
