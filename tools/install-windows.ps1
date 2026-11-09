#requires -RunAsAdministrator
# Instala/atualiza o agente DeskLink no Windows: publica o binario e registra a
# tarefa de logon com privilegio maximo (sem janela, sem tray).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\DeskLink.Windows'
$dst = Join-Path $env:LOCALAPPDATA 'DeskLink\app'

Write-Host 'publicando...'
dotnet publish $project -c Release -o $dst | Out-Null

$exe = Join-Path $dst 'DeskLink.exe'
if (-not (Test-Path $exe)) { throw "nao encontrado: $exe" }

$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $dst
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERNAME"

# rede de seguranca: reexecuta a cada 5 min; se ja estiver rodando, IgnoreNew
try {
    $rep = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $trigger.Repetition = $rep.Repetition
} catch {
    Write-Warning "nao consegui configurar repeticao: $_"
}

$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest

Get-Process DeskLink -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-ScheduledTask -TaskName 'DeskLink' -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

Register-ScheduledTask -TaskName 'DeskLink' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName 'DeskLink'

Write-Host 'OK: tarefa DeskLink registrada e iniciada.'
