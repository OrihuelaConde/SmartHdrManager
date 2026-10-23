# ---------------------------------------------------------------------------
# Smart HDR Manager - HDR games list
#
# Downloads which PC games support HDR from PCGamingWiki and writes hdr-games.json - the list behind
# Games > HDR games and Settings > HDR games list. The GitHub workflow .github/workflows/hdr-database.yml runs
# this once a week and publishes the file as an asset of the release "hdr-db". It also runs by hand with
# PowerShell 7 or Windows PowerShell 5.1 - but PCGamingWiki answers some home networks with a Cloudflare challenge
# (HTTP 403, "Just a moment..."); the workflow is the reliable way to build the list.
#
# How: the wiki's own query - the one behind its page "List of games that support high dynamic range display (HDR)" -
# is rendered through the public MediaWiki API (action=parse), 500 games at a time, and read from the resulting table.
# Anonymous Cargo API queries (action=cargoquery) are not allowed on PCGamingWiki; rendering a query is.
#
# The data comes from PCGamingWiki under CC BY-NC-SA 3.0 (https://www.pcgamingwiki.com) - the file keeps that
# license, it is NOT covered by the app's MIT license.
#
# Run from PowerShell:
#   cd S:\Tools\SmartHdrManager
#   powershell -ExecutionPolicy Bypass -File .\hdr-database.ps1                       (writes .\hdr-games.json)
#   powershell -ExecutionPolicy Bypass -File .\hdr-database.ps1 -Output 'S:\hdr-games.json'
# ---------------------------------------------------------------------------
param(
    [string]$Output = (Join-Path $PSScriptRoot 'hdr-games.json'),
    [int]$PageSize = 500
)

$ErrorActionPreference = 'Stop'
$api = 'https://www.pcgamingwiki.com/w/api.php'
# PCGamingWiki asks API users to identify themselves
$userAgent = 'SmartHdrManager-HdrList/1 (+https://github.com/VantaCode420/SmartHdrManager)'

function Fail($text) { Write-Host ''; Write-Host "ERROR: $text" -ForegroundColor Red; exit 1 }

