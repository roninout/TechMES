param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

# Windows PowerShell 5.1 correctly reads the Russian source literals in this
# script because this file is saved as UTF-8 WITH BOM.
#
# Always run relative to the folder containing this script.
$ProjectRoot = $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    throw "Unable to determine script directory."
}

Set-Location $ProjectRoot

$ExpectedBranch = "feature/techmes-calc"
$ExpectedHead = "b342bd8cebfc6178a43ba42ba054bfc8e2ece513"

function Write-RepoFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $fullPath = Join-Path (Get-Location) $Path
    $directory = Split-Path -Parent $fullPath

    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    # Embedded source is kept as LF in this script.
    # On Windows write normal CRLF so the patch does not create unnecessary
    # line-ending noise in the repository.
    $normalized = $Content.Replace("`r`n", "`n")
    $platformText = $normalized.Replace("`n", [Environment]::NewLine)

    [System.IO.File]::WriteAllText(
        $fullPath,
        $platformText,
        [System.Text.UTF8Encoding]::new($false))
}

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$OldText,
        [Parameter(Mandatory = $true)][string]$NewText
    )

    $fullPath = Join-Path (Get-Location) $Path

    if (-not (Test-Path $fullPath)) {
        throw "File not found: $Path"
    }

    $source = [System.IO.File]::ReadAllText($fullPath)
    $newline = if ($source.Contains("`r`n")) { "`r`n" } else { "`n" }

    $sourceNormalized = $source.Replace("`r`n", "`n")
    $oldNormalized = $OldText.Replace("`r`n", "`n")
    $newNormalized = $NewText.Replace("`r`n", "`n")

    if (-not $sourceNormalized.Contains($oldNormalized)) {
        throw "Expected block was not found in $Path. The branch may have changed."
    }

    $updatedNormalized = $sourceNormalized.Replace($oldNormalized, $newNormalized)
    $updated = $updatedNormalized.Replace("`n", $newline)

    [System.IO.File]::WriteAllText(
        $fullPath,
        $updated,
        [System.Text.UTF8Encoding]::new($false))
}

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------

if (-not (Test-Path ".git")) {
    throw "Run this script from the TechMES repository root."
}

$currentBranch = (git branch --show-current).Trim()
if ($currentBranch -ne $ExpectedBranch) {
    throw "Expected branch '$ExpectedBranch', current branch is '$currentBranch'."
}

$currentHead = (git rev-parse HEAD).Trim()
if ($currentHead -ne $ExpectedHead) {
    throw "Expected HEAD $ExpectedHead, current HEAD is $currentHead. Do not apply this patch to another revision."
}

# Ignore only the patcher scripts themselves. Any source-code change must
# still stop the patch so we never overwrite user's work.
$dirty = @(git status --porcelain) | Where-Object {
    $_ -notmatch '^\?\?\s+Apply-CapacityCoreStage.*\.ps1$'
}

if ($dirty.Count -gt 0) {
    Write-Host ""
    Write-Host "Uncommitted repository changes:" -ForegroundColor Yellow
    $dirty | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }

    throw "Working tree is not clean. Commit/stash local changes before applying the Capacity Core stage."
}

Write-Host "Applying Capacity Core stage to $currentBranch @ $currentHead..." -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 1. Substance capability metadata
# ---------------------------------------------------------------------------

Write-RepoFile "TechMES.Calc/Substances/SubstancePropertySupport.cs" @'
namespace TechMES.Calc.Substances;

/// <summary>
/// Физические свойства, для которых конкретная legacy-модель вещества
/// разрешена в соответствующем TechMES Calculation Definition.
///
/// Наличие старого метода GetDensity/GetCapacity/GetContent само по себе
/// ещё не означает, что свойство должно быть доступно Production UI.
/// Например, формула может иметь старый нестандартный контракт единиц
/// либо возвращать legacy sentinel вместо реального значения.
/// </summary>
[Flags]
public enum SubstancePropertySupport
{
    None = 0,

    /// <summary>
    /// Density calculation.
    /// </summary>
    Density = 1 << 0,

    /// <summary>
    /// Specific heat capacity calculation.
    /// </summary>
    SpecificHeatCapacity = 1 << 1,

    /// <summary>
    /// Content calculation.
    /// Для Content фактическая поддержка дополнительно зависит
    /// от допустимой комбинации компонентов.
    /// </summary>
    Content = 1 << 2
}
'@

Write-RepoFile "TechMES.Calc/Substances/SubstanceDescriptor.cs" @'
namespace TechMES.Calc.Substances;

/// <summary>
/// Stable description of one substance code inherited from TechDotNetLib.
/// Code is the value historically stored in COMP/PERC configuration.
///
/// SupportedProperties отделяет сам факт наличия legacy-класса
/// от разрешения использовать его в конкретном Production Calculation Definition.
/// </summary>
public sealed record SubstanceDescriptor(
    string Code,
    string Name,
    SubstancePhase Phase,
    SubstancePropertySupport SupportedProperties)
{
    public bool Supports(SubstancePropertySupport property)
    {
        return property != SubstancePropertySupport.None
            && (SupportedProperties & property) == property;
    }
}
'@

Write-RepoFile "TechMES.Calc/Substances/SubstanceCatalog.cs" @'
using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances.Components;

namespace TechMES.Calc.Substances;

/// <summary>
/// Catalog of substance codes used by the former TechDotNetLib.
///
/// The catalog owns:
/// - code -> formula-model mapping;
/// - physical phase;
/// - explicit property capabilities used by Calculation Definitions.
///
/// It does not know anything about SCADA tags, PostgreSQL or Calc Jobs.
/// </summary>
public static class SubstanceCatalog
{
    private const SubstancePropertySupport DefaultPropertySupport =
        SubstancePropertySupport.Density |
        SubstancePropertySupport.SpecificHeatCapacity;

    private sealed record Entry(SubstanceDescriptor Descriptor, Func<LegacySubstance> Factory);

    private static readonly IReadOnlyDictionary<string, Entry> Entries = CreateEntries();

