# Capacity: памятка по расширению

Этот файл предназначен для разработчика. Он описывает, какие места необходимо изменить, чтобы добавить новое вещество, дополнительный параметр или новую логику Capacity. Это не пользовательская справка WEB-интерфейса.

## Текущая цепочка

```text
Substances/Components/<Substance>.cs
    -> SubstanceCatalog.CreateEntries()
    -> CapacityCalculationDefinition
    -> MixtureCalculationDefinitionBase.CreateMixtureParameters(...)
    -> Runtime CalcContractMapper
    -> CapacityConfigurationPanel SubstanceOptions
    -> комбобокс компонентов Capacity
```

Обычный новый компонент не нужно вручную добавлять в Razor-комбобокс. Комбобокс строится из `component0Code.Options`, а эти options формируются в `TechMES.Calc` из `SubstanceCatalog`.

## Как добавить обычный компонент Capacity

### 1. Добавить физическую модель

Создать файл:

```text
TechMES.Calc/Substances/Components/<NewSubstance>.cs
```

Класс должен наследовать `LegacySubstance` и реализовать обязательные члены:

```csharp
internal sealed class NewSubstance : LegacySubstance
{
    public NewSubstance(bool isSteam) : base(isSteam) { }

    public override double MolarMass => /* kg/kmol по принятому legacy-контракту */;
    public override bool IsSteam => isSteam;

    public override double GetDensity(float temperature, float pressure)
    {
        // Реальная Density-формула либо явно неподдерживаемая реализация.
    }

    public override double GetCapacity(float temperature)
    {
        // Результат legacy-метода должен быть в kJ/(kg·K).
        // MixturePropertyCalculator переводит его в J/(kg·K) через x1000.
    }
}
```

Если Capacity зависит от массовой доли самого компонента или дополнительных параметров, переопределить расширенную перегрузку:

```csharp
public override double GetCapacity(float temperature, double massPercent, IReadOnlyDictionary<string, double>? additionalParameters)
```

Нельзя выставлять поддержку Capacity, пока формула не возвращает конечное положительное значение во всём разрешённом рабочем диапазоне.

### 2. Зарегистрировать SubstanceCode

Файл:

```text
TechMES.Calc/Substances/SubstanceCatalog.cs
```

Метод:

```text
CreateEntries()
```

Добавить запись с уникальным стабильным кодом:

```csharp
Add(
    "NEW",
    "New substance",
    SubstancePhase.Liquid,
    () => new NewSubstance(false),
    SubstancePropertySupport.SpecificHeatCapacity);
```

Если один класс поддерживает и Density, и Capacity:

```csharp
SubstancePropertySupport.Density | SubstancePropertySupport.SpecificHeatCapacity
```

Флаг нужно указывать осознанно. Не следует полагаться на `DefaultPropertySupport`: случайно разрешённый `SpecificHeatCapacity` автоматически добавит компонент в Capacity UI.

Для жидкой и паровой фаз обычно используются разные стабильные коды и отдельные записи каталога. Значение `SubstancePhase` определяет, в какой вкладке `Liquid/Vapor` компонент появится в WEB.

### 3. Проверить автоматическое появление в комбобоксе

Следующие места менять не требуется:

- `TechMES.Calc/Capacity/CapacityCalculationDefinition.cs` уже вызывает `CreateMixtureParameters(..., SubstancePropertySupport.SpecificHeatCapacity)`;
- `TechMES.Calc/Mixtures/MixtureCalculationDefinitionBase.cs` создаёт `CalculationParameterOption` для каждого поддерживаемого вещества;
- `TechMES.Runtime.Service/Calc/CalcContractMapper.cs` переносит options и фазу в DTO;
- `TechMES.Web/Components/Calc/Capacity/CapacityConfigurationPanel.razor` получает options через `SubstanceOptions` и фильтрует их через `FilteredSubstanceOptions`.

После перезапуска Runtime новый компонент должен появиться в Capacity-комбобоксе соответствующей фазы.

## Как добавить постоянную настройку только для одного вещества

Пример существующей реализации: `DryMatter -> Purity`.

### 1. Объявить ключ в классе вещества

Файл:

```text
TechMES.Calc/Substances/Components/<NewSubstance>.cs
```

Например:

```csharp
public const string CorrectionParameterKey = "newSubstanceCorrection";
```

### 2. Добавить metadata параметра

