# TechMES Substances Structure Cleanup

Этот cleanup-патч предназначен для состояния ветки `feature/techmes-calc`
после применения `TechMES_Density_LegacySplit_Patch`.

Он **не изменяет тела формул компонентов**.

## Новая структура

```text
TechMES.Calc/Substances/
├── Components/
│   ├── Acetaldehyde.cs
│   ├── Acetonitrile.cs
│   ├── ...
│   └── Water.cs
│
├── Content/
│   ├── ContentCalculationRequest.cs
│   ├── ContentPropertyCalculator.cs
│   └── ContentCalc.cs
│
├── Thermodynamics/
│   └── TechLib.cs
│
├── LegacySubstance.cs
├── MixtureComponent.cs
├── MixturePropertyCalculator.cs
├── SubstanceCatalog.cs
├── SubstanceDescriptor.cs
└── SubstancePhase.cs
```

## Что удаляется

`Substances/Legacy/WaterSteamProLib` удаляется полностью.

Перед удалением скрипт проверяет, что в компонентах нет активных строк с `WspLib.`.
Закомментированные старые ссылки разрешены.

## Два падающих Nitrogen-теста

Причина не в формуле Density.

Оригинальный контракт:

```csharp
GetDensity(float temperature, float pressure)
```

Новый `MixturePropertyCalculator` также передаёт значения через `float`.
Но regression helper в тесте считал expected через `double`, поэтому отличался примерно на 1e-8.

Патч исправляет expected так, чтобы он использовал исходный `float`-контракт TechDotNetLib.

## Запуск

Можно запускать из любой папки:

```powershell
.\Apply-TechMES-SubstancesStructureCleanup.ps1 -ProjectRoot "C:\path\to\TechMES"
```

Либо, если PowerShell находится внутри дерева solution, скрипт попробует сам найти корень:

```powershell
.\Apply-TechMES-SubstancesStructureCleanup.ps1
```

После применения:

```powershell
dotnet test .\TechMES.Calc.Tests\TechMES.Calc.Tests.csproj
```

Перед изменениями создаётся backup:

```text
.patch-backup\SubstancesStructureCleanup_<timestamp>
```