    public static IReadOnlyList<SubstanceDescriptor> Items { get; } = Entries.Values
        .Select(entry => entry.Descriptor)
        .OrderBy(item => item.Code, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Возвращает только вещества, явно разрешённые для указанного свойства.
    ///
    /// Это основной источник options для property-specific Calculation Definition.
    /// Благодаря этому WEB не должен угадывать поддержку вещества по имени,
    /// фазе или пробному выполнению legacy-формулы.
    /// </summary>
    public static IReadOnlyList<SubstanceDescriptor> GetSupported(SubstancePropertySupport property)
    {
        if (property == SubstancePropertySupport.None)
            return [];

        return Items
            .Where(item => item.Supports(property))
            .ToArray();
    }

    public static bool TryGet(string? code, out SubstanceDescriptor descriptor)
    {
        if (!string.IsNullOrWhiteSpace(code) && Entries.TryGetValue(code.Trim(), out var entry))
        {
            descriptor = entry.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    public static SubstanceDescriptor GetRequired(string code)
    {
        if (TryGet(code, out var descriptor))
            return descriptor;

        throw new CalculationException("substance.unknown", $"Unknown substance code '{code}'.");
    }

    internal static LegacySubstance CreateRequiredModel(string code)
    {
        if (!string.IsNullOrWhiteSpace(code) && Entries.TryGetValue(code.Trim(), out var entry))
            return entry.Factory();

        throw new CalculationException("substance.unknown", $"Unknown substance code '{code}'.");
    }

    private static IReadOnlyDictionary<string, Entry> CreateEntries()
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        void Add(
            string code,
            string name,
            SubstancePhase phase,
            Func<LegacySubstance> factory,
            SubstancePropertySupport supportedProperties = DefaultPropertySupport)
        {
            result.Add(
                code,
                new Entry(
                    new SubstanceDescriptor(code, name, phase, supportedProperties),
                    factory));
        }

        Add("ALC", "Alcohol", SubstancePhase.Liquid, () => new Alcohol(false));
        Add("ALCS", "Alcohol", SubstancePhase.Vapor, () => new Alcohol(true));

        Add("ACA", "Acetaldehyde", SubstancePhase.Liquid, () => new Acetaldehyde(false));
        Add("ACAS", "Acetaldehyde", SubstancePhase.Vapor, () => new Acetaldehyde(true));

        Add("ACN", "Acetonitrile", SubstancePhase.Liquid, () => new Acetonitrile(false));
        Add("ACNS", "Acetonitrile", SubstancePhase.Vapor, () => new Acetonitrile(true));

        Add("HP", "Hydrogen peroxide", SubstancePhase.Liquid, () => new HydrohenPeroxyde(false));
        Add("HPS", "Hydrogen peroxide", SubstancePhase.Vapor, () => new HydrohenPeroxyde(true));

        Add("N", "Nitrogen", SubstancePhase.Vapor, () => new Nitrogen());
        Add("O2", "Oxygen", SubstancePhase.Vapor, () => new Oxygen());

        Add("P", "Propylene", SubstancePhase.Liquid, () => new Propylene(false));
        Add("PS", "Propylene", SubstancePhase.Vapor, () => new Propylene(true));

        Add("PO", "Propylene oxide", SubstancePhase.Liquid, () => new PropyleneOxyde(false));
        Add("POS", "Propylene oxide", SubstancePhase.Vapor, () => new PropyleneOxyde(true));

        Add("Water", "Water", SubstancePhase.Liquid, () => new Water(false));
        Add("WaterS", "Water", SubstancePhase.Vapor, () => new Water(true));

        // DryMatter имеет восстановленную Density ICUMSA-корреляцию,
        // но Capacity пока не реализована.
        Add(
            "DryMatter",
            "Dry matter",
            SubstancePhase.Liquid,
            () => new DryMatter(),
            SubstancePropertySupport.Density);

        Add("Butadiene_1_2", "1,2-Butadiene", SubstancePhase.Liquid, () => new Butadiene_1_2(false));
        Add("Butadiene_1_2S", "1,2-Butadiene", SubstancePhase.Vapor, () => new Butadiene_1_2(true));

        Add("Butadiene_1_3", "1,3-Butadiene", SubstancePhase.Liquid, () => new Butadiene_1_3(false));
        Add("Butadiene_1_3S", "1,3-Butadiene", SubstancePhase.Vapor, () => new Butadiene_1_3(true));

        Add("Butene_1", "1-Butene", SubstancePhase.Liquid, () => new Butene_1(false));
        Add("Butene_1S", "1-Butene", SubstancePhase.Vapor, () => new Butene_1(true));

        Add("Cis-2-Butene", "cis-2-Butene", SubstancePhase.Liquid, () => new Cis_2_Butene(false));
        Add("Cis-2-ButeneS", "cis-2-Butene", SubstancePhase.Vapor, () => new Cis_2_Butene(true));

        Add("Ethane", "Ethane", SubstancePhase.Liquid, () => new Ethane(false));
        Add("EthaneS", "Ethane", SubstancePhase.Vapor, () => new Ethane(true));

        Add("Ethylene", "Ethylene", SubstancePhase.Liquid, () => new Ethylene(false));
        Add("EthyleneS", "Ethylene", SubstancePhase.Vapor, () => new Ethylene(true));

        Add("Isobutane", "Isobutane", SubstancePhase.Liquid, () => new Isobutane(false));
        Add("IsobutaneS", "Isobutane", SubstancePhase.Vapor, () => new Isobutane(true));

        Add("Methyl-Acetylene", "Methyl acetylene", SubstancePhase.Liquid, () => new Methyl_Acetylene(false));
        Add("Methyl-AcetyleneS", "Methyl acetylene", SubstancePhase.Vapor, () => new Methyl_Acetylene(true));

        Add("n-Butane", "n-Butane", SubstancePhase.Liquid, () => new N_Butane(false));
        Add("n-ButaneS", "n-Butane", SubstancePhase.Vapor, () => new N_Butane(true));

        Add("n-Pentane", "n-Pentane", SubstancePhase.Liquid, () => new N_Pentane(false));
        Add("n-PentaneS", "n-Pentane", SubstancePhase.Vapor, () => new N_Pentane(true));

        Add("Propadiene", "Propadiene", SubstancePhase.Liquid, () => new Propadiene(false));
        Add("PropadieneS", "Propadiene", SubstancePhase.Vapor, () => new Propadiene(true));

        Add("Pr", "Propane", SubstancePhase.Liquid, () => new Propane(false));
        Add("PrS", "Propane", SubstancePhase.Vapor, () => new Propane(true));

        Add("Trans-2-Butene", "trans-2-Butene", SubstancePhase.Liquid, () => new Trans_2_Butene(false));
        Add("Trans-2-ButeneS", "trans-2-Butene", SubstancePhase.Vapor, () => new Trans_2_Butene(true));

        Add("Vinylacetylene", "Vinylacetylene", SubstancePhase.Liquid, () => new Vinylacetylene(false));
        Add("VinylacetyleneS", "Vinylacetylene", SubstancePhase.Vapor, () => new Vinylacetylene(true));

        Add("Freezium", "Freezium", SubstancePhase.Liquid, () => new Freezium(false));
        Add("Ethanol", "Ethanol", SubstancePhase.Liquid, () => new Ethanol(false));
        Add("Addition", "Addition (legacy Ethanol model)", SubstancePhase.Liquid, () => new Ethanol(false));
        Add("Diesel", "Diesel", SubstancePhase.Liquid, () => new Diesel(false));

        Add("NaOH", "Sodium hydroxide", SubstancePhase.Liquid, () => new NaOH(false));
        Add("NaOHS", "Sodium hydroxide", SubstancePhase.Vapor, () => new NaOH(true));

        Add("HCL", "Hydrochloric acid", SubstancePhase.Liquid, () => new HCL(false));
        Add("HCLS", "Hydrochloric acid", SubstancePhase.Vapor, () => new HCL(true));

        // Methan Capacity использует собственный legacy temperature contract (K),
        // а Fusel.GetCapacity возвращает legacy sentinel -1.
        //
        // Density пока оставляем доступной для обратной совместимости существующего
        // Density ядра и regression-тестов. Capacity Definition эти два вещества
        // больше не показывает и Calculation Core их явно отклоняет.
        Add(
            "Methan",
            "Methane (legacy native K/Pa model)",
            SubstancePhase.Vapor,
            () => new Methan(true),
            SubstancePropertySupport.Density);

        Add(
            "Fusel",
            "Fusel oil (legacy native K/Pa model)",
            SubstancePhase.Liquid,
            () => new Fusel(false),
            SubstancePropertySupport.Density);

        return result;
    }
}
'@

# ---------------------------------------------------------------------------
# 2. Mixture Definition base: property-specific component options
# ---------------------------------------------------------------------------

Write-RepoFile "TechMES.Calc/Mixtures/MixtureCalculationDefinitionBase.cs" @'
using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Mixtures;

/// <summary>
/// Общая база для расчётов физических свойств многокомпонентной смеси.
///
/// Сейчас её используют Density и Capacity.
/// В дальнейшем эту же инфраструктуру можно использовать для других
/// расчётов свойств смеси, если у них будет аналогичная конфигурация компонентов.
///
/// Важно разделять два разных понятия:
///
/// 1. Физические входные параметры расчёта.
///    Например:
///    - Temperature;
///    - Pressure;
///    - в будущем Humidity, Concentration, Compressibility и т.д.
///
///    Их количество здесь НЕ ограничено.
///    Конкретный CalculationDefinition передаёт обычный список
///    CalculationParameterDefinition любой длины.
///
/// 2. Компоненты смеси.
///    Для них сейчас намеренно сохраняется максимум 5 компонентов,
///    потому что текущая структура Equipment в Plant SCADA содержит:
///
///    COMP_N
///    PERC_0
///    PERC_1
///    PERC_2
///    PERC_3
///    PERC_4
///
///    Это ограничение текущего SCADA-контракта смеси,
///    а не ограничение Calculation Engine.
/// </summary>
public abstract class MixtureCalculationDefinitionBase : CalculationDefinitionBase
{
    protected const int MaxComponentCount = 5;
    protected const string ComponentCountKey = "componentCount";

    /// <summary>
    /// Создаёт полный набор параметров Calculation Definition.
    ///
    /// propertyParameters содержит параметры конкретного физического свойства.
    ///
    /// requiredSubstanceProperty позволяет property-specific Definition
    /// показывать только вещества, для которых соответствующая физическая
    /// формула разрешена SubstanceCatalog.
    ///
    /// Если фильтр не указан, сохраняется прежнее поведение и доступны
    /// все SubstanceCatalog.Items. Это важно для поэтапной миграции Density.
    ///
    /// После физических параметров автоматически добавляется конфигурация смеси:
    /// componentCount и пять возможных componentNCode/componentNPercent.
    ///
    /// Ограничение в пять относится исключительно к текущей структуре
    /// компонентов смеси в Plant SCADA и не относится к ProcessInput.
    /// </summary>
    protected static IReadOnlyList<CalculationParameterDefinition> CreateMixtureParameters(
        IReadOnlyList<CalculationParameterDefinition> propertyParameters,
        SubstancePropertySupport? requiredSubstanceProperty = null)
    {
        ArgumentNullException.ThrowIfNull(propertyParameters);

        var result = new List<CalculationParameterDefinition>(propertyParameters.Count + 11);
        result.AddRange(propertyParameters);

        result.Add(new CalculationParameterDefinition(
            Key: ComponentCountKey,
            Name: "Component count",
            Type: CalculationParameterType.Integer,
            IsRequired: true,
            Minimum: 1,
            Maximum: MaxComponentCount,
            Step: 1,
            Decimals: 0,
            Order: 100,
            Description: "Number of active mixture components. Current SCADA structure supports from 1 to 5 components."));

        var substances = requiredSubstanceProperty.HasValue
            ? SubstanceCatalog.GetSupported(requiredSubstanceProperty.Value)
            : SubstanceCatalog.Items;

        // Для Substance Selection передаём фазу отдельным структурированным полем.
        // Name остаётся только пользовательским отображением.
        // Для фильтрации Liquid/Vapour используется исключительно Option.Phase.
        var substanceOptions = substances
            .Select(item => new CalculationParameterOption(
                item.Code,
                $"{item.Code} — {item.Name} ({GetPhaseName(item.Phase)})",
                GetPhaseName(item.Phase)))
            .ToArray();

        // Внутренние ключи компонентов оставляем 0-based:
        //
        // component0Percent -> PERC_0
        // ...
        // component4Percent -> PERC_4
        //
        // Пользователю при этом показываются обычные номера 1..5.
        for (var index = 0; index < MaxComponentCount; index++)
        {
            var displayIndex = index + 1;

            result.Add(new CalculationParameterDefinition(
                Key: GetComponentCodeKey(index),
                Name: $"Component {displayIndex}",
                Type: CalculationParameterType.Selection,
                IsRequired: false,
                Order: 110 + index * 10,
                Description: $"Substance used as mixture component {displayIndex}.",
                Options: substanceOptions));

            result.Add(new CalculationParameterDefinition(
                Key: GetComponentPercentKey(index),
                Name: $"Component {displayIndex} mass percent",
                Type: CalculationParameterType.Number,
                Unit: "%",
                IsRequired: false,
                Minimum: 0,
                Maximum: 100,
                Step: 0.1,
                Decimals: 3,
                Order: 111 + index * 10,
                Description: $"Mass percentage of mixture component {displayIndex}."));
        }

        return result;
    }

    /// <summary>
    /// Создаёт фактический список компонентов смеси из CalculationParameterSet.
    ///
    /// componentCount определяет, сколько первых componentNCode/componentNPercent
    /// действительно участвуют в расчёте.
    ///
    /// Остальные слоты не используются. Это позволяет одному Definition
    /// работать со смесями от одного до пяти компонентов.
    ///
    /// Хотя componentNCode и componentNPercent объявлены как IsRequired=false,
    /// для активных компонентов они становятся обязательными здесь.
    ///
    /// Такая условная обязательность не может быть выражена простым
    /// CalculationParameterDefinition.IsRequired, потому что она зависит
    /// от фактического значения componentCount.
    /// </summary>
    protected static IReadOnlyList<MixtureComponent> ReadMixtureComponents(CalculationParameterSet parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var componentCount = parameters.GetRequiredInt(ComponentCountKey);

        if (componentCount < 1 || componentCount > MaxComponentCount)
        {
            throw new CalculationException(
                "mixture.component-count.invalid",
                $"Mixture component count must be between 1 and {MaxComponentCount}.");
        }

        var components = new List<MixtureComponent>(componentCount);

        for (var index = 0; index < componentCount; index++)
        {
            var codeKey = GetComponentCodeKey(index);
            var percentKey = GetComponentPercentKey(index);

            if (!parameters.TryGetValue(codeKey, out var rawCode) || rawCode is null)
            {
                throw new CalculationException(
                    "mixture.component.code-missing",
                    $"Substance code for mixture component {index + 1} is missing.");
            }

            if (!parameters.TryGetValue(percentKey, out var rawPercent) || rawPercent is null)
            {
                throw new CalculationException(
                    "mixture.component.percent-missing",
                    $"Mass percentage for mixture component {index + 1} is missing.");
            }

            var code = parameters.GetRequiredString(codeKey);
            var percent = parameters.GetRequiredDouble(percentKey);

            components.Add(new MixtureComponent(code, percent));
        }

        return components;
    }

    /// <summary>
    /// Добавляет в Trace полную фактическую конфигурацию смеси.
    /// </summary>
    protected static void AddMixtureTrace(
        ICollection<CalculationTraceItem> trace,
        IReadOnlyList<MixtureComponent> components)
    {
        trace.Add(new CalculationTraceItem(
            "componentCount",
            "Component count",
            components.Count.ToString(CultureInfo.InvariantCulture)));

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            trace.Add(new CalculationTraceItem(
                $"component{index}Code",
                $"Component {index + 1}",
                component.SubstanceCode));

            trace.Add(new CalculationTraceItem(
                $"component{index}Percent",
                $"Component {index + 1} mass percent",
                Format(component.MassPercent),
                "%"));
        }
    }

    protected static string GetComponentCodeKey(int index)
    {
        return $"component{index}Code";
    }

    protected static string GetComponentPercentKey(int index)
    {
        return $"component{index}Percent";
    }

    protected static string Format(double value)
    {
        return value.ToString("0.############", CultureInfo.InvariantCulture);
    }

    private static string GetPhaseName(SubstancePhase phase)
    {
        return phase switch
        {
            SubstancePhase.Liquid => "liquid",
            SubstancePhase.Vapor => "vapor",
            _ => phase.ToString()
        };
    }
}
'@

# ---------------------------------------------------------------------------
# 3. Capacity calculation result + shared calculator
# ---------------------------------------------------------------------------

Write-RepoFile "TechMES.Calc/Mixtures/MixtureCapacityCalculationResult.cs" @'
namespace TechMES.Calc.Mixtures;

/// <summary>
/// Результат Specific Heat Capacity для одного фактически участвующего
/// компонента смеси.
///
/// Index соответствует исходному componentN/PercN слоту.
/// SpecificHeatCapacityJPerKgK - чистая теплоёмкость компонента
/// в нормализованных единицах J/(kg·K), реально использованная при смешении.
/// </summary>
public sealed record MixtureCapacityComponentResult(
    int Index,
    string SubstanceCode,
    double MassPercent,
    double SpecificHeatCapacityJPerKgK);

/// <summary>
/// Полный результат Capacity смеси.
///
/// SpecificHeatCapacityJPerKgK - итоговая теплоёмкость смеси до DeltaC.
/// Components - фактические Cp участвующих компонентов.
/// </summary>
public sealed record MixtureCapacityCalculationResult(
    double SpecificHeatCapacityJPerKgK,
    IReadOnlyList<MixtureCapacityComponentResult> Components);
'@

Write-RepoFile "TechMES.Calc/Mixtures/MixturePropertyCalculator.cs" @'
using TechMES.Calc.Exceptions;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Mixtures;

/// <summary>
/// Выполняет расчёт физических свойств смеси по массовым долям компонентов.
///
/// Формулы отдельных веществ находятся в отдельных файлах
/// TechMES.Calc/Substances/Components и перенесены из TechDotNetLib.
///
/// Этот класс отвечает только за формулу смеси и единицы нового Calc-контракта:
/// - Density возвращается в kg/m³ без старого SCADA scaling ×10;
/// - Capacity возвращается в J/(kg·K);
/// - дополнительные ProcessInput передаются компонентам без изменения
///   старых GetDensity/GetCapacity/GetContent.
/// </summary>
public static class MixturePropertyCalculator
{
    private const double PercentageTolerance = 1e-6;

