<#
.SYNOPSIS
  Manage the AppHost's persistent infrastructure containers (Postgres, Redis, Mailpit).

.DESCRIPTION
  Persistent containers keep running after the AppHost stops (fast restarts, data kept),
  but they also keep using RAM. Use this script to manage them.

    status   list the containers, their memory use and the data volumes   (default)
    stop     stop them to free RAM; the next AppHost run starts them again
    start    start them again without running the AppHost
    remove   delete the containers but KEEP the volumes (data). Needed after changing
             container settings such as memory caps, which Aspire does not re-apply
             to an existing persistent container.
    reset    delete the containers AND the volumes: all local data is gone (asks first)

.EXAMPLE
  .\tools\infra.ps1 stop
#>
param(
    [ValidateSet('status', 'stop', 'start', 'remove', 'reset')]
    [string]$Action = 'status'
)

$ErrorActionPreference = 'Stop'

# Aspire names persistent containers "<resource>-<hash>" and labels them as persistent.
function Get-InfraContainers {
    docker ps -a --filter 'label=com.microsoft.developer.usvc-dev.persistent=true' --format '{{.Names}}' |
        Where-Object { $_ -match '^(postgres|redis|mailpit)-' }
}

$containers = @(Get-InfraContainers)
$volumes = @(docker volume ls --format '{{.Name}}' | Where-Object { $_ -like 'fooddelivery-*' })

switch ($Action) {
    'status' {
        if ($containers.Count -eq 0) { Write-Host 'No infrastructure containers. Run the AppHost to create them.' }
        else {
            docker ps -a --filter 'label=com.microsoft.developer.usvc-dev.persistent=true' --format 'table {{.Names}}\t{{.Status}}'
            $running = @(docker ps --format '{{.Names}}' | Where-Object { $containers -contains $_ })
            if ($running.Count -gt 0) { docker stats --no-stream --format 'table {{.Name}}\t{{.MemUsage}}' @running }
        }
        Write-Host "`nVolumes: $($volumes -join ', ')"
    }
    'stop' {
        if ($containers.Count -gt 0) { docker stop @containers | Out-Null }
        Write-Host "Stopped: $($containers -join ', ')"
    }
    'start' {
        if ($containers.Count -gt 0) { docker start @containers | Out-Null }
        Write-Host "Started: $($containers -join ', ')"
    }
    'remove' {
        if ($containers.Count -gt 0) { docker rm -f @containers | Out-Null }
        Write-Host "Removed containers (volumes kept): $($containers -join ', ')"
    }
    'reset' {
        $answer = Read-Host "Delete containers AND volumes ($($volumes -join ', '))? All local data is lost. Type 'yes'"
        if ($answer -ne 'yes') { Write-Host 'Cancelled.'; return }
        if ($containers.Count -gt 0) { docker rm -f @containers | Out-Null }
        if ($volumes.Count -gt 0) { docker volume rm @volumes | Out-Null }
        Write-Host 'Containers and volumes deleted. The next AppHost run starts from empty databases.'
    }
}
