# Content: памятка по добавлению новой физической системы

Этот файл предназначен для разработчика. Content расширяется не простым добавлением вещества в комбобокс: каждая комбинация веществ должна иметь явно зарегистрированную физическую корреляцию и отдельный Calculation Definition.

## Текущая цепочка

```text
ContentSystem
    -> физическая формула IContentSubstanceModel или отдельная multi-component model
    -> ContentCombinationCalculator.CreateDefinitions()
    -> ContentCalculationDefinitions.CreateAll()
    -> BuiltInCalculationCatalog
    -> Runtime definitions API
    -> ContentConfigurationPanel._contentDefinitions
    -> комбобокс Content system
```

WEB не хранит отдельный список Content-систем. Он автоматически показывает все definitions с:

```text
Category == "Content"
```

## Что считать новой Content-системой

Система определяется фиксированным составом и порядком outputs. Примеры:

```text
ACN + Water
PO + Propylene
ACN + Water + PO
```

Пользователь не собирает произвольную комбинацию. Это защищает от запуска корреляции для физически неподдерживаемой системы.

## Как добавить бинарную систему

Предположим, нужно добавить `NEW + Water`, где формула возвращает содержание `NEW`, а Water вычисляется как дополнение до 100%.

### 1. Зарегистрировать вещества

Файл:

```text
TechMES.Calc/Substances/SubstanceCatalog.cs
```

В `CreateEntries()` должны существовать оба стабильных кода. Для вещества, участвующего в Content, добавить:

```text
SubstancePropertySupport.Content
```

Если вещества ещё нет, сначала создать его модель в:

```text
TechMES.Calc/Substances/Components/<NewSubstance>.cs
```

### 2. Добавить физическую систему

Файл:

```text
TechMES.Calc/Content/ContentSystem.cs
```

Добавить новое уникальное значение enum:

```csharp
NewWater = 7
```

Существующие числовые значения не менять.

### 3. Реализовать корреляцию

Для бинарной системы основной компонент обычно реализует:

```text
IContentSubstanceModel.GetContent(...)
```

Файл находится в:

```text
TechMES.Calc/Substances/Components/<PrimarySubstance>.cs
```

Если класс уже поддерживает несколько систем, добавить новую ветку `ContentSystem.NewWater` в существующий switch. Если это новое вещество, реализовать `IContentSubstanceModel`.

Внешний контракт:

- Temperature — `°C`;
- Pressure — `bar(abs)`;
- результат primary-компонента — инженерный процент;
- ошибки диапазона — `CalculationException` со стабильным code.

Формулы выбора диапазона давления и полинома следует брать из:

```text
TechMES.Calc/Content/ContentCorrelationMath.cs
```

Не копировать эти вспомогательные функции в новую модель.

### 4. Зарегистрировать допустимые порядки компонентов

Файл:

```text
TechMES.Calc/Content/ContentCombinationCalculator.cs
```

Метод:

```text
CreateDefinitions()
```

Добавить:

```csharp
AddBinary(ContentSystem.NewWater, "NEW", "Water",
    ["NEW", "Water"],
    ["Water", "NEW"]);
```

Добавлять обратный порядок можно только если он физически и исторически поддерживается. `ContentCombinationCalculator` вернёт результаты в том порядке, который указан в конкретном Definition.

### 5. Добавить Calculation Definition

Файл:

```text
TechMES.Calc/Content/ContentCalculationDefinitions.cs
```

Добавить стабильный код:

```csharp
public const string NewWaterCode = "content.new-water";
```

Затем в `CreateAll()` добавить:

```csharp
new ContentCalculationDefinition(
    NewWaterCode,
    "NEW / Water content",
    [
        new ContentComponent("NEW", "newPercent", "NEW"),
        new ContentComponent("Water", "waterPercent", "Water")
    ])
```

Порядок `ContentComponent` важен:

```text
первый output  -> Content.Param0
второй output  -> Content.Param1
третий output  -> Content.Param2
```

После этого `ContentCalculationDefinitions.CreateAll()` автоматически подключается через `BuiltInCalculationCatalog.Create()`, а WEB-комбобокс получает новую систему через definitions API.

## Как добавить систему из трёх компонентов

Для корреляции, которая сразу возвращает все компоненты:

1. Создать отдельный файл модели, например:

```text
TechMES.Calc/Content/ThreeComponentSystem/NewThreeComponentContentModel.cs
```

2. Добавить `ContentSystem`.
3. В `ContentCombinationCalculator.CalculateMultiComponent()` добавить switch-ветку, вызывающую новую модель.
4. В `CreateDefinitions()` вызвать `AddMultiComponent(...)` и перечислить только реально поддерживаемые порядки.
5. В `ContentCalculationDefinitions.CreateAll()` создать Definition с тремя `ContentComponent` в нужном output-порядке.
6. Добавить regression tests.