function Invoke-Wiki([hashtable]$query) {
    $query['format'] = 'json'
    $query['formatversion'] = '2'
    $pairs = foreach ($key in $query.Keys) { [uri]::EscapeDataString($key) + '=' + [uri]::EscapeDataString([string]$query[$key]) }
    $body = $pairs -join '&'
    for ($attempt = 1; ; $attempt++) {
        try {
            # POST: a whole query does not fit into every URL length limit
            $result = Invoke-RestMethod -Uri $api -Method Post -Body $body -ContentType 'application/x-www-form-urlencoded' -UserAgent $userAgent -TimeoutSec 60
            if ($result.error) { throw "PCGamingWiki answered: $($result.error.code) - $($result.error.info)" }
            return $result
        }
        catch {
            if ($attempt -ge 4) {
                $hint = if ("$($_.Exception.Message)" -match '403') { ' - PCGamingWiki may block this network with a Cloudflare challenge; run the GitHub workflow "HDR games list" instead.' } else { '' }
                Fail "PCGamingWiki could not be read: $($_.Exception.Message)$hint"
            }
            Write-Host "  retrying in $(5 * $attempt) s ($($_.Exception.Message))"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

# Same rule as HdrDatabase.KeyOf in the app: lower case, no "(2016)"-style suffix, letters and digits only
function KeyOf([string]$name) {
    $n = $name -replace '\s*\([^)]*\)\s*$', ''
    return ($n.ToLowerInvariant() -replace '[^\p{L}\p{N}]', '')
}

function CellText([string]$html) {
    return [System.Net.WebUtility]::HtmlDecode(($html -replace '(?s)<!--.*?-->', '' -replace '<[^>]+>', ' ')).Trim()
}

# Same filter as the wiki's list page. "always on" (HDR without a switch) is stored as "true".
$where = "Video.HDR='true' OR Video.HDR='limited' OR Video.HDR='hackable' OR Video.HDR='always on'"
$rowPattern = '(?s)<tr>\s*<td class="field_Page">(.*?)</td>\s*<td class="field_Steam">(.*?)</td>\s*<td class="field_GOG">(.*?)</td>\s*<td class="field_HDR">(.*?)</td>'

Write-Host '==> Games with HDR on PCGamingWiki' -ForegroundColor Cyan
$total = [int](CellText (Invoke-Wiki @{ action = 'parse'; contentmodel = 'wikitext'; prop = 'text'; disablelimitreport = '1'
        text = "{{#cargo_query:tables=Video|fields=COUNT(*)|where=$where|format=list}}" }).parse.text)
Write-Host "  $total games"
if ($total -lt 100) { Fail "Only $total games found - the wiki changed, the old list is kept." }

$games = @()
$seen = @{}
for ($offset = 0; $offset -lt $total; $offset += $PageSize) {
    $query = "{{#cargo_query:tables=Game,Video|join on=Game._pageID=Video._pageID" +
             "|fields=Game._pageName=Page,Game.Steam_AppID=Steam,Game.GOGcom_ID=GOG,Video.HDR=HDR" +
             "|where=$where|order by=Game._pageName|format=table|limit=$PageSize|offset=$offset}}"
    $html = (Invoke-Wiki @{ action = 'parse'; contentmodel = 'wikitext'; prop = 'text'; disablelimitreport = '1'; text = $query }).parse.text
    if ($html -match 'class="error"') { Fail "PCGamingWiki answered: $(CellText $html)" }

    foreach ($row in [regex]::Matches($html, $rowPattern)) {
        $title = [regex]::Match($row.Groups[1].Value, 'title="([^"]+)"')
        $name = if ($title.Success) { [System.Net.WebUtility]::HtmlDecode($title.Groups[1].Value) } else { CellText $row.Groups[1].Value }
        $hdr = (CellText $row.Groups[4].Value).ToLowerInvariant()
        $seen[$hdr] = 1 + [int]$seen[$hdr]
        if ($hdr -eq 'always on') { $hdr = 'true' }
        if (-not $name) { continue }

        $game = [ordered]@{ name = $name; key = (KeyOf $name); hdr = $hdr }
        $steam = @([regex]::Matches((CellText $row.Groups[2].Value), '\d+') | ForEach-Object { $_.Value } | Select-Object -Unique)
        $gog = @([regex]::Matches((CellText $row.Groups[3].Value), '\d+') | ForEach-Object { $_.Value } | Select-Object -Unique)
        if ($steam.Count -gt 0) { $game['steam'] = $steam }
        if ($gog.Count -gt 0) { $game['gog'] = $gog }
        $games += [pscustomobject]$game
    }
    Write-Host "  $($games.Count) rows read"
    Start-Sleep -Milliseconds 500   # be gentle with the wiki
}

# Every value seen, so a new value in the wiki's template gets noticed
foreach ($v in ($seen.Keys | Sort-Object)) { Write-Host ("  {0,-12} {1,5}" -f $v, $seen[$v]) }

$games = @($games | Sort-Object -Property name -Unique)
if ($games.Count -lt [math]::Min(100, $total)) { Fail "Only $($games.Count) of $total games could be read - the wiki changed, the old list is kept." }

$data = [ordered]@{
    version   = 1
    generated = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    source    = 'PCGamingWiki (https://www.pcgamingwiki.com)'
    license   = 'CC BY-NC-SA 3.0 (https://creativecommons.org/licenses/by-nc-sa/3.0/)'
    games     = $games
}
$json = $data | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Output), $json, (New-Object System.Text.UTF8Encoding($false)))

$counts = @($games | Group-Object -Property hdr | ForEach-Object { "$($_.Name): $($_.Count)" })
$counts += "with Steam id: $(@($games | Where-Object { $_.steam }).Count)"
$counts += "with GOG id: $(@($games | Where-Object { $_.gog }).Count)"
Write-Host ''
Write-Host "Wrote $($games.Count) games to $Output  ($($counts -join ', '); $([math]::Round($json.Length / 1KB)) KB)" -ForegroundColor Green