    public static double CalculateDensityKgPerM3(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        double pressureBarAbsolute,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        return CalculateDensity(
            components,
            temperatureC,
            pressureBarAbsolute,
            additionalParameters).DensityKgPerM3;
    }

    /// <summary>
    /// Рассчитывает Density смеси:
    ///
    ///     rho = 1 / Σ(w_i / rho_i)
    ///
    /// Density-specific проверки находятся именно здесь:
    /// - корректное абсолютное давление;
    /// - специальный контракт DryMatter / ICUMSA.
    ///
    /// Благодаря этому Capacity больше не наследует Density-only правила.
    /// </summary>
    public static MixtureDensityCalculationResult CalculateDensity(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        double pressureBarAbsolute,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateCommonInputs(components, temperatureC);
        ValidateAbsolutePressure(pressureBarAbsolute);
        ValidateDryMatterComposition(components);

        var denominator = 0d;
        var componentResults = new List<MixtureDensityComponentResult>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            if (component.MassPercent == 0d)
                continue;

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);

            var componentDensity = model.GetDensity(
                (float)temperatureC,
                (float)pressureBarAbsolute,
                component.MassPercent,
                additionalParameters);

            if (!double.IsFinite(componentDensity) || componentDensity <= 0d)
            {
                throw new CalculationException(
                    "substance.density.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid density {componentDensity}.");
            }

