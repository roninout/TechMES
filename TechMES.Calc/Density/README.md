# Density: памятка по расширению

Этот файл предназначен для разработчика. Он описывает точные точки расширения Density и появления новых веществ в WEB-комбобоксе.

## Текущая цепочка

```text
Substances/Components/<Substance>.cs
    -> SubstanceCatalog.CreateEntries()
    -> DensityCalculationDefinition
    -> MixtureCalculationDefinitionBase.CreateMixtureParameters(...)
    -> Runtime CalcContractMapper
    -> DensityConfigurationPanel SubstanceOptions
    -> комбобокс компонентов Density
```

Для обычного нового вещества ручное добавление option в Razor не требуется.

## Как добавить обычный компонент Density

### 1. Добавить физическую модель

Создать файл:

```text
TechMES.Calc/Substances/Components/<NewSubstance>.cs
```

Класс должен наследовать `LegacySubstance` и реализовать как минимум реальную Density-формулу:

```csharp
internal sealed class NewSubstance : LegacySubstance
{
    public NewSubstance(bool isSteam) : base(isSteam) { }

    public override double MolarMass => /* значение по принятому контракту */;
    public override bool IsSteam => isSteam;

    public override double GetDensity(float temperature, float pressure)
    {
        // temperature = °C для нормализованного TechMES-контракта;
        // pressure = bar(abs).
    }

    public override double GetCapacity(float temperature)
    {
        // Обязательный legacy-член. Не выставлять Capacity capability,
        // если рабочая Cp-формула отсутствует.
    }
}
```

Если формула зависит от массовой доли компонента или дополнительных параметров, переопределить:

```csharp
public override double GetDensity(float temperature, float pressure, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
```

Density получает абсолютное давление. Преобразование `P(abs) = P(g) + AtmosphericPressureBarAbsolute` выполняет `DensityCalculationDefinition`, поэтому повторно добавлять атмосферное давление внутри обычной формулы нельзя.

Если переносимая legacy-формула внутри исторически ожидает K/Pa, адаптацию выполнять внутри конкретного класса вещества. Пример такого адаптера: `Methan`.

### 2. Зарегистрировать SubstanceCode

Файл:

```text
TechMES.Calc/Substances/SubstanceCatalog.cs
```

Метод:

```text
CreateEntries()
```

Пример Density-only регистрации:

```csharp
Add(
    "NEW",
    "New substance",
    SubstancePhase.Liquid,
    () => new NewSubstance(false),
    SubstancePropertySupport.Density);
```

Если модель также имеет проверенную Capacity-формулу:

```csharp
SubstancePropertySupport.Density | SubstancePropertySupport.SpecificHeatCapacity
```

Флаг указывать явно. `DefaultPropertySupport` разрешает сразу Density и Capacity, поэтому его неявное использование может случайно добавить неготовый компонент в оба интерфейса.

Для liquid/vapor обычно создаются разные коды и записи. `SubstancePhase` управляет фильтром вкладок `Liquid/Vapor` в WEB.

### 3. Проверить автоматическое появление

Ручные изменения не нужны в следующих местах:

- `DensityCalculationDefinition` запрашивает `SubstancePropertySupport.Density`;
- `MixtureCalculationDefinitionBase.CreateMixtureParameters()` строит options;
- `CalcContractMapper` передаёт `Value`, `Name` и `Phase` в Runtime DTO;
- `DensityConfigurationPanel.SubstanceOptions` читает options Definition;
- `DensityConfigurationPanel.FilteredSubstanceOptions` оставляет выбранную фазу.

После перезапуска Runtime новое вещество должно появиться в соответствующем комбобоксе.

## Как добавить новый ProcessInput Density

Сейчас Definition содержит:

```text
temperatureC
pressureBarGauge
additionalParameter1
additionalParameter2
additionalParameter3
```

Чтобы использовать зарезервированную позицию:

1. В `TechMES.Calc/Density/DensityCalculationDefinition.cs`, в `PropertyParameterDefinitions`, изменить metadata нужного `additionalParameterN`: `Name`, `Unit`, `Description`, DefaultValue, Min/Max и обязательность.
2. Оставить `Role: CalculationParameterRole.ProcessInput`.
3. Убедиться, что ключ присутствует в `AdditionalParameterKeys`.
4. Прочитать значение в расширенной перегрузке `NewSubstance.GetDensity(..., additionalParameters)`.
5. Если имя должно использоваться в live bargraph, обновить `GetProcessDisplayName()` в `TechMES.Web/Components/Calc/Density/DensityConfigurationPanel.razor`. Settings уже показывает `binding.Parameter.Name`.

