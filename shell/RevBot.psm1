# RevBot in PowerShell: the Revenant desk pet's shell body.
#
#   Import-Module <repo>\shell\RevBot.psd1
#   revbot                 him, in colour, with what he is doing (revbot show 16: bigger; revbot show head: just his head)
#   revbot ask "question"  ask his Claude; the answer prints here and in his bubble
#   revbot chat            a back-and-forth until you type /bye
#   revbot say "text"      he says it in his speech bubble
#   revbot play wave       any clip: wave, jump, happy, think, shrug, flex, leap, evolve
#   revbot state working   hold a state (working, waiting, review); "revbot clear" lets go
#   revbot talk            opens his chat box on the desktop
#   revbot where           the state file, as is
#   revbot start           starts the desktop pet if it is not running
#   revbot follow on|off   follow you into the terminal (he walks to it and goes in); revbot back brings him out
#   Enable-RevBotPrompt    a one-line RevBot in front of your prompt (Disable-RevBotPrompt undoes it)
#
# It reads state.json (what the pet writes next to its exe) and talks to the pet
# over its local named pipe through revbot-hook.exe. Nothing here needs a network.
# The pet's folder is $env:REVBOT_HOME, else the repo's dist\ next to this module.

Set-StrictMode -Version 2

# Where this module lives: $PSScriptRoot is empty inside the functions when PowerShell calls them later, so take it now.
$script:ModuleRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$script:Esc = [char]27
$script:Cells = $null
$script:Palette = $null
$script:Alphabet = '0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz-_'
$script:OldPrompt = $null

function Get-RevBotHome {
    <# The folder holding DesktopPet.exe, hook\revbot-hook.exe and state.json. #>
    if ($env:REVBOT_HOME) { return $env:REVBOT_HOME }
    $parent = Split-Path $script:ModuleRoot -Parent
    foreach ($dir in @($parent, (Join-Path $parent 'dist'))) {   # dist\shell (deployed) or <repo>\shell
        if (Test-Path (Join-Path $dir 'DesktopPet.exe')) { return (Resolve-Path $dir).Path }
    }
    return (Join-Path $parent 'dist')
}

function Get-RevBotState {
    <# The pet's state, or $null when he is not running (or has never run). #>
    $path = Join-Path (Get-RevBotHome) 'state.json'
    if (-not (Test-Path $path)) { return $null }
    try {
        $s = Get-Content -Raw -Path $path -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch { return $null }
    if (-not $s.running) { return $null }
    # A crash leaves running=true behind; the process id tells the truth.
    if ($s.PSObject.Properties['pid'] -and -not (Get-Process -Id $s.pid -ErrorAction SilentlyContinue)) { return $null }
    return $s
}

function Send-RevBot {
    <# One line over the pet's pipe: "say Hello", "play wave", "ask What time is it". #>
    param([Parameter(Mandatory, ValueFromRemainingArguments)][string[]]$Line)
    $exe = Join-Path (Get-RevBotHome) 'hook\revbot-hook.exe'
    if (-not (Test-Path $exe)) { Write-Warning "revbot-hook.exe not found under $(Get-RevBotHome) (set `$env:REVBOT_HOME)"; return }
    & $exe --send ($Line -join ' ') | Out-Null
}

function Start-RevBot {
    <# Starts the desktop pet if it is not already on screen. #>
    if (Get-RevBotState) { Write-Host 'RevBot is already here.'; return }
    $exe = Join-Path (Get-RevBotHome) 'DesktopPet.exe'
    if (-not (Test-Path $exe)) { Write-Warning "DesktopPet.exe not found under $(Get-RevBotHome) (set `$env:REVBOT_HOME)"; return }
    Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe -Parent) | Out-Null
    for ($i = 0; $i -lt 40 -and -not (Get-RevBotState); $i++) { Start-Sleep -Milliseconds 250 }
    if (Get-RevBotState) { Write-Host 'RevBot is up.' } else { Write-Warning 'He did not report in; check logs\pet.log.' }
}

function Import-RevBotCells {
    if ($script:Cells) { return $true }
    $candidates = @(
        (Join-Path (Get-RevBotHome) 'mod\revbot\cells.json'),
        (Join-Path (Split-Path $script:ModuleRoot -Parent) 'mod\revbot\cells.json')
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) {
            $script:Cells = Get-Content -Raw -Path $c | ConvertFrom-Json
            $script:Palette = @($script:Cells.palette | ForEach-Object {
                $h = $_.TrimStart('#'); "$([Convert]::ToInt32($h.Substring(0,2),16));$([Convert]::ToInt32($h.Substring(2,2),16));$([Convert]::ToInt32($h.Substring(4,2),16))"
            })
            return $true
        }
    }
    return $false
}