            denominator += component.MassPercent * 0.01d / componentDensity;

            componentResults.Add(new MixtureDensityComponentResult(
                Index: index,
                SubstanceCode: component.SubstanceCode,
                MassPercent: component.MassPercent,
                DensityKgPerM3: componentDensity));
        }

        if (!double.IsFinite(denominator) || denominator <= 0d)
        {
            throw new CalculationException(
                "mixture.density.invalid-denominator",
                "Mixture density denominator must be greater than zero.");
        }

        var density = 1d / denominator;

        if (!double.IsFinite(density) || density <= 0d)
        {
            throw new CalculationException(
                "mixture.density.invalid-result",
                "Calculated mixture density is invalid.");
        }

        return new MixtureDensityCalculationResult(density, componentResults);
    }

    /// <summary>
    /// Короткий совместимый API, возвращающий только итоговую Capacity смеси.
    /// Полный вариант CalculateSpecificHeatCapacity дополнительно возвращает
    /// фактическую Cp каждого компонента.
    /// </summary>
    public static double CalculateSpecificHeatCapacityJPerKgK(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        return CalculateSpecificHeatCapacity(
            components,
            temperatureC,
            additionalParameters).SpecificHeatCapacityJPerKgK;
    }

    /// <summary>
    /// Рассчитывает удельную теплоёмкость смеси в J/(kg·K).
    ///
    /// Формула соответствует старому TechDotNetLib.Mix:
    ///
    ///     Cp = Σ(w_i × Cp_i)
    ///
    /// Legacy GetCapacity() возвращает kJ/(kg·K).
    /// В нормализованный TechMES contract каждый component Cp и итог смеси
    /// переводятся в J/(kg·K) через ×1000.
    ///
    /// В отличие от Density:
    /// - Pressure здесь не валидируется и не используется;
    /// - DryMatter/ICUMSA composition rule здесь не применяется;
    /// - поддержка Capacity проверяется отдельной capability metadata.
    /// </summary>
    public static MixtureCapacityCalculationResult CalculateSpecificHeatCapacity(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC,
        IReadOnlyDictionary<string, double>? additionalParameters = null)
    {
        ValidateCommonInputs(components, temperatureC);

        var capacityKjPerKgK = 0d;
        var componentResults = new List<MixtureCapacityComponentResult>();

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];

            // Неактивный компонент не определяет возможность текущего расчёта.
            // Это сохраняет то же поведение, которое уже используется Density.
            if (component.MassPercent == 0d)
                continue;

            var descriptor = SubstanceCatalog.GetRequired(component.SubstanceCode);

            if (!descriptor.Supports(SubstancePropertySupport.SpecificHeatCapacity))
            {
                throw new CalculationException(
                    "substance.capacity.unsupported",
                    $"Substance '{component.SubstanceCode}' is not supported by the normalized specific heat capacity calculation.");
            }

            var model = SubstanceCatalog.CreateRequiredModel(component.SubstanceCode);
            var pureCapacityKjPerKgK = model.GetCapacity(
                (float)temperatureC,
                additionalParameters);

            if (!double.IsFinite(pureCapacityKjPerKgK) || pureCapacityKjPerKgK <= 0d)
            {
                throw new CalculationException(
                    "substance.capacity.invalid",
                    $"Substance '{component.SubstanceCode}' returned invalid heat capacity {pureCapacityKjPerKgK}.");
            }

            capacityKjPerKgK +=
                component.MassPercent * 0.01d * pureCapacityKjPerKgK;

            componentResults.Add(new MixtureCapacityComponentResult(
                Index: index,
                SubstanceCode: component.SubstanceCode,
                MassPercent: component.MassPercent,
                SpecificHeatCapacityJPerKgK: pureCapacityKjPerKgK * 1000d));
        }

        var capacityJPerKgK = capacityKjPerKgK * 1000d;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
        {
            throw new CalculationException(
                "substance.capacity.invalid-result",
                "Calculated mixture heat capacity is invalid.");
        }

        return new MixtureCapacityCalculationResult(
            capacityJPerKgK,
            componentResults);
    }

    /// <summary>
    /// Общая для Density/Capacity структурная проверка смеси.
    ///
    /// Здесь остаются только действительно общие правила:
    /// - смесь не пустая;
    /// - Temperature конечна;
    /// - коды существуют и не повторяются;
    /// - проценты лежат в 0..100 и дают 100%;
    /// - активные компоненты принадлежат одной фазе.
    ///
    /// Property-specific правила выполняются конкретным расчётом отдельно.
    /// </summary>
    private static void ValidateCommonInputs(
        IReadOnlyList<MixtureComponent> components,
        double temperatureC)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Count == 0)
        {
            throw new CalculationException(
                "mixture.components.empty",
                "At least one mixture component is required.");
        }

        if (!double.IsFinite(temperatureC))
        {
            throw new CalculationException(
                "mixture.temperature.invalid",
                "Mixture temperature must be a finite number.");
        }

        var totalPercent = 0d;
        var usedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.SubstanceCode))
            {
                throw new CalculationException(
                    "mixture.component.code-empty",
                    "Mixture component substance code cannot be empty.");
            }

            var code = component.SubstanceCode.Trim();

            if (!usedCodes.Add(code))
            {
                throw new CalculationException(
                    "mixture.component.duplicate",
                    $"Substance '{code}' is specified more than once.");
            }

            // Существование кода проверяем даже для 0%.
            SubstanceCatalog.GetRequired(code);

            if (!double.IsFinite(component.MassPercent)
                || component.MassPercent < 0d
                || component.MassPercent > 100d)
            {
                throw new CalculationException(
                    "mixture.component.percent-invalid",
                    $"Mass percent for '{code}' must be between 0 and 100.");
            }

            totalPercent += component.MassPercent;
        }

        if (Math.Abs(totalPercent - 100d) > PercentageTolerance)
        {
            throw new CalculationException(
                "mixture.percent-total-invalid",
                $"Mixture mass percentages must total 100%. Actual total: {totalPercent:0.######}%.");
        }

        ValidateSinglePhaseComposition(components);
    }

    private static void ValidateAbsolutePressure(double pressureBarAbsolute)
    {
        if (!double.IsFinite(pressureBarAbsolute) || pressureBarAbsolute <= 0d)
        {
            throw new CalculationException(
                "mixture.pressure.invalid",
                "Absolute pressure must be a finite number greater than zero.");
        }
    }

    private static void ValidateSinglePhaseComposition(
        IReadOnlyList<MixtureComponent> components)
    {
        SubstancePhase? mixturePhase = null;

        foreach (var component in components.Where(component => component.MassPercent > 0d))
        {
            var descriptor = SubstanceCatalog.GetRequired(component.SubstanceCode);

            if (!mixturePhase.HasValue)
            {
                mixturePhase = descriptor.Phase;
                continue;
            }

            if (descriptor.Phase != mixturePhase.Value)
            {
                throw new CalculationException(
                    "mixture.phase-mixed",
                    "Liquid and vapor components cannot be mixed in the same calculation.");
            }
        }
    }

    /// <summary>
    /// Density-only контракт DryMatter.
    ///
    /// Корреляция восстановлена из PLC-расчёта сахарного водного раствора,
    /// поэтому поддерживаются только:
    /// - DryMatter = 100%;
    /// - Water + DryMatter = 100%.
    ///
    /// Capacity эту проверку намеренно не вызывает.
    /// </summary>
    private static void ValidateDryMatterComposition(
        IReadOnlyList<MixtureComponent> components)
    {
        var activeComponents = components
            .Where(component => component.MassPercent > 0d)
            .ToArray();

        var dryMatter = activeComponents.FirstOrDefault(component =>
            string.Equals(
                component.SubstanceCode,
                "DryMatter",
                StringComparison.OrdinalIgnoreCase));

        if (dryMatter is null)
            return;

        if (activeComponents.Length == 1
            && string.Equals(
                activeComponents[0].SubstanceCode,
                "DryMatter",
                StringComparison.OrdinalIgnoreCase)
            && Math.Abs(activeComponents[0].MassPercent - 100d) <= PercentageTolerance)
        {
            return;
        }

        if (activeComponents.Length != 2)
        {
            throw new CalculationException(
                "mixture.drymatter.unsupported-combination",
                "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");
        }

        var hasWater = activeComponents.Any(component =>
            string.Equals(
                component.SubstanceCode,
                "Water",
                StringComparison.OrdinalIgnoreCase));

        var hasOnlySupportedComponents = activeComponents.All(component =>
            string.Equals(
                component.SubstanceCode,
                "Water",
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                component.SubstanceCode,
                "DryMatter",
                StringComparison.OrdinalIgnoreCase));

        if (!hasWater || !hasOnlySupportedComponents)
        {
            throw new CalculationException(
                "mixture.drymatter.unsupported-combination",
                "DryMatter density correlation supports only pure DryMatter or a Water + DryMatter mixture.");
        }
    }
}
'@

