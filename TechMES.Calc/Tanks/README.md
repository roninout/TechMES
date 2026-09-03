# Tanks: памятка по добавлению нового Tank Type

Этот файл предназначен для разработчика. Новый Tank Type состоит из двух частей:

1. расчётная модель в `TechMES.Calc`;
2. интерактивный preview/editor в `TechMES.Web`.

Общий Runtime endpoint, PostgreSQL schema и общий Help dialog для каждого нового Tank Type создавать не нужно.

## Текущая цепочка

```text
TankTypeNVolumeDefinition
    -> BuiltInCalculationCatalog.Create()
    -> Runtime definitions API
    -> TankConfigurationPanel._tankDefinitions
    -> Tank type dropdown
    -> TankTypeNInteractivePreview
    -> Calc Job
```

Все Tank definitions должны иметь:

```text
Category = "Tanks"
Code     = "tank.volume.typeN"
```

Иначе текущий `TankConfigurationPanel.LoadDefinitionsAsync()` не включит их в список.

## Как добавить расчёт нового Tank Type

Предположим, добавляется Type 9.

### 1. Создать Definition

Создать файл:

```text
TechMES.Calc/Tanks/Types/TankType9VolumeDefinition.cs
```

Класс должен наследовать:

```text
TankTypeVolumeDefinitionBase
```

Минимальная структура:

```csharp
public sealed class TankType9VolumeDefinition : TankTypeVolumeDefinitionBase
{
    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters(
        Dimension("dimA", "dimA", 10, minimum: 1d),
        Dimension("dimB", "dimB", 11, minimum: 1d));

    public override string Code => "tank.volume.type9";
    public override string Name => "Type 9 — ...";
    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    protected override double GetTotalLengthMm(CalculationParameterSet parameters)
    {
        // Полный физический размер по направлению Level.
    }

    protected override double CalculateVolume(CalculationParameterSet parameters)
    {
        // Получить levelMm и вернуть объём в m³.
    }

    protected override IReadOnlyList<CalculationTraceItem> BuildVolumeTrace(CalculationParameterSet parameters, double liquidHeightMm)
    {
        // Формулы, подстановки, промежуточные результаты и текущая область заполнения.
    }
}
```

`CreateParameters()` автоматически добавляет общие inputs:

```text
levelRaw
densityHmi
upperDeadArea
lowerDeadArea
calculateAbove100
```

В новый Definition передаются только собственные геометрические dimensions.

### 2. Не дублировать общую LevelTank-логику

Базовый класс уже выполняет:

```text
Level.R -> measurement area -> levelMm -> physical liquid height -> Volume -> Mass
```

Новый Tank Type должен реализовать только геометрию `Volume(liquidHeightMm)`.

Не нужно повторно:

- ограничивать Level.R значением 100%;
- рассчитывать dead areas;
- рассчитывать `hMax`;
- умножать Volume на Density;
- формировать общие outputs `hMax/levelMm/volume/mass`.

### 3. Использовать один расчёт деталей

Рекомендуемый шаблон Type 1..8:

```text
CalculateDetails(parameters, liquidHeightMm)
```

Его должны использовать одновременно:

- `CalculateVolume()`;
- `BuildVolumeTrace()`.

Так Runtime-результат и Help не будут содержать две разные копии формулы.

При физически невозможной геометрии возвращать детали с `VolumeM3 = double.NaN` либо бросать `CalculationException`. `TankTypeVolumeDefinitionBase` преобразует не конечный Volume в понятный failure и не позволит записать ошибочный результат.

### 4. Зарегистрировать Definition

Файл:

```text
TechMES.Calc/Abstractions/BuiltInCalculationCatalog.cs
```

Метод:

```text
Create()
```

После `TankType8VolumeDefinition` добавить:

```csharp
new TankType9VolumeDefinition(),
```

Без этой регистрации Definition не попадёт в Runtime и не появится в комбобоксе.

## Как добавить новый экран/preview в WEB

### 1. Создать интерактивный компонент

Создать:

```text
TechMES.Web/Components/Calc/Tanks/TankType9InteractivePreview.razor
TechMES.Web/Components/Calc/Tanks/TankType9InteractivePreview.razor.css
```

За основу брать наиболее близкий геометрически существующий Type. Компонент должен принимать:

- используемые `Dim*`;
- `UpperDeadArea`;
- `LowerDeadArea`;
- `CalculateAbove100`;
- `LevelPercent`;
- `Disabled`;
- `EventCallback<double>` для редактируемых размеров.

Preview отвечает только за визуализацию и изменение полей. Физическая Volume-формула остаётся в `TechMES.Calc`.

### 2. Подключить preview

Файл:

```text
TechMES.Web/Components/Calc/Tanks/TankConfigurationPanel.razor
```

В блоке `SelectedTypeNumber == 1 ... 8` добавить ветку:

```razor
else if (SelectedTypeNumber == 9)
{
    <TankType9InteractivePreview ... />
}
```

Передать только реальные dimensions Type 9 и соответствующие callbacks `SetInputNumber("dimX", value)`.

### 3. Научить WEB распознавать номер