function Get-RevBotFrameLines {
    <# One frame of a clip as lines of ANSI-coloured text, left as block characters on the terminal's own background. #>
    param([string]$Clip = 'idle', [int]$Rows = 12, [ValidateSet('body', 'head')][string]$Crop = 'body', [int]$Frame = 0, [switch]$Trim)
    if (-not (Import-RevBotCells)) { return @('(no sprite sheet: build it with tools\build_cells.py)') }
    # the size of that crop nearest that many rows (a sheet built before crops existed counts as all body)
    $sizes = @($script:Cells.sizes | Where-Object { $(if ($_.PSObject.Properties['crop']) { $_.crop } else { 'body' }) -eq $Crop })
    if ($sizes.Count -eq 0) { $sizes = @($script:Cells.sizes) }
    $size = $sizes | Sort-Object { [Math]::Abs($_.rows - $Rows) } | Select-Object -First 1
    $clips = $size.clips
    $c = $clips.PSObject.Properties[$Clip]
    if (-not $c) { $c = $clips.PSObject.Properties['idle'] }
    $frames = $c.Value.frames
    $f = $frames[$Frame % $frames.Count]
    $e = $script:Esc
    $lines = New-Object System.Collections.Generic.List[string]
    for ($r = 0; $r -lt $size.rows; $r++) {
        $sb = New-Object System.Text.StringBuilder
        $lastFg = -1; $lastBg = -1
        for ($col = 0; $col -lt $size.columns; $col++) {
            $i = $r * $size.columns + $col
            $g = $f.g[$i]
            $fi = $script:Alphabet.IndexOf($f.f[$i])
            $bi = $script:Alphabet.IndexOf($f.b[$i])
            if ($fi -ne $lastFg) { [void]$sb.Append($(if ($fi -gt 0) { "$e[38;2;$($script:Palette[$fi-1])m" } else { "$e[39m" })); $lastFg = $fi }
            if ($bi -ne $lastBg) { [void]$sb.Append($(if ($bi -gt 0) { "$e[48;2;$($script:Palette[$bi-1])m" } else { "$e[49m" })); $lastBg = $bi }
            [void]$sb.Append($g)
        }
        [void]$sb.Append("$e[0m")
        $lines.Add($sb.ToString())
    }
    if ($Trim) {
        $blank = { param($l) (($l -replace "$([char]27)\[[0-9;]*m", '').Trim()) -eq '' }
        while ($lines.Count -gt 1 -and (& $blank $lines[0])) { $lines.RemoveAt(0) }
        while ($lines.Count -gt 1 -and (& $blank $lines[$lines.Count - 1])) { $lines.RemoveAt($lines.Count - 1) }
    }
    return $lines
}

function Get-RevBotDoing {
    param($State)
    if (-not $State) { return 'not running (revbot start)' }
    switch ($State.claude) {
        'waiting' { return 'Claude needs your OK' }
        'working' { return "hammering away ($($State.sessions) Claude session$(if ($State.sessions -ne 1) {'s'}))" }
        'review'  { return 'looking into it' }
    }
    switch ($State.mode) {
        'Asleep'  { return 'asleep' }
        'Held'    { return 'picked up' }
        'Falling' { return 'in the air' }
        'Walk'    { return 'out for a walk' }
    }
    return 'here with you'
}

