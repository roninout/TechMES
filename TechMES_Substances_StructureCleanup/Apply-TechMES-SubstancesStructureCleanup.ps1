param(
    [Parameter(Mandatory = $false)]
    [string]$ProjectRoot = ""
)

$ErrorActionPreference = "Stop"

function Find-TechMESRoot {
    param([string]$ExplicitRoot)

    $candidates = New-Object System.Collections.Generic.List[string]

    if (-not [string]::IsNullOrWhiteSpace($ExplicitRoot)) {
        $candidates.Add([System.IO.Path]::GetFullPath($ExplicitRoot))
    }

    $candidates.Add([System.IO.Path]::GetFullPath((Get-Location).Path))

    if ($MyInvocation.MyCommand.Path) {
        $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
        $candidates.Add([System.IO.Path]::GetFullPath($scriptDir))
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $current = $candidate

        while (-not [string]::IsNullOrWhiteSpace($current)) {
            if ((Test-Path (Join-Path $current "TechMES.Calc")) -and
                (Test-Path (Join-Path $current "TechMES.Calc.Tests"))) {
                return $current
            }

            $parent = Split-Path -Parent $current

            if ([string]::IsNullOrWhiteSpace($parent) -or $parent -eq $current) {
                break
            }

            $current = $parent
        }
    }

    throw "TechMES solution root was not found. Pass -ProjectRoot explicitly."
}

function Read-Utf8File {
    param([string]$Path)
    return [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}

function Write-Utf8File {
    param([string]$Path, [string]$Content)
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Add-UsingIfMissing {
    param(
        [string]$Content,
        [string]$UsingLine
    )

    if ($Content.Contains($UsingLine)) {
        return $Content
    }

    return $UsingLine + [Environment]::NewLine + $Content
}

$root = Find-TechMESRoot -ExplicitRoot $ProjectRoot

$calcRoot = Join-Path $root "TechMES.Calc"
$testsRoot = Join-Path $root "TechMES.Calc.Tests"
$substancesRoot = Join-Path $calcRoot "Substances"

$legacyRoot = Join-Path $substancesRoot "Legacy"
$legacyContentRoot = Join-Path $legacyRoot "Content"
$waterSteamRoot = Join-Path $legacyRoot "WaterSteamProLib"

$componentsRoot = Join-Path $substancesRoot "Components"
$thermodynamicsRoot = Join-Path $substancesRoot "Thermodynamics"
$contentRoot = Join-Path $substancesRoot "Content"

if (-not (Test-Path $legacyRoot)) {
    throw "Expected source folder was not found: $legacyRoot"
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupRoot = Join-Path $root ".patch-backup\SubstancesStructureCleanup_$timestamp"

Write-Host ""
Write-Host "TechMES Substances structure cleanup"
Write-Host "Project root : $root"
Write-Host "Backup       : $backupRoot"
Write-Host ""

New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
Copy-Item -Path $substancesRoot -Destination (Join-Path $backupRoot "Substances") -Recurse -Force
Copy-Item -Path (Join-Path $testsRoot "DensityCapacityDefinitionTests.cs") -Destination $backupRoot -Force

New-Item -ItemType Directory -Force -Path $componentsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $thermodynamicsRoot | Out-Null
New-Item -ItemType Directory -Force -Path $contentRoot | Out-Null

# -------------------------------------------------------------------------
# 1. WaterSteamProLib
# -------------------------------------------------------------------------
# До удаления убеждаемся, что в исходных формулах нет активного WspLib-вызова.
# Закомментированные строки "// ... WspLib..." допускаются.
$activeWspReferences = @()

Get-ChildItem -Path $legacyRoot -Filter "*.cs" -File -Recurse |
    Where-Object { $_.FullName -notlike "*\WaterSteamProLib\*" } |
    ForEach-Object {
        $file = $_

        foreach ($line in Get-Content -Path $file.FullName) {
            $trimmed = $line.Trim()

            if ($trimmed.Contains("WspLib.") -and -not $trimmed.StartsWith("//")) {
                $activeWspReferences += "$($file.FullName): $trimmed"
            }
        }
    }

if ($activeWspReferences.Count -gt 0) {
    Write-Host "Active WspLib references were found:" -ForegroundColor Red
    $activeWspReferences | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "WaterSteamProLib cannot be removed safely while active WspLib calls exist."
}

# -------------------------------------------------------------------------
# 2. Компоненты: Legacy/*.cs -> Components/*.cs
# -------------------------------------------------------------------------
$componentFiles = Get-ChildItem -Path $legacyRoot -Filter "*.cs" -File |
    Where-Object { $_.Name -notin @("LegacySubstance.cs", "TechLib.cs") }

foreach ($file in $componentFiles) {
    $destination = Join-Path $componentsRoot $file.Name
    Move-Item -Path $file.FullName -Destination $destination -Force

    $text = Read-Utf8File $destination

    # Меняем только namespace/using. Тела GetDensity/GetCapacity/GetContent не трогаем.
    $text = $text.Replace(
        "namespace TechMES.Calc.Substances.Legacy",
        "namespace TechMES.Calc.Substances.Components")

    $text = $text.Replace(
        "using TechMES.Calc.Substances.Legacy.WaterSteamProLib;" + [Environment]::NewLine,
        "")

    $text = $text.Replace(
        "using TechMES.Calc.Substances.Legacy.WaterSteamProLib;" + "`n",
        "")

    # Base class теперь находится в корневом namespace Substances.
    $text = Add-UsingIfMissing -Content $text -UsingLine "using TechMES.Calc.Substances;"

    # TechLib находится отдельно от компонентов.
    if ($text.Contains("TechLib.")) {
        $text = Add-UsingIfMissing -Content $text -UsingLine "using TechMES.Calc.Substances.Thermodynamics;"
    }

    Write-Utf8File -Path $destination -Content $text
}

# -------------------------------------------------------------------------
# 3. LegacySubstance -> корень Substances
# -------------------------------------------------------------------------
$legacySubstanceSource = Join-Path $legacyRoot "LegacySubstance.cs"
$legacySubstanceDestination = Join-Path $substancesRoot "LegacySubstance.cs"

if (Test-Path $legacySubstanceSource) {
    Move-Item -Path $legacySubstanceSource -Destination $legacySubstanceDestination -Force
}

$text = Read-Utf8File $legacySubstanceDestination
$text = $text.Replace(
    "namespace TechMES.Calc.Substances.Legacy",
    "namespace TechMES.Calc.Substances")
Write-Utf8File -Path $legacySubstanceDestination -Content $text

# -------------------------------------------------------------------------
# 4. TechLib -> Substances/Thermodynamics
# -------------------------------------------------------------------------
$techLibSource = Join-Path $legacyRoot "TechLib.cs"
$techLibDestination = Join-Path $thermodynamicsRoot "TechLib.cs"

if (Test-Path $techLibSource) {
    Move-Item -Path $techLibSource -Destination $techLibDestination -Force
}

$text = Read-Utf8File $techLibDestination
$text = $text.Replace(
    "namespace TechMES.Calc.Substances.Legacy",
    "namespace TechMES.Calc.Substances.Thermodynamics")
Write-Utf8File -Path $techLibDestination -Content $text

# -------------------------------------------------------------------------
# 5. ContentCalc -> существующая Substances/Content
# -------------------------------------------------------------------------
$contentCalcSource = Join-Path $legacyContentRoot "ContentCalc.cs"
$contentCalcDestination = Join-Path $contentRoot "ContentCalc.cs"

if (Test-Path $contentCalcSource) {
    Move-Item -Path $contentCalcSource -Destination $contentCalcDestination -Force
}

$text = Read-Utf8File $contentCalcDestination
$text = $text.Replace(
    "namespace TechMES.Calc.Substances.Legacy.Content",
    "namespace TechMES.Calc.Substances.Content")
$text = $text.Replace(
    "using TechMES.Calc.Substances.Legacy;" + [Environment]::NewLine,
    "")
$text = $text.Replace(
    "using TechMES.Calc.Substances.Legacy;" + "`n",
    "")
Write-Utf8File -Path $contentCalcDestination -Content $text

$contentPropertyPath = Join-Path $contentRoot "ContentPropertyCalculator.cs"
$text = Read-Utf8File $contentPropertyPath
$text = $text.Replace(
    "using TechMES.Calc.Substances.Legacy.Content;" + [Environment]::NewLine,
    "")
$text = $text.Replace(
    "using TechMES.Calc.Substances.Legacy.Content;" + "`n",
    "")
Write-Utf8File -Path $contentPropertyPath -Content $text

# -------------------------------------------------------------------------
# 6. SubstanceCatalog использует Components namespace.
# -------------------------------------------------------------------------
$catalogPath = Join-Path $substancesRoot "SubstanceCatalog.cs"
$text = Read-Utf8File $catalogPath
$text = $text.Replace(
    "using TechMES.Calc.Substances.Legacy;",
    "using TechMES.Calc.Substances.Components;")
Write-Utf8File -Path $catalogPath -Content $text

# Комментарий в MixturePropertyCalculator также обновляем.
$mixtureCalculatorPath = Join-Path $substancesRoot "MixturePropertyCalculator.cs"
$text = Read-Utf8File $mixtureCalculatorPath
$text = $text.Replace(
    "TechMES.Calc/Substances/Legacy",
    "TechMES.Calc/Substances/Components")
Write-Utf8File -Path $mixtureCalculatorPath -Content $text

# -------------------------------------------------------------------------
# 7. Удаляем WaterSteamProLib и оставшиеся пустые Legacy-папки.
# -------------------------------------------------------------------------
if (Test-Path $waterSteamRoot) {
    Remove-Item -Path $waterSteamRoot -Recurse -Force
}

if ((Test-Path $legacyContentRoot) -and
    -not (Get-ChildItem -Path $legacyContentRoot -Force | Select-Object -First 1)) {
    Remove-Item -Path $legacyContentRoot -Force
}

if ((Test-Path $legacyRoot) -and
    -not (Get-ChildItem -Path $legacyRoot -Force | Select-Object -First 1)) {
    Remove-Item -Path $legacyRoot -Force
}

# -------------------------------------------------------------------------
# 8. Regression test Nitrogen.
# -------------------------------------------------------------------------
# Оригинальный GetDensity принимает float temperature/pressure.
# Тест должен считать expected с тем же входным precision,
# иначе сравнивает legacy float-формулу с искусственной double-формулой.
$testPath = Join-Path $testsRoot "DensityCapacityDefinitionTests.cs"
$text = Read-Utf8File $testPath

$oldBlock = @'
    private static double NitrogenDensity(double temperatureC, double pressureBarAbsolute)
    {
        const double gasConstant = 8.3144598d;
        const double molarMass = 28.0134d;

        return pressureBarAbsolute * 100d
            / (gasConstant / molarMass)
            / (temperatureC + 273.15d);
    }
'@

$newBlock = @'
    private static double NitrogenDensity(double temperatureC, double pressureBarAbsolute)
    {
        const double gasConstant = 8.3144598d;
        const double molarMass = 28.0134d;

        // Оригинальный TechDotNetLib.GetDensity принимает float.
        // Expected обязан использовать тот же входной precision,
        // иначе тест проверяет уже другую, double-версию формулы.
        var legacyTemperature = (float)temperatureC;
        var legacyPressure = (float)pressureBarAbsolute;

        return legacyPressure * Math.Pow(10, 2)
            / (gasConstant / molarMass)
            / (legacyTemperature + 273.15);
    }
'@

if (-not $text.Contains($oldBlock)) {
    throw "NitrogenDensity test helper was not found in the expected form. Test file was not modified."
}

$text = $text.Replace($oldBlock, $newBlock)
Write-Utf8File -Path $testPath -Content $text

# -------------------------------------------------------------------------
# 9. Финальная проверка структуры.
# -------------------------------------------------------------------------
$forbiddenNamespaceRefs = Get-ChildItem -Path $calcRoot -Filter "*.cs" -File -Recurse |
    Select-String -Pattern "TechMES\.Calc\.Substances\.Legacy"

if ($forbiddenNamespaceRefs) {
    Write-Host ""
    Write-Host "Old Legacy namespace references remain:" -ForegroundColor Yellow
    $forbiddenNamespaceRefs | ForEach-Object {
        Write-Host "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Yellow
    }
    throw "Old TechMES.Calc.Substances.Legacy namespace references remain."
}

$waterSteamRefs = Get-ChildItem -Path $calcRoot -Filter "*.cs" -File -Recurse |
    Select-String -Pattern "WaterSteamProLib"

if ($waterSteamRefs) {
    Write-Host ""
    Write-Host "WaterSteamProLib references remain:" -ForegroundColor Yellow
    $waterSteamRefs | ForEach-Object {
        Write-Host "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" -ForegroundColor Yellow
    }
    throw "WaterSteamProLib references remain."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host ""
Write-Host "New structure:"
Write-Host "  TechMES.Calc\Substances\LegacySubstance.cs"
Write-Host "  TechMES.Calc\Substances\Components\*.cs"
Write-Host "  TechMES.Calc\Substances\Thermodynamics\TechLib.cs"
Write-Host "  TechMES.Calc\Substances\Content\ContentCalc.cs"
Write-Host ""
Write-Host "WaterSteamProLib removed."
Write-Host "Density Nitrogen regression expected value now uses original float input precision."
Write-Host ""
Write-Host "Next:"
Write-Host "  dotnet test .\TechMES.Calc.Tests\TechMES.Calc.Tests.csproj"
Write-Host ""
