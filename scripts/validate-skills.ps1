# Fails if a skill mentions a tool name that the server does not define, or a SKILL.md lacks name/description.
$root = Split-Path $PSScriptRoot -Parent
$tools = @{}
Get-ChildItem "$root\src\ClaudeSW.AddIn\Tools\*.cs" | ForEach-Object {
    [regex]::Matches((Get-Content $_.FullName -Raw), 'yield return Tool\(\s*"([a-z_0-9]+)"') | ForEach-Object { $tools[$_.Groups[1].Value] = $true }
}
foreach ($extra in "launch_solidworks", "get_solidworks_context", "undo_last_change") { $tools[$extra] = $true }
"Known tools: $($tools.Count)"
$bad = 0
Get-ChildItem "$root\skills" -Recurse -Filter *.md | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    if ($_.Name -eq "SKILL.md" -and ($text -notmatch '(?m)^name:\s*\S' -or $text -notmatch '(?m)^description:\s*\S')) {
        Write-Host "MISSING front matter: $($_.FullName)"; $script:bad++
    }
    # every `snake_case_with_underscore` token in backticks that looks like a tool must exist
    foreach ($m in [regex]::Matches($text, '`([a-z]+(?:_[a-z0-9]+)+)`')) {
        $n = $m.Groups[1].Value
        if (-not $tools.ContainsKey($n) -and $n -notin @("refs", "entity_type")) { Write-Host "UNKNOWN tool '$n' in $($_.FullName)"; $script:bad++ }
    }
}
if ($bad) { Write-Host "$bad problem(s)"; exit 1 } else { Write-Host "Skills OK" }