В том же файле обновить:

```text
GetTypeNumber(string? definitionCode)
```

Минимальное изменение для Type 9 — увеличить текущий цикл `1..8` до `1..9`. Более устойчивый вариант — один раз разобрать числовой suffix после `tank.volume.type`, чтобы Type 10 и следующие не требовали изменения этого метода.

### 4. Добавить физическую полную высоту

Обновить switch:

```text
CurrentTotalLengthMm
```

Добавить формулу Type 9, идентичную смыслу `GetTotalLengthMm()` в Definition. Этот WEB-расчёт нужен только для предварительной валидации sensor dead areas; Volume по нему не рассчитывается.

### 5. Добавить межпараметрическую валидацию

Обычные `Minimum/Maximum` автоматически проверяет:

```text
AreEditorValuesWithinDefinitionRanges
```

Если Type 9 имеет правила между несколькими dimensions, добавить ветку в:

```text
IsTypeSpecificGeometryValid
```

и отдельный метод `IsType9GeometryValid()`.

Та же проверка обязательно должна существовать внутри `TankType9VolumeDefinition`. WEB-проверка улучшает интерфейс, но не является защитой Runtime.

### 6. Добавить описания размеров

Файл:

```text
TechMES.Web/Components/Calc/Tanks/TankFieldDescriptions.cs
```

В switch для каждого используемого `dimA...dimN` добавить текст для `typeNumber == 9`.

Если вводится новое имя, например `dimH`, дополнительно потребуются:

- `CurrentDimH` в `TankConfigurationPanel.razor`;
- case `"dimh"` в `TankFieldDescriptions.Get()`;
- параметр и callback в `TankType9InteractivePreview`;
- metadata `Dimension("dimH", ...)` в Definition.

## Output binding

Существующая SCADA-структура общая для всех Tank Type:

```text
hMax    -> Tank.HMax
levelMm -> Tank.HHmi
volume  -> Tank.VHmi
mass    -> Tank.MHmi
```

Если Type 9 использует эти же outputs, менять `TankOutputItems` и `BuildTankOutputs()` не нужно.

Если добавляется новый обязательный output, изменить:

```text
TankTypeVolumeDefinitionBase.OutputDefinitions
TankConfigurationPanel.TankOutputItems
TankConfigurationPanel.BuildTankOutputs()
Plant SCADA Tank Equipment Type
Runtime/Web tests
```

Диагностические значения, не предназначенные для SCADA, лучше добавлять в Trace, а не в общий список outputs.

## Help-окно

Новый отдельный диалог создавать не требуется.

Существующие:

```text
TankConfigurationPanel.OpenTankHelpAsync()
TankCalculationInfoDialog.razor
```

запускают выбранный реальный Definition с `includeTrace: true`. Новый Type автоматически использует общий Help dialog, если его `BuildVolumeTrace()` возвращает полные и понятные строки.

## Версия Tank Definition

Сейчас версия всех Tank Type находится в:

```text
TankTypeVolumeDefinitionBase.Version
```

Это общая версия для Type 1..8. Изменение её значения создаст version mismatch сразу для всех Tank Jobs. При добавлении Type 9 без изменения старой математики общую версию увеличивать не нужно.

Если в будущем разные Tank Types будут развиваться независимо, версию следует перенести из base в конкретные definitions или добавить переопределяемое защищённое значение.

## Обязательные тесты

Файл:

```text
TechMES.Calc.Tests/TankTypeVolumeDefinitionTests.cs
```

Изменить:

- `BuiltInCatalogContainsAllEightTankTypes` — имя и диапазон;
- `BuiltInCatalogContainsExactlyEightTankDefinitions` — ожидаемое количество;
- добавить helpers для Type 9.

Для нового Tank Type проверить:

- пустой и полный Tank;
- известные рабочие точки в каждой геометрической области;
- непрерывность на границах частей;
- 0%, 100% и больше 100%;
- upper/lower dead areas;
- `calculateAbove100` on/off;
- Mass при известной Density;
- невозможную геометрию;
- Min/Max параметров;
- конечность Volume;
- правильный Trace и Help-подстановки;
- симметрию или монотонность, если она следует из геометрии.

Для сложной формулы expected values должны рассчитываться независимым способом или быть получены из проверенного эталона, а не вызовом того же `CalculateDetails()`.

## Контрольный список

- [ ] Создан `TankTypeNVolumeDefinition`.
- [ ] Код имеет формат `tank.volume.typeN`.
- [ ] Definition зарегистрирован в `BuiltInCalculationCatalog`.
- [ ] Реальная математика находится только в `TechMES.Calc`.
- [ ] `BuildVolumeTrace()` использует ту же модель деталей.
- [ ] Созданы Razor preview и CSS.
- [ ] Обновлены `GetTypeNumber` и `CurrentTotalLengthMm`.
- [ ] Добавлена межпараметрическая валидация, если нужна.
- [ ] Добавлены описания dimensions.
- [ ] Новый тип появился в Tank type dropdown.
- [ ] Общий Help dialog показывает формулы и подстановки.
- [ ] Тесты обновлены и проходят.