# ---------------------------------------------------------------------------
# 4. Capacity Definition v2
# ---------------------------------------------------------------------------

Write-RepoFile "TechMES.Calc/Capacity/CapacityCalculationDefinition.cs" @'
using TechMES.Calc.Mixtures;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;
using TechMES.Calc.Substances;

namespace TechMES.Calc.Capacity;

/// <summary>
/// Расчёт удельной теплоёмкости многокомпонентной смеси.
///
/// По структуре Calculation Job Capacity намеренно повторяет Density:
/// - Temperature;
/// - optional Pressure;
/// - до трёх дополнительных ProcessInput;
/// - CompN / Perc0..Perc4;
/// - component0Code..component4Code;
/// - DeltaC;
/// - один основной SCADA output Capacity.
///
/// Математика смеси при этом своя:
///
///     Cp = Σ(w_i × Cp_i)
///
/// Legacy GetCapacity возвращает kJ/(kg·K), а MixturePropertyCalculator
/// нормализует компонентные и итоговый результаты в J/(kg·K).
/// </summary>
public sealed class CapacityCalculationDefinition : MixtureCalculationDefinitionBase
{
    public const string DefinitionCode = "mixture.capacity";

    private const string TemperatureKey = "temperatureC";
    private const string PressureKey = "pressureBarAbsolute";
    private const string CorrectionKey = "capacityCorrection";