function Show-RevBot {
    <# Him, in colour, with his state beside: his whole figure (12 or 16 rows), or -Head for just his head. #>
    param([int]$Rows = 12, [switch]$Head)
    $s = Get-RevBotState
    $clip = if ($s) { $s.clip } else { 'sleeping' }
    if ($clip -eq 'look') { $clip = 'idle' }
    $lines = @(Get-RevBotFrameLines -Clip $clip -Rows $(if ($Head) { 8 } else { $Rows }) -Crop $(if ($Head) { 'head' } else { 'body' }) -Trim)
    $side = @(
        "$($script:Esc)[1;38;2;255;153;0mRevBot$($script:Esc)[0m",
        (Get-RevBotDoing $s)
    )
    if ($s -and $s.saying) { $side += "`"$($s.saying)`"" }
    if ($s -and $s.surfaces -and $s.surfaces.Count -gt 0) { $side += "also in: $($s.surfaces -join ', ')" }
    $pad = ($lines | ForEach-Object { ($_ -replace "$($script:Esc)\[[0-9;]*m", '').Length } | Measure-Object -Maximum).Maximum + 2
    $start = [Math]::Max(0, $lines.Count - $side.Count)
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $plain = ($lines[$i] -replace "$($script:Esc)\[[0-9;]*m", '')
        $text = if ($i -ge $start) { $side[$i - $start] } else { '' }
        Write-Host ($lines[$i] + (' ' * [Math]::Max(0, $pad - $plain.Length)) + $text)
    }
}

function Invoke-RevBotAsk {
    <# Ask his Claude (or local model) and wait for the answer; it shows in his bubble too. #>
    [CmdletBinding(PositionalBinding = $false)]
    param([Parameter(Mandatory, Position = 0, ValueFromRemainingArguments)][string[]]$Text, [int]$TimeoutSec = 180)
    $before = Get-RevBotState
    if (-not $before) { Write-Warning 'RevBot is not running (revbot start).'; return }
    $n0 = if ($before.PSObject.Properties['replyCount']) { $before.replyCount } else { 0 }
    Send-RevBot ('ask ' + ($Text -join ' '))
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $dots = 0
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
        $s = Get-RevBotState
        if (-not $s) { Write-Warning 'He went away.'; return }
        if ($s.PSObject.Properties['replyCount'] -and $s.replyCount -ne $n0) {
            Write-Host ''
            Write-Host "$($script:Esc)[38;2;255;153;0mRevBot:$($script:Esc)[0m $($s.reply)"
            return
        }
        if (++$dots % 10 -eq 0) { Write-Host -NoNewline '.' }
    }
    Write-Host ''
    Write-Warning 'No answer yet; it will show in his bubble when it comes.'
}

function Enter-RevBotChat {
    <# A conversation in the terminal; /bye ends it. #>
    if (-not (Get-RevBotState)) { Write-Warning 'RevBot is not running (revbot start).'; return }
    Show-RevBot -Head
    Write-Host "Talk to RevBot. /bye ends it."
    while ($true) {
        $line = Read-Host 'you'
        if ($null -eq $line) { break }
        $line = $line.Trim()
        if ($line -eq '') { continue }
        if ($line -in '/bye', '/exit', '/quit') { break }
        Invoke-RevBotAsk -Text $line
    }
}

function Get-RevBotPromptLine {
    <# One line for the prompt: his face and what he is doing. #>
    $s = Get-RevBotState
    $e = $script:Esc
    if (-not $s) { return "$e[2m[z_z] RevBot is away$e[0m" }
    $face = switch ($s.claude) { 'waiting' { '[o_o]' } 'working' { '[>_<]' } 'review' { '[o_O]' } default { if ($s.mode -eq 'Asleep') { '[-_-]' } else { '[^_^]' } } }
    $line = "$e[38;2;255;153;0m$face$e[0m $(Get-RevBotDoing $s)"
    if ($s.saying) { $line += "  $e[2m`"$($s.saying)`"$e[0m" }
    return $line
}

function Enable-RevBotPrompt {
    <# Puts RevBot's line above your existing prompt. #>
    if ($script:OldPrompt) { return }
    $script:OldPrompt = (Get-Item function:prompt).ScriptBlock
    $old = $script:OldPrompt
    Set-Item function:global:prompt -Value {
        Write-Host (Get-RevBotPromptLine)
        & $old
    }.GetNewClosure()
}

function Disable-RevBotPrompt {
    if (-not $script:OldPrompt) { return }
    Set-Item function:global:prompt -Value $script:OldPrompt
    $script:OldPrompt = $null
}

function Invoke-RevBot {
    <# The one command: revbot [ask|chat|say|play|state|clear|talk|where|start|show|come] ... #>
    param([Parameter(ValueFromRemainingArguments)][string[]]$Words)
    if (-not $Words -or $Words.Count -eq 0) { Show-RevBot; return }
    $verb = $Words[0].ToLowerInvariant()
    $rest = @($Words | Select-Object -Skip 1)
    switch ($verb) {
        'ask'   { Invoke-RevBotAsk -Text $rest }
        'chat'  { Enter-RevBotChat }
        'say'   { Send-RevBot ('say ' + ($rest -join ' ')) }
        'play'  { Send-RevBot ('play ' + ($rest -join ' ')) }
        'state' { Send-RevBot ('state ' + ($rest -join ' ')) }
        'clear' { Send-RevBot 'clear' }
        'talk'  { Send-RevBot 'talk' }
        'come'  { Send-RevBot 'sendhome' }
        'follow' { Send-RevBot ('follow ' + ($rest -join ' ')) }
        'back'  { Send-RevBot 'comeback' }
        'start' { Start-RevBot }
        'show'  { if ($rest.Count -and $rest[0] -eq 'head') { Show-RevBot -Head } else { Show-RevBot -Rows $(if ($rest.Count) { [int]$rest[0] } else { 12 }) } }
        'where' { $s = Get-RevBotState; if ($s) { $s | Format-List } else { 'not running' } }
        default { Write-Host "revbot [ask TEXT | chat | say TEXT | play CLIP | state NAME | clear | talk | start | show [12|16|head] | where | follow on|off | back]" }
    }
}

Set-Alias -Name revbot -Value Invoke-RevBot
Export-ModuleMember -Function Get-RevBotHome, Get-RevBotState, Send-RevBot, Start-RevBot, Show-RevBot, Invoke-RevBotAsk, Enter-RevBotChat, Get-RevBotPromptLine, Enable-RevBotPrompt, Disable-RevBotPrompt, Invoke-RevBot -Alias revbot
