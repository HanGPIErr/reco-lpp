# Ad-hoc analysis helper: dump Ambre Report Denmark.xlsx as plain tab-separated
# rows with resolved shared strings. Prints each sheet to stdout.
param(
    [string]$Path = 'c:\Users\Gianni\source\repos\RecoTool\Ambre Report Denmark.xlsx'
)

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Read-XmlEntry([System.IO.Compression.ZipArchive]$zip, [string]$name) {
    $e = $zip.Entries | Where-Object { $_.FullName -eq $name } | Select-Object -First 1
    if (-not $e) { return $null }
    $s = $e.Open()
    try {
        $sr = New-Object System.IO.StreamReader($s)
        try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
    } finally { $s.Dispose() }
}

function ColLetterToIndex([string]$letters) {
    $n = 0
    foreach ($c in $letters.ToCharArray()) {
        $n = $n * 26 + ([int][char]$c - [int][char]'A' + 1)
    }
    return $n
}

$zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
try {
    # Shared strings
    $ssXml = Read-XmlEntry $zip 'xl/sharedStrings.xml'
    $sharedStrings = @()
    if ($ssXml) {
        [xml]$doc = $ssXml
        $ns = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
        $ns.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
        foreach ($si in $doc.sst.si) {
            # Flatten <t> or rich <r>/<t> children
            $txt = ''
            if ($si.t) { $txt = $si.t.InnerText }
            elseif ($si.r) { $txt = ($si.r | ForEach-Object { $_.t.InnerText }) -join '' }
            $sharedStrings += , $txt
        }
    }
    Write-Host ("Shared strings loaded: {0}" -f $sharedStrings.Count)

    # Sheet list
    $wbXml = Read-XmlEntry $zip 'xl/workbook.xml'
    [xml]$wb = $wbXml
    $ns = New-Object System.Xml.XmlNamespaceManager($wb.NameTable)
    $ns.AddNamespace('x', 'http://schemas.openxmlformats.org/spreadsheetml/2006/main')
    $ns.AddNamespace('r', 'http://schemas.openxmlformats.org/officeDocument/2006/relationships')
    $sheets = @()
    foreach ($sh in $wb.workbook.sheets.sheet) {
        $sheets += [pscustomobject]@{
            Name = $sh.name
            SheetId = $sh.sheetId
            RelId = $sh.GetAttribute('id','http://schemas.openxmlformats.org/officeDocument/2006/relationships')
        }
    }
    Write-Host ("Sheets: {0}" -f (($sheets | ForEach-Object { $_.Name }) -join ', '))

    # Relationships to map rel id → sheet file
    $relXml = Read-XmlEntry $zip 'xl/_rels/workbook.xml.rels'
    [xml]$rels = $relXml
    $relMap = @{}
    foreach ($r in $rels.Relationships.Relationship) { $relMap[$r.Id] = $r.Target }

    foreach ($sheet in $sheets) {
        $target = $relMap[$sheet.RelId]
        if (-not $target) { continue }
        $path = if ($target.StartsWith('/')) { $target.TrimStart('/') } else { 'xl/' + $target }
        Write-Host ""
        Write-Host ("================ Sheet: {0}   ({1}) ================" -f $sheet.Name, $path)

        $sxml = Read-XmlEntry $zip $path
        if (-not $sxml) { Write-Host '  (empty)'; continue }

        # Use XmlReader for streaming since sheets can be huge
        $sr = New-Object System.IO.StringReader($sxml)
        $xr = [System.Xml.XmlReader]::Create($sr)

        $row = @()
        $currentRow = -1
        $rowsOut = 0
        $maxCol = 0

        while ($xr.Read()) {
            if ($xr.NodeType -eq [System.Xml.XmlNodeType]::Element -and $xr.LocalName -eq 'row') {
                if ($row.Count -gt 0) {
                    Write-Host ("R{0}: {1}" -f $currentRow, ($row -join ' | '))
                    $rowsOut++
                    if ($rowsOut -ge 60) { Write-Host '  ...[truncated at 60 rows]'; break }
                }
                $row = @()
                $currentRow = [int]$xr.GetAttribute('r')
            }
            elseif ($xr.NodeType -eq [System.Xml.XmlNodeType]::Element -and $xr.LocalName -eq 'c') {
                $ref = $xr.GetAttribute('r')
                $t = $xr.GetAttribute('t')
                $colLetters = ($ref -replace '[0-9]', '')
                $colIdx = ColLetterToIndex $colLetters
                if ($colIdx -gt $maxCol) { $maxCol = $colIdx }

                $value = ''
                # Read nested <v>/<f>
                $depth = $xr.Depth
                if (-not $xr.IsEmptyElement) {
                    while ($xr.Read() -and $xr.Depth -gt $depth) {
                        if ($xr.NodeType -eq [System.Xml.XmlNodeType]::Element -and $xr.LocalName -eq 'v') {
                            $value = $xr.ReadElementContentAsString()
                        }
                    }
                }

                if ($t -eq 's' -and $value -match '^\d+$') {
                    $idx = [int]$value
                    if ($idx -lt $sharedStrings.Count) { $value = $sharedStrings[$idx] }
                }

                while ($row.Count -lt ($colIdx - 1)) { $row += '' }
                $row += ("{0}={1}" -f $colLetters, $value)
            }
        }
        if ($row.Count -gt 0 -and $rowsOut -lt 60) {
            Write-Host ("R{0}: {1}" -f $currentRow, ($row -join ' | '))
        }
        $xr.Dispose()
        $sr.Dispose()
    }
} finally {
    $zip.Dispose()
}