    private static readonly string[] AdditionalParameterKeys =
    [
        "additionalParameter1",
        "additionalParameter2",
        "additionalParameter3"
    ];

    private static readonly IReadOnlyList<CalculationParameterDefinition> PropertyParameterDefinitions =
    [
        new CalculationParameterDefinition(
            Key: TemperatureKey,
            Name: "Temperature",
            Type: CalculationParameterType.Number,
            Unit: "°C",
            IsRequired: true,
            Minimum: -273.15,
            Step: 0.1,
            Decimals: 2,
            Order: 1,
            Description: "Mixture temperature used by the substance heat-capacity correlations.",
            Role: CalculationParameterRole.ProcessInput),

        // Pressure присутствовал в старом Capacity object, но legacy Mix.GetCapacity
        // его фактически не использовал.
        //
        // Пока оставляем полноценный optional ProcessInput с DefaultValue:
        // WEB получает тот же ProcessInput lifecycle, что и Density,
        // а текущая формула не создаёт ложную обязательную зависимость.
        new CalculationParameterDefinition(
            Key: PressureKey,
            Name: "Absolute pressure",
            Type: CalculationParameterType.Number,
            Unit: "bar(abs)",
            IsRequired: false,
            DefaultValue: 1.01325d,
            Minimum: 0.000001,
            Step: 0.01,
            Decimals: 4,
            Order: 2,
            Description: "Reserved absolute pressure input. Current legacy Capacity correlations depend only on Temperature.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter1",
            Name: "Additional parameter",
            Type: CalculationParameterType.Number,
            IsRequired: false,
            Order: 3,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter2",
            Name: "Additional parameter",
            Type: CalculationParameterType.Number,
            IsRequired: false,
            Order: 4,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        new CalculationParameterDefinition(
            Key: "additionalParameter3",
            Name: "Additional parameter",
            Type: CalculationParameterType.Number,
            IsRequired: false,
            Order: 5,
            Description: "Reserved additional process parameter.",
            Role: CalculationParameterRole.ProcessInput),

        // Legacy DELTA_C:
        //
        // OPC read scaling ×10 и последующее ×0.1 в старом расчёте
        // взаимно уничтожались. Поэтому новый Job хранит DeltaC сразу
        // как инженерное J/(kg·K) значение.
        new CalculationParameterDefinition(
            Key: CorrectionKey,
            Name: "DeltaC",
            Type: CalculationParameterType.Number,
            Unit: "J/(kg·K)",
            IsRequired: false,
            DefaultValue: 0d,
            Step: 1,
            Decimals: 3,
            Order: 10,
            Description: "Specific heat capacity correction read from the Capacity Equipment DeltaC SCADA item.",
            Role: CalculationParameterRole.Configuration)
    ];

    // Capacity получает только те вещества, для которых SpecificHeatCapacity
    // явно разрешена SubstanceCatalog.
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions =
        CreateMixtureParameters(
            PropertyParameterDefinitions,
            SubstancePropertySupport.SpecificHeatCapacity);

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new CalculationOutputDefinition(
            Key: "capacity",
            Name: "Specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 1,
            Description: "Calculated mixture specific heat capacity including DeltaC."),

        // Diagnostic Runtime/UI outputs.
        new CalculationOutputDefinition(
            Key: "component0Capacity",
            Name: "Component 1 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 101,
            Description: "Specific heat capacity of mixture component 1."),

        new CalculationOutputDefinition(
            Key: "component1Capacity",
            Name: "Component 2 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 102,
            Description: "Specific heat capacity of mixture component 2."),

        new CalculationOutputDefinition(
            Key: "component2Capacity",
            Name: "Component 3 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 103,
            Description: "Specific heat capacity of mixture component 3."),

        new CalculationOutputDefinition(
            Key: "component3Capacity",
            Name: "Component 4 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 104,
            Description: "Specific heat capacity of mixture component 4."),

        new CalculationOutputDefinition(
            Key: "component4Capacity",
            Name: "Component 5 specific heat capacity",
            Unit: "J/(kg·K)",
            Decimals: 3,
            Order: 105,
            Description: "Specific heat capacity of mixture component 5.")
    ];