Если вместе с Temperature/Pressure понадобится больше пяти ProcessInput, изменить в `DensityConfigurationPanel.razor`:

```text
MaxProcessParameterCount
RadzenNumeric Max="5"
```

и расширить `PropertyParameterDefinitions`/`AdditionalParameterKeys`.

## Как добавить постоянную настройку конкретного вещества

В ядре metadata уже поддерживает:

```text
CalculationParameterDefinition.AppliesToSubstanceCode
```

Но текущий специализированный `DensityConfigurationPanel` не строит substance-specific Constant editors, в отличие от Capacity.

Поэтому для нового Density-компонента с постоянной настройкой нужно:

1. Добавить `CalculationParameterDefinition` в `DensityCalculationDefinition.PropertyParameterDefinitions` с `Role.Configuration` и `AppliesToSubstanceCode`.
2. Передать значение в словарь параметров, используемый формулой компонента.
3. В `TechMES.Web/Components/Calc/Density/DensityConfigurationPanel.razor` добавить механизм редакторов и сохранения Constant inputs по образцу следующих Capacity-участков:
   - `BuildSubstanceConfiguration()`;
   - `ActiveSubstanceConfigurationEditors`;
   - `ConfiguredSubstanceConfigurationEditors`;
   - `IsSubstanceConfigurationReady`;
   - сохранение Constant в `BuildCapacityInputs()`;
   - восстановление и Unsaved signature.

Нельзя просто добавить Constant в Definition и считать задачу законченной: старый Density UI не даст разработчику/оператору его настроить и не сохранит в Calc Job.

## Ограничение количества компонентов

Текущий SCADA-контракт содержит:

```text
CompN
Perc0
Perc1
Perc2
Perc3
Perc4
```

Поэтому `MixtureCalculationDefinitionBase.MaxComponentCount` равен `5`. Это не математическое ограничение ядра, а ограничение Equipment Type и существующего UI.

Для шестого компонента недостаточно изменить только Calc. Потребуется одновременно изменить:

- Equipment Type в Plant SCADA;
- `MixtureCalculationDefinitionBase.MaxComponentCount`;
- параметры `componentNCode/componentNPercent`;
- `DensityConfigurationPanel.MaxComponentCount`;
- ITEM mapping `PercN` в Density panel;
- Calc Model discovery, если новый ITEM не попадает в существующий scan;
- тесты и выходную диагностику `componentNDensity`.

## Когда менять версию Definition

Свойство:

```text
DensityCalculationDefinition.Version
```

Увеличить версию при изменении формулы `mixture.density`, единиц, параметров или семантики outputs. Добавление нового вещества без изменения существующих кодов обычно не требует смены версии Definition. Изменение поведения уже существующего SubstanceCode требует регрессионных тестов и решения о совместимости существующих Jobs.

## Обязательные тесты

Файлы:

```text
TechMES.Calc.Tests/SubstancePropertyTests.cs
TechMES.Calc.Tests/DensityCapacityDefinitionTests.cs
TechMES.Calc.Tests/DensityLegacyRegressionTests.cs
```

Добавить проверки:

- регистрация и фаза SubstanceCode;
- `SubstancePropertySupport.Density`;
- появление в options `component0Code`;
- известные рабочие точки формулы;
- преобразование единиц и давления;
- liquid/vapor варианты;
- границы и невалидные значения;
- расчёт чистого вещества и смеси;
- при переносе legacy-формулы — сравнение с независимым regression oracle.

В тестах зафиксированы количества (`55` всего, `53` Density). После осознанного добавления записи ожидаемые значения обновить.

## Контрольный список

- [ ] Реальная Density-формула добавлена и проверена.
- [ ] SubstanceCode уникален и стабилен.
- [ ] Указана правильная фаза.
- [ ] Выставлен только действительно поддерживаемый capability.
- [ ] Давление не конвертируется повторно.
- [ ] Компонент появился в правильном комбобоксе.
- [ ] Проверена смесь с суммой массовых долей 100%.
- [ ] Тесты обновлены и проходят.
- [ ] Runtime и WEB перезапущены.