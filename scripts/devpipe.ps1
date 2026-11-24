<#
.SYNOPSIS
  Sends one command to the add-in's developer pipe (SOLIDWORKS must run with CLAUDESW_DEV=1).
.EXAMPLE
  .\devpipe.ps1 '{"cmd":"screenshot","path":"C:\\temp\\pane.png"}'
  .\devpipe.ps1 '{"cmd":"tool","name":"new_document","input":{"doc_type":"part"}}'
  .\devpipe.ps1 '{"cmd":"ui","message":{"type":"chat.send","text":"hello"}}'
  .\devpipe.ps1 '{"cmd":"state"}'
#>
param([Parameter(Mandatory = $true)][string]$Json, [int]$TimeoutMs = 180000)
$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "ClaudeForSolidWorks.Dev", [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(10000)
$enc = New-Object System.Text.UTF8Encoding($false)
$w = New-Object System.IO.StreamWriter($pipe, $enc); $w.AutoFlush = $true
$r = New-Object System.IO.StreamReader($pipe, $enc)
$w.WriteLine(($Json -replace "`r?`n", " "))
$task = $r.ReadLineAsync()
if (-not $task.Wait($TimeoutMs)) { $pipe.Dispose(); throw "timeout waiting for reply" }
$task.Result
$pipe.Dispose()