    public override string Code => DefinitionCode;
    public override string Name => "Mixture specific heat capacity";
    public override string Category => "Capacity";

    // Version 2:
    // - Capacity component options filter unsupported Cp models;
    // - ProcessInput count expanded to the same 2..5 contract as Density;
    // - componentNCapacity diagnostic outputs added;
    // - Density-only DryMatter validation removed from Capacity path.
    public override string Version => "2";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;
    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    protected override CalculationResult CalculateCore(
        CalculationParameterSet parameters,
        bool includeTrace)
    {
        var temperatureC = parameters.GetRequiredDouble(TemperatureKey);
        var pressureBarAbsolute = parameters.GetRequiredDouble(PressureKey);
        var deltaC = parameters.GetDouble(CorrectionKey, 0d);
        var components = ReadMixtureComponents(parameters);
        var additionalParameters = ReadAdditionalParameters(parameters);

        var mixtureResult = MixturePropertyCalculator.CalculateSpecificHeatCapacity(
            components,
            temperatureC,
            additionalParameters);

        var baseCapacityJPerKgK = mixtureResult.SpecificHeatCapacityJPerKgK;
        var capacityJPerKgK = baseCapacityJPerKgK + deltaC;

        if (!double.IsFinite(capacityJPerKgK) || capacityJPerKgK <= 0d)
        {
            return CalculationResult.Failure(
                "capacity.result.invalid",
                "Calculated specific heat capacity after DeltaC must be a finite value greater than zero.");
        }

        var outputs = new List<CalculationOutput>
        {
            new(
                Key: "capacity",
                Name: "Specific heat capacity",
                Value: capacityJPerKgK,
                Unit: "J/(kg·K)")
        };

        foreach (var component in mixtureResult.Components)
        {
            outputs.Add(new CalculationOutput(
                Key: $"component{component.Index}Capacity",
                Name: $"{component.SubstanceCode} specific heat capacity",
                Value: component.SpecificHeatCapacityJPerKgK,
                Unit: "J/(kg·K)"));
        }

        var trace = new List<CalculationTraceItem>();

        if (includeTrace)
        {
            trace.Add(new CalculationTraceItem(
                "temperatureC",
                "Temperature",
                Format(temperatureC),
                "°C"));

            // Pressure пока диагностический ProcessInput.
            trace.Add(new CalculationTraceItem(
                "pressureBarAbsolute",
                "Absolute pressure",
                Format(pressureBarAbsolute),
                "bar(abs)"));

            foreach (var parameter in additionalParameters.OrderBy(
                item => item.Key,
                StringComparer.OrdinalIgnoreCase))
            {
                trace.Add(new CalculationTraceItem(
                    parameter.Key,
                    "Additional parameter",
                    Format(parameter.Value),
                    null));
            }

            AddMixtureTrace(trace, components);

            foreach (var component in mixtureResult.Components)
            {
                trace.Add(new CalculationTraceItem(
                    $"component{component.Index}Capacity",
                    $"{component.SubstanceCode} specific heat capacity",
                    Format(component.SpecificHeatCapacityJPerKgK),
                    "J/(kg·K)"));
            }

            trace.Add(new CalculationTraceItem(
                "baseCapacity",
                "Capacity before DeltaC",
                Format(baseCapacityJPerKgK),
                "J/(kg·K)"));

            trace.Add(new CalculationTraceItem(
                "capacityCorrection",
                "DeltaC",
                Format(deltaC),
                "J/(kg·K)"));

            trace.Add(new CalculationTraceItem(
                "capacity",
                "Final specific heat capacity",
                Format(capacityJPerKgK),
                "J/(kg·K)"));
        }

        return CalculationResult.Success(outputs, trace: trace);
    }

    private static IReadOnlyDictionary<string, double> ReadAdditionalParameters(
        CalculationParameterSet parameters)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in AdditionalParameterKeys)
        {
            if (parameters.TryGetValue(key, out var rawValue) && rawValue is not null)
                result[key] = parameters.GetRequiredDouble(key);
        }

        return result;
    }
}
'@

# ---------------------------------------------------------------------------
# 5. Tests
# ---------------------------------------------------------------------------

$definitionTests = "TechMES.Calc.Tests/DensityCapacityDefinitionTests.cs"

Replace-Exact $definitionTests @'
    [Fact]
    public void DensityCalculatesPureAcetonitrile()
