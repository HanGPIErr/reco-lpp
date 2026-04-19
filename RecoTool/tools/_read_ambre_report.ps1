# Ad-hoc: extract textual content of the sample "Ambre Report Denmark.xlsx"
# so Cascade can read it without needing Excel installed.
# Writes a plain-text dump to stdout (shared strings + each sheet's cells).
param(
    [string]$Path = 'c:\Users\Gianni\source\repos\RecoTool\Ambre Report Denmark.xlsx'
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
try {
    Write-Host "=== ENTRIES ==="
    $zip.Entries | ForEach-Object { "{0,-60} {1,10}" -f $_.FullName, $_.Length } | Write-Host

    function Read-XmlEntry([string]$name) {
        $e = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
        if (-not $e) { return $null }
        $s = $e.Open()
        try {
            $sr = New-Object System.IO.StreamReader($s)
            try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        } finally { $s.Dispose() }
    }

    Write-Host "`n=== workbook.xml ==="
    Read-XmlEntry 'xl/workbook.xml' | Write-Host

    Write-Host "`n=== sharedStrings.xml (truncated) ==="
    $ss = Read-XmlEntry 'xl/sharedStrings.xml'
    if ($ss) {
        if ($ss.Length -gt 8000) { Write-Host $ss.Substring(0, 8000); Write-Host "...[truncated, total length $($ss.Length)]" }
        else { Write-Host $ss }
    }

    Write-Host "`n=== sheet list ==="
    $sheets = $zip.Entries | Where-Object { $_.FullName -like 'xl/worksheets/sheet*.xml' }
    $sheets | ForEach-Object { "{0,-40} {1,10}" -f $_.FullName, $_.Length } | Write-Host

    foreach ($sh in $sheets) {
        Write-Host "`n=== $($sh.FullName) (truncated) ==="
        $txt = Read-XmlEntry $sh.FullName
        if ($txt) {
            if ($txt.Length -gt 12000) { Write-Host $txt.Substring(0, 12000); Write-Host "...[truncated, total length $($txt.Length)]" }
            else { Write-Host $txt }
        }
    }
} finally {
    $zip.Dispose()
}
