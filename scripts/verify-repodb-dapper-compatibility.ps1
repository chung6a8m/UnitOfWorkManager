param(
    [switch]$KeepContainers,
    [int]$SqlServerPort = 14333,
    [int]$PostgreSqlPort = 15432,
    [int]$MySqlPort = 13306
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$composeFile = Join-Path $repoRoot 'tests/containers/repodb-provider-matrix.compose.yml'
$resultsDirectory = Join-Path $repoRoot '.artifacts/repodb-provider-matrix'
$composeProject = 'uow-repodb-matrix'
$password = 'UowTest!2026'

function Assert-CommandAvailable {
    param([Parameter(Mandatory)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found on PATH."
    }
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    Write-Host "`n==> $Description"
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

function Get-TrxCounter {
    param(
        [Parameter(Mandatory)][System.Xml.XmlElement]$Counters,
        [Parameter(Mandatory)][string]$Name,
        [switch]$Required
    )

    $rawValue = $Counters.GetAttribute($Name)
    if ([string]::IsNullOrWhiteSpace($rawValue)) {
        if ($Required) {
            throw "TRX Counters element is missing required '$Name' attribute."
        }
        return 0
    }

    $value = 0
    if (-not [int]::TryParse($rawValue, [ref]$value)) {
        throw "TRX counter '$Name' has invalid integer value '$rawValue'."
    }

    return $value
}

function Assert-ProviderTrx {
    param(
        [Parameter(Mandatory)][string]$ProviderName,
        [Parameter(Mandatory)][string]$Path
    )

    if (-not (Test-Path $Path)) {
        throw "$ProviderName did not produce TRX output at '$Path'."
    }

    [xml]$trx = Get-Content -Raw -Path $Path
    $counters = $trx.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "$ProviderName TRX has no ResultSummary/Counters element."
    }

    $passed = Get-TrxCounter -Counters $counters -Name 'passed' -Required
    $failed = Get-TrxCounter -Counters $counters -Name 'failed' -Required
    $notExecuted = Get-TrxCounter -Counters $counters -Name 'notExecuted' -Required
    $skipped = Get-TrxCounter -Counters $counters -Name 'skipped'

    if ($passed -ne 8 -or $failed -ne 0 -or $notExecuted -ne 0 -or $skipped -ne 0) {
        throw "$ProviderName expected 8 passed, 0 failed and 0 skipped/notExecuted; actual: passed=$passed failed=$failed skipped=$skipped notExecuted=$notExecuted."
    }

    Write-Host "$ProviderName contracts: 8 PASS, 0 SKIP"
}

Assert-CommandAvailable -Name 'docker'
Assert-CommandAvailable -Name 'dotnet'
Invoke-Checked -Description 'Check Docker Compose' -Action { docker compose version }

if (-not (Test-Path $composeFile)) {
    throw "Compose file not found: $composeFile"
}

if (Test-Path $resultsDirectory) {
    Remove-Item -Recurse -Force $resultsDirectory
}
New-Item -ItemType Directory -Path $resultsDirectory | Out-Null

$env:UOW_SQLSERVER_PORT = $SqlServerPort.ToString()
$env:UOW_POSTGRESQL_PORT = $PostgreSqlPort.ToString()
$env:UOW_MYSQL_PORT = $MySqlPort.ToString()

try {
    Invoke-Checked -Description 'Start RepoDb provider database matrix' -Action {
        docker compose -f $composeFile -p $composeProject up -d --wait
    }

    Invoke-Checked -Description 'Create SQL Server test database' -Action {
        docker exec uow-sqlserver /opt/mssql-tools18/bin/sqlcmd `
            -S localhost -U sa -P $password -C `
            -Q "IF DB_ID(N'uow_tests') IS NULL CREATE DATABASE [uow_tests];"
    }

    $env:UOW_TEST_SQLSERVER = "Server=127.0.0.1,$SqlServerPort;Database=uow_tests;User Id=sa;Password=$password;Encrypt=False;TrustServerCertificate=True"
    $env:UOW_TEST_POSTGRESQL = "Host=127.0.0.1;Port=$PostgreSqlPort;Database=uow_tests;Username=postgres;Password=$password"
    $env:UOW_TEST_MYSQL = "Server=127.0.0.1;Port=$MySqlPort;Database=uow_tests;User ID=root;Password=$password;SslMode=None;AllowPublicKeyRetrieval=True"

    Push-Location $repoRoot
    try {
        Invoke-Checked -Description 'Dapper QueryMultiple contracts' -Action {
            dotnet test tests/UnitOfWork.Tests/UnitOfWork.Tests.csproj `
                --filter 'FullyQualifiedName~DapperQueryMultipleTests'
        }
        Write-Host 'Dapper QueryMultiple contracts: PASS'

        Invoke-Checked -Description 'RepoDb SQLite metadata contracts' -Action {
            dotnet test tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests/UnitOfWork.Sample.WebApi.MinimalApi.RepoDb.Tests.csproj `
                --filter 'FullyQualifiedName~RepoDbSqliteMetadataTests'
        }
        Write-Host 'RepoDb SQLite metadata contracts: PASS'

        $providers = @(
            @{
                Name = 'RepoDb SQL Server'
                Project = 'tests/UnitOfWork.RepoDb.SqlServer.Tests/UnitOfWork.RepoDb.SqlServer.Tests.csproj'
                Trx = 'repodb-sqlserver.trx'
            },
            @{
                Name = 'RepoDb PostgreSQL'
                Project = 'tests/UnitOfWork.RepoDb.PostgreSql.Tests/UnitOfWork.RepoDb.PostgreSql.Tests.csproj'
                Trx = 'repodb-postgresql.trx'
            },
            @{
                Name = 'RepoDb MySql.Data'
                Project = 'tests/UnitOfWork.RepoDb.MySql.Tests/UnitOfWork.RepoDb.MySql.Tests.csproj'
                Trx = 'repodb-mysql.trx'
            },
            @{
                Name = 'RepoDb MySqlConnector'
                Project = 'tests/UnitOfWork.RepoDb.MySqlConnector.Tests/UnitOfWork.RepoDb.MySqlConnector.Tests.csproj'
                Trx = 'repodb-mysqlconnector.trx'
            }
        )

        foreach ($provider in $providers) {
            $trxPath = Join-Path $resultsDirectory $provider.Trx
            Invoke-Checked -Description "$($provider.Name) contracts" -Action {
                dotnet test $provider.Project `
                    --results-directory $resultsDirectory `
                    --logger "trx;LogFileName=$($provider.Trx)"
            }
            Assert-ProviderTrx -ProviderName $provider.Name -Path $trxPath
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (-not $KeepContainers) {
        Write-Host "`n==> Stop RepoDb provider database matrix"
        docker compose -f $composeFile -p $composeProject down -v
    }
}