'@ @'
    [Fact]
    public void CapacityComponentOptionsExposeOnlySupportedHeatCapacityModels()
    {
        var definition = new CapacityCalculationDefinition();

        var componentParameter = definition.Parameters.Single(parameter =>
            string.Equals(parameter.Key, "component0Code", StringComparison.OrdinalIgnoreCase));

        var options = componentParameter.Options!;

        Assert.Contains(options, option =>
            string.Equals(option.Value, "ACN", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(options, option =>
            string.Equals(option.Value, "DryMatter", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(options, option =>
            string.Equals(option.Value, "Fusel", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(options, option =>
            string.Equals(option.Value, "Methan", StringComparison.OrdinalIgnoreCase));

        var acn = options.Single(option =>
            string.Equals(option.Value, "ACN", StringComparison.OrdinalIgnoreCase));

        var acnVapor = options.Single(option =>
            string.Equals(option.Value, "ACNS", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("liquid", acn.Phase);
        Assert.Equal("vapor", acnVapor.Phase);
    }

    [Fact]
    public void DensityCalculatesPureAcetonitrile()
'@

Replace-Exact $definitionTests @'
    [Fact]
    public void ActiveComponentRequiresSubstanceCode()
'@ @'
    [Fact]
    public void CapacityReturnsComponentHeatCapacitiesForRuntimeVisualization()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 2,

                ["component0Code"] = "ACN",
                ["component0Percent"] = 50d,

                ["component1Code"] = "Water",
                ["component1Percent"] = 50d
            }));

        Assert.True(result.IsSuccess, result.ErrorMessage);

        var component0Capacity = GetOutput(result, "component0Capacity");
        var component1Capacity = GetOutput(result, "component1Capacity");
        var mixtureCapacity = GetOutput(result, "capacity");

        Assert.Equal(2221.05154452d, component0Capacity, precision: 8);
        Assert.True(double.IsFinite(component1Capacity));
        Assert.True(component1Capacity > 0d);

        // Для массовых долей 50/50 итог обязан быть обычным
        // арифметическим средним чистых Cp компонентов.
        Assert.Equal(
            (component0Capacity + component1Capacity) * 0.5d,
            mixtureCapacity,
            precision: 8);
    }

    [Fact]
    public void CapacityDefinitionRejectsUnsupportedHeatCapacityModel()
    {
        var definition = new CapacityCalculationDefinition();

        var result = definition.Calculate(new CalculationParameterSet(
            new Dictionary<string, object?>
            {
                ["temperatureC"] = 20d,
                ["componentCount"] = 1,
                ["component0Code"] = "DryMatter",
                ["component0Percent"] = 100d
            }));

        Assert.False(result.IsSuccess);
        Assert.Equal("parameter.selection-invalid", result.ErrorCode);
    }

    [Fact]
    public void ActiveComponentRequiresSubstanceCode()
'@

Replace-Exact $definitionTests @'
    [Fact]
    public void CapacityExposesTemperatureAndPressureAsProcessInputs()
    {
        var definition = new CapacityCalculationDefinition();

        var processInputs = definition.Parameters
            .Where(parameter => parameter.Role == CalculationParameterRole.ProcessInput)
            .OrderBy(parameter => parameter.Order)
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.Equal(["temperatureC", "pressureBarAbsolute"], processInputs);
    }
'@ @'
    [Fact]
    public void CapacityExposesFiveProcessInputs()
    {
        var definition = new CapacityCalculationDefinition();

        var processInputs = definition.Parameters
            .Where(parameter => parameter.Role == CalculationParameterRole.ProcessInput)
            .OrderBy(parameter => parameter.Order)
            .Select(parameter => parameter.Key)
            .ToArray();

        Assert.Equal(
        [
            "temperatureC",
            "pressureBarAbsolute",
            "additionalParameter1",
            "additionalParameter2",
            "additionalParameter3"
        ],
        processInputs);
    }
'@

$substanceTests = "TechMES.Calc.Tests/SubstancePropertyTests.cs"

Replace-Exact $substanceTests @'
        Assert.Equal(SubstancePhase.Liquid, SubstanceCatalog.GetRequired("DryMatter").Phase);
        Assert.Equal("Dry matter", SubstanceCatalog.GetRequired("DryMatter").Name);
'@ @'
        Assert.Equal(SubstancePhase.Liquid, SubstanceCatalog.GetRequired("DryMatter").Phase);
        Assert.Equal("Dry matter", SubstanceCatalog.GetRequired("DryMatter").Name);

        Assert.True(
            SubstanceCatalog.GetRequired("ACN")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("DryMatter")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("Fusel")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.False(
            SubstanceCatalog.GetRequired("Methan")
                .Supports(SubstancePropertySupport.SpecificHeatCapacity));

        Assert.Equal(
            52,
            SubstanceCatalog.GetSupported(SubstancePropertySupport.SpecificHeatCapacity).Count);
'@

Replace-Exact $substanceTests @'
    [Theory]
    [InlineData("Methan", 298.15d)]
    [InlineData("Diesel", 20d)]
    [InlineData("HCL", 20d)]
    [InlineData("HCLS", 20d)]
    [InlineData("NaOH", 20d)]
    [InlineData("NaOHS", 20d)]
    public void LegacyCapacityModelsAreExecutedWithoutArtificialBlockList(string code, double temperature)
    {
        var capacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [new MixtureComponent(code, 100d)],
            temperatureC: temperature);

        Assert.True(double.IsFinite(capacity));
        Assert.True(capacity > 0d);
    }

    [Fact]
    public void FuselCapacityReturnsOriginalInvalidLegacyValueInsteadOfBeingArtificiallyBlocked()
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent("Fusel", 100d)],
                temperatureC: 20d));

        Assert.Equal("substance.capacity.invalid", exception.Code);
    }
'@ @'
    [Theory]
    [InlineData("Diesel", 20d)]
    [InlineData("HCL", 20d)]
    [InlineData("HCLS", 20d)]
    [InlineData("NaOH", 20d)]
    [InlineData("NaOHS", 20d)]
    public void SupportedLegacyCapacityModelsAreExecuted(string code, double temperature)
    {
        var capacity = MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
            [new MixtureComponent(code, 100d)],
            temperatureC: temperature);

        Assert.True(double.IsFinite(capacity));
        Assert.True(capacity > 0d);
    }

    [Theory]
    [InlineData("Methan")]
    [InlineData("Fusel")]
    [InlineData("DryMatter")]
    public void UnsupportedCapacityModelsAreRejectedByNormalizedContract(string code)
    {
        var exception = Assert.Throws<CalculationException>(() =>
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent(code, 100d)],
                temperatureC: 20d));

        Assert.Equal("substance.capacity.unsupported", exception.Code);
    }

    [Fact]
    public void ZeroPercentUnsupportedCapacityComponentDoesNotParticipateInCalculation()
    {
        var capacityWithoutInactiveComponent =
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [new MixtureComponent("ACN", 100d)],
                temperatureC: 20d);

        var capacityWithInactiveComponent =
            MixturePropertyCalculator.CalculateSpecificHeatCapacityJPerKgK(
                [
                    new MixtureComponent("ACN", 100d),
                    new MixtureComponent("Fusel", 0d)
                ],
                temperatureC: 20d);

        Assert.Equal(
            capacityWithoutInactiveComponent,
            capacityWithInactiveComponent,
            precision: 12);
    }
'@

# ---------------------------------------------------------------------------
# 6. Verification
# ---------------------------------------------------------------------------

Write-Host ""
Write-Host "Changed files:" -ForegroundColor Cyan
git status --short

Write-Host ""
Write-Host "Running git diff --check..." -ForegroundColor Cyan
git diff --check

if (-not $SkipTests) {
    Write-Host ""
    Write-Host "Running TechMES.Calc.Tests..." -ForegroundColor Cyan
    dotnet test "TechMES.Calc.Tests/TechMES.Calc.Tests.csproj" -c Debug

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE."
    }
}

Write-Host ""
Write-Host "Capacity Core stage applied successfully." -ForegroundColor Green
Write-Host "Review git diff, then commit/push when ready." -ForegroundColor Green
