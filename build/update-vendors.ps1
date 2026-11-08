# wific - refreshes the embedded MAC vendor database (IEEE registry via Wireshark "manuf").
# Copyright (C) 2026 trelowney
# SPDX-License-Identifier: GPL-3.0-or-later
#
# Usage: pwsh build/update-vendors.ps1
# If the download fails, the snapshot already committed in src/Wific/Assets/manuf.gz is kept.

$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\src\Wific\Assets\manuf.gz'
$url = 'https://www.wireshark.org/download/automated/data/manuf'

try {
    $tmp = [System.IO.Path]::GetTempFileName()
    Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60 -Headers @{ 'User-Agent' = 'wific-build' } -OutFile $tmp
    $text = [System.IO.File]::ReadAllText($tmp, [System.Text.Encoding]::UTF8)
    Remove-Item $tmp -ErrorAction SilentlyContinue
    $lines = $text -split "`n" | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object { $_.TrimEnd("`r") }
    if ($lines.Count -lt 20000) { throw "Downloaded vendor list looks too small ($($lines.Count) lines)." }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
    $out = [System.IO.File]::Create($target)
    try {
        $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
        $gz.Write($bytes, 0, $bytes.Length)
        $gz.Dispose()
    }
    finally {
        $out.Dispose()
    }
    Write-Host "Vendor database updated: $($lines.Count) entries."
}
catch {
    if (-not (Test-Path $target)) { throw }
    Write-Host "Vendor download failed ($($_.Exception.Message)); using the committed snapshot."
}
