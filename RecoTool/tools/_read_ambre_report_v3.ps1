# V3 - uses an XDocument load per sheet (slower but reliable for header rows)
# and resolves shared strings correctly. Output focuses on the first ~50 rows
# plus the top 10 rows formatted as a header-resolved table.
param(
    [string]$Path = 'c:\Users\Gianni\source\repos\RecoTool\Ambre Report Denmark.xlsx'
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Xml.Linq

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
        $doc = [System.Xml.Linq.XDocument]::Parse($ssXml)
        foreach ($si in $doc.Root.Elements()) {
            # concat all descendant t elements
            $txt = ($si.Descendants() | Where-Object { $_.Name.LocalName -eq 't' } | ForEach-Object { $_.Value }) -join ''
            $sharedStrings += , $txt
        }
    }
    Write-Host ("Shared strings loaded: {0}" -f $sharedStrings.Count)

    $wbXml = Read-XmlEntry $zip 'xl/workbook.xml'
    $wb = [System.Xml.Linq.XDocument]::Parse($wbXml)
    $sheetsList = @()
    foreach ($sh in ($wb.Root.Elements() | Where-Object { $_.Name.LocalName -eq 'sheets' }).Elements()) {
        $nameAttr = $sh.Attribute('name')
        $rAttr = $sh.Attribute([System.Xml.Linq.XName]::Get('{http://schemas.openxmlformats.org/officeDocument/2006/relationships}id'))
        $sheetsList += [pscustomobject]@{
            Name = $nameAttr.Value
            RelId = $rAttr.Value
        }
    }

    $relXml = Read-XmlEntry $zip 'xl/_rels/workbook.xml.rels'
    $rels = [System.Xml.Linq.XDocument]::Parse($relXml)
    $relMap = @{}
    foreach ($r in $rels.Root.Elements()) { $relMap[$r.Attribute('Id').Value] = $r.Attribute('Target').Value }

    foreach ($sheet in $sheetsList) {
        $target = $relMap[$sheet.RelId]
        if (-not $target) { continue }
        $path = if ($target.StartsWith('/')) { $target.TrimStart('/') } else { 'xl/' + $target }
        Write-Host ""
        Write-Host ("================ Sheet: {0} ({1}) ================" -f $sheet.Name, $path)

        $sxml = Read-XmlEntry $zip $path
        if (-not $sxml) { Write-Host '  (empty)'; continue }

        try {
            $sdoc = [System.Xml.Linq.XDocument]::Parse($sxml)
        } catch {
            Write-Host ("  parse error: {0}" -f $_.Exception.Message)
            continue
        }

        $rowEls = $sdoc.Descendants() | Where-Object { $_.Name.LocalName -eq 'row' }
        $rowsOut = 0
        foreach ($row in $rowEls) {
            $rNum = $row.Attribute('r').Value
            $cells = @()
            foreach ($c in ($row.Elements() | Where-Object { $_.Name.LocalName -eq 'c' })) {
                $ref = $c.Attribute('r').Value
                $t = if ($c.Attribute('t')) { $c.Attribute('t').Value } else { '' }
                $colLetters = ($ref -replace '[0-9]', '')
                $vEl = $c.Elements() | Where-Object { $_.Name.LocalName -eq 'v' } | Select-Object -First 1
                $isEl = $c.Elements() | Where-Object { $_.Name.LocalName -eq 'is' } | Select-Object -First 1
                $value = ''
                if ($vEl) { $value = $vEl.Value }
                elseif ($isEl) {
                    $value = ($isEl.Descendants() | Where-Object { $_.Name.LocalName -eq 't' } | ForEach-Object { $_.Value }) -join ''
                }
                if ($t -eq 's' -and $value -match '^\d+$') {
                    $idx = [int]$value
                    if ($idx -lt $sharedStrings.Count) { $value = $sharedStrings[$idx] }
                }
                if ($value -ne '') {
                    $cells += ("{0}={1}" -f $colLetters, $value)
                }
            }
            if ($cells.Count -gt 0) {
                Write-Host ("R{0}: {1}" -f $rNum, ($cells -join ' | '))
                $rowsOut++
                if ($rowsOut -ge 40) { Write-Host '  ...[truncated at 40 rows]'; break }
            }
        }
    }
} finally {
    $zip.Dispose()
}