Не следует вычислять трёхкомпонентную систему как набор независимых бинарных формул, если физическая корреляция определена совместно.

## Ограничения количества Content-компонентов

Сейчас действуют два ограничения:

```text
ContentPropertyCalculator: только 2 или 3 компонента
Content Equipment / WEB: максимум 5 Param slots
```

Обычная новая система из двух или трёх компонентов не требует изменения лимитов.

Если нужна система из четырёх или пяти компонентов, необходимо изменить:

1. `ContentPropertyCalculator.NormalizeAndValidateComponents()` — допустимое количество.
2. `ContentCombinationCalculator` — новый тип/обработчик много-компонентной корреляции.
3. `ContentCalculationDefinitions` — Definition и outputs.
4. Проверить `MaxContentItemCount = 5` в `ContentCalculationDefinitions`.
5. Проверить `MaxContentItemCount = 5` в `TechMES.Web/Components/Calc/Content/ContentConfigurationPanel.razor`.
6. Убедиться, что Plant SCADA Equipment содержит `Param0...Param4`, соответствующие `ParamN_Dp` и `ParamN_Dt`.
7. Обновить тесты.

Больше пяти компонентов потребуют изменения Equipment Type Plant SCADA и всех `ParamN` mappings. Простого увеличения числа в Calc недостаточно.

## Когда WEB менять не нужно

Для новой системы из двух/трёх компонентов, использующей существующие:

```text
Temperature
Pressure
Conf
Select
Param0...Param4
ParamN_Dp
ParamN_Dt
```

`ContentConfigurationPanel.razor` менять не нужно. Он:

- фильтрует definitions по `Category == "Content"`;
- строит комбобокс из `_contentDefinitions`;
- строит outputs по metadata Definition;
- связывает outputs с `ParamN` по порядку;
- связывает коррекции с `ParamN_Dp/ParamN_Dt`;
- строит pie по Runtime outputs.

## Когда нужны изменения вне TechMES.Calc

Если новая система требует нового SCADA ITEM, а не существующих `Conf/Select/ParamN/ParamN_Dp/ParamN_Dt`, потребуется изменить:

- Plant SCADA Equipment Type;
- `TechMES.Infrastructure.CtApi/Gateways/CtApiCalcModelCatalogProvider.cs`, если discovery/mapping не получает ITEM;
- `TechMES.Web/Components/Calc/Content/ContentConfigurationPanel.razor` — mapping input/output;
- `TechMES.Infrastructure.CtApi/Gateways/CtApiEquipmentParamProvider.cs` — `ParamDefinitions` и `ParamWriteDefinitions`, если ITEM должен редактироваться оператором;
- Calc Job input/output binding;
- тесты Runtime/Web.

Новый write endpoint создавать не нужно: редактируемый ITEM подключается к существующему Param write-flow.

## Версия Definition

Все текущие Content definitions используют версию в приватном классе:

```text
ContentCalculationDefinitions.ContentCalculationDefinition.Version
```

Изменение общей версии затронет все Content systems. Если новая корреляция добавлена без изменения старых contracts, существующие Jobs не должны получать ложный version mismatch. Перед изменением общей версии нужно определить, действительно ли изменилось поведение уже установленных definitions.

## Обязательные тесты

Файлы:

```text
TechMES.Calc.Tests/ContentCalculationDefinitionTests.cs
TechMES.Calc.Tests/SubstancePropertyTests.cs
TechMES.Calc.Tests/ContentArchitectureTests.cs
TechMES.Calc.Tests/Legacy/ContentCalc.cs
```

Обновить/добавить:

- `BuiltInCatalogContainsAllContentDefinitions` — новый code;
- ожидаемое количество definitions (`6` сейчас);
- `GetComponents()` — точный порядок компонентов;
- theory `ContentDefinitionMatchesContentFacade`;
- известные рабочие точки;
- границы Temperature/Pressure/configurationCode;
- все поддерживаемые порядки;
- неподдерживаемые перестановки;
- соответствие независимому legacy/reference oracle, если он существует;
- конечность и правильное количество outputs.

## Контрольный список

- [ ] Все SubstanceCode зарегистрированы.
- [ ] Добавлен `ContentSystem`.
- [ ] Реализована одна физическая корреляция без дублирования общей математики.
- [ ] В `ContentCombinationCalculator` перечислены только допустимые порядки.
- [ ] Добавлен стабильный `content.*` code и Definition.
- [ ] Output order совпадает с `ParamN` order.
- [ ] Количество outputs не превышает доступные SCADA slots.
- [ ] Новая система появилась в Content-комбобоксе после перезапуска Runtime.
- [ ] Regression tests обновлены и проходят.