Файл:

```text
TechMES.Calc/Capacity/CapacityCalculationDefinition.cs
```

Список:

```text
PropertyParameterDefinitions
```

Добавить `CalculationParameterDefinition` со следующими признаками:

```csharp
Role: CalculationParameterRole.Configuration,
AppliesToSubstanceCode: "NEW"
```

Текущий Capacity UI автоматически:

- создаёт редактор в `BuildSubstanceConfiguration()`;
- показывает его через `ActiveSubstanceConfigurationEditors`, только когда компонент выбран;
- проверяет Min/Max;
- сохраняет значение как Constant input в `BuildCapacityInputs()`.

Текущая WEB-реализация substance-specific Configuration поддерживает числовой `RadzenNumeric`. Для Boolean, Text или Selection нужно дополнительно расширить UI-блок `ActiveSubstanceConfigurationEditors` в:

```text
TechMES.Web/Components/Calc/Capacity/CapacityConfigurationPanel.razor
```

### 3. Использовать настройку в формуле

`CapacityCalculationDefinition.ReadComponentCalculationParameters()` передаёт такие значения в словарь `additionalParameters`. Класс вещества должен прочитать ключ в расширенной перегрузке `GetCapacity(...)`.

## Как добавить новый ProcessInput Capacity

Сейчас зарезервированы три позиции:

```text
additionalParameter1
additionalParameter2
additionalParameter3
```

Чтобы назначить одной позиции реальный смысл:

1. В `CapacityCalculationDefinition.PropertyParameterDefinitions` изменить `Name`, `Unit`, `Description`, обязательность, DefaultValue и диапазон нужного параметра.
2. Сохранить `Role: CalculationParameterRole.ProcessInput`.
3. Добавить или сохранить ключ в `AdditionalParameterKeys`, чтобы значение попало в `ReadComponentCalculationParameters()`.
4. Использовать ключ в формуле нужного вещества.
5. Если имя должно отображаться в верхней live-панели, обновить `GetProcessDisplayName()` в `TechMES.Web/Components/Calc/Capacity/CapacityConfigurationPanel.razor`. В Settings используется `binding.Parameter.Name` автоматически.

Если ProcessInput станет больше пяти вместе с Temperature и Pressure, дополнительно изменить в `CapacityConfigurationPanel.razor`:

```text
MaxProcessParameterCount
RadzenNumeric Max="5"
```

Также потребуется добавить соответствующие definitions и убедиться, что `ActiveProcessBindings` включает их.

## Когда менять версию Definition

Файл:

```text
TechMES.Calc/Capacity/CapacityCalculationDefinition.cs
```

Свойство:

```text
Version
```

Версию нужно увеличить, если изменился контракт параметров, единицы, формула или смысл результата. Простое добавление нового вещества в каталог без изменения `mixture.capacity` обычно не требует смены версии Definition, но изменение поведения уже существующего SubstanceCode требует регрессионного решения и тестов.

## Обязательные тесты

Файлы:

```text
TechMES.Calc.Tests/SubstancePropertyTests.cs
TechMES.Calc.Tests/DensityCapacityDefinitionTests.cs
```

Добавить проверки:

- код существует в `SubstanceCatalog`;
- установлен правильный `SubstancePhase`;
- установлен `SpecificHeatCapacity`;
- компонент присутствует в `CapacityCalculationDefinition` options;
- известная рабочая точка формулы;
- границы рабочего диапазона;
- некорректный результат отклоняется;
- смесь из нескольких компонентов рассчитывается правильно;
- при необходимости проверяется substance-specific Configuration.

В существующих тестах есть проверки точного количества веществ (`37` для Capacity и `55` всего). После осознанного добавления компонента эти ожидаемые значения нужно обновить.

## Контрольный список

- [ ] Создана и протестирована физическая модель.
- [ ] SubstanceCode уникален и не будет переименован после сохранения Jobs.
- [ ] Фаза указана правильно.
- [ ] `SpecificHeatCapacity` выставлен только при рабочей формуле.
- [ ] Проверены единицы: legacy `GetCapacity` возвращает kJ/(kg·K), наружный результат — J/(kg·K).
- [ ] Компонент появился в правильном Liquid/Vapor комбобоксе.
- [ ] Сумма массовых долей равна 100%.
- [ ] Тесты обновлены и проходят.
- [ ] Runtime и WEB перезапущены.