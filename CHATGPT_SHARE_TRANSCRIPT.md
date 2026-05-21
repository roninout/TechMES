# TechMES WPF-логика исправления

Источник: https://chatgpt.com/share/6a0d9ab9-3708-83eb-a8b6-7cab31f97edc

Сообщений: 91

## 1. Пользователь

Вот архив WPF-проекта. (ты можешь держать его в памяти?) В нем есть метод GetAllEquipmentsAsync, он возвращает и название и группы.... нам,я думаю такой же нужен. Потому что сейчас мы возвращаем ListBox, но нужно будет сделать Tree для оборудования. 

Сейчас возникает такое сообщение

info: TechMES.Infrastructure.CtApi.Native.CtApiNativeClient[0]
      Opening CtApi. Path=G:\BinCt, Server=26.68.197.96, User=Al
info: TechMES.Infrastructure.CtApi.Native.CtApiNativeClient[0]
      CtApi opened successfully.
info: TechMES.Infrastructure.CtApi.Gateways.CtApiEquipmentCatalogProvider[0]
      Загружаем Equipment catalog через CtApi. Table=EQUIP, Properties=NAME,COMMENT,TYPE,EQUIPTYPE,EQUIP_TYPE,TYPEGROUP,TYPE_GROUP, ScadaAllowedTypes=Equipment,DigitalIn,DigitalInSiemens,DigitalOut,DigitalOutSiemens,Motor,MotorSiemens,AnalogIn,AnalogInSiemens,AnalogInCalc,AnalogInCalcSiemens,ValveA,ValveASiemens,ValveA_EL,ValveD,ValveDSiemens,Atv,AtvSiemens, Filter=TYPE=AnalogIn OR TYPE=AnalogInCalc OR TYPE=AnalogInCalcSiemens OR TYPE=AnalogInSiemens OR TYPE=Atv OR TYPE=AtvSiemens OR TYPE=DigitalIn OR TYPE=DigitalInSiemens OR TYPE=DigitalOut OR TYPE=DigitalOutSiemens OR TYPE=Equipment OR TYPE=Motor OR TYPE=MotorSiemens OR TYPE=ValveA OR TYPE=ValveA_EL OR TYPE=ValveASiemens OR TYPE=ValveD OR TYPE=ValveDSiemens
warn: TechMES.Infrastructure.CtApi.Gateways.CtApiEquipmentCatalogProvider[0]
      Equipment catalog: 768 item(s) имеют TypeGroup=Unknown. Проверьте EquipmentCatalog:ScadaTypeField, ScadaTypeFieldCandidates и TypeAliases.
info: TechMES.Infrastructure.CtApi.Gateways.CtApiEquipmentCatalogProvider[0]
      Equipment catalog загружен через CtApi. RawCount=768, MappedCount=768, FilteredCount=0

Оборудования в комбобоксах нет и типов там тоже нет. Вот текущий архив проекта. Просмотри его что с ним не так.

## 2. ChatGPT

Я посмотрю оба архива: WPF как эталон по `GetAllEquipmentsAsync`, и текущий TechMES — где именно теряются SCADA-типы и почему после фильтра получается `FilteredCount=0`. Затем дам точечную правку под текущую структуру проекта.

## 3. Tool

The output of this plugin was redacted.

## 4. Tool

The output of this plugin was redacted.

## 5. Tool

The output of this plugin was redacted.

## 6. Tool

The output of this plugin was redacted.

## 7. Tool

The output of this plugin was redacted.

## 8. Tool

The output of this plugin was redacted.

## 9. Tool

The output of this plugin was redacted.

## 10. Tool

The output of this plugin was redacted.

## 11. Tool

The output of this plugin was redacted.

## 12. Tool

The output of this plugin was redacted.

## 13. Tool

The output of this plugin was redacted.

## 14. ChatGPT

Нашёл главную разницу: WPF вообще не берёт тип из `EQUIP.TYPE`. Он получает список через таблицу `Tag` (`*_HASHCODE` и `*_EQUIP`), а реальный тип читает отдельным Cicode-вызовом `EquipGetProperty("...", "Type", 3)`. Поэтому текущий WEB-provider видит 768 имён, но у всех пустой/неподходящий `TypeName`, и фильтр обнуляет список.

## 15. Tool

The output of this plugin was redacted.

## 16. Tool

The output of this plugin was redacted.

## 17. Tool

The output of this plugin was redacted.

## 18. Tool

The output of this plugin was redacted.

## 19. Tool

The output of this plugin was redacted.

## 20. Tool

The output of this plugin was redacted.

## 21. Tool

The output of this plugin was redacted.

## 22. Tool

The output of this plugin was redacted.

## 23. Tool

The output of this plugin was redacted.

## 24. Tool

The output of this plugin was redacted.

## 25. Tool

The output of this plugin was redacted.

## 26. Tool

The output of this plugin was redacted.

## 27. Tool

The output of this plugin was redacted.

## 28. ChatGPT

This code was redacted.

## 29. Tool

The output of this plugin was redacted.

## 30. ChatGPT

Да, я запомнил это как текущую точку по TechMES WEB и WPF-эталону.

Проблема найдена: **текущий TechMES неправильно повторял WPF-логику загрузки оборудования**.

В WPF `GetAllEquipmentsAsync` делает так:

1. Берёт обычное оборудование из таблицы `Tag` по фильтру:

```csharp
Tag=*_HASHCODE
```

2. Берёт группы из таблицы `Tag` по фильтру:

```csharp
Tag=*_EQUIP
```

3. Реальный тип оборудования читает не из `EQUIP.TYPE`, а через Cicode:

```csharp
EquipGetProperty("S01.H01.M01", "Type", 3)
```

А в текущем TechMES provider пытался читать таблицу `EQUIP` и поле `TYPE`. Поэтому получалось:

```text
RawCount=768
MappedCount=768
FilteredCount=0
```

То есть имена оборудования были найдены, но `TypeName` не определялся, всё уходило в `TypeGroup=Unknown`, а потом фильтр всё отбрасывал.

Я подготовил исправленный архив:

[Скачать TechMES_WPF_EquipmentCatalog_Fix.zip](sandbox:/mnt/data/TechMES_WPF_EquipmentCatalog_Fix.zip)

Что изменил:

- добавил `CicodeAsync` в `ICtApiNativeClient`;
- реализовал `CicodeAsync` в `CtApiNativeClient` через существующий `_apiGate`;
- полностью заменил `CtApiEquipmentCatalogProvider` на WPF-compatible логику:
  - `FindAsync("Tag", "Tag=*_HASHCODE", ...)`;
  - `FindAsync("Tag", "Tag=*_EQUIP", ...)`;
  - `EquipGetProperty(..., "Type", 3)` для типа;
  - `EquipRefBrowse...` для дочерних элементов групп;
- добавил в `EquipmentDto` поля под будущий Tree:
  - `NodeId`;
  - `ParentNodeId`;
  - `IsEquipmentChildNode`;
- убрал дублирующее поле `Database = "ERROR"` в `Program.cs`;
- добавил `TechMES.Infrastructure.CtApi` в классический `TechMES.sln`, потому что в `.slnx` он был, а в `.sln` отсутствовал.

После запуска ожидаемый лог должен быть уже примерно такой:

```text
Загружаем Equipment catalog через WPF-compatible CtApi flow...
Equipment catalog загружен через CtApi. HashTagRows=..., GroupTagRows=..., PlainCount=..., GroupCount=..., TotalNodes=...
```

Главное: `PlainCount` должен стать больше нуля. Тогда в WEB появятся и станции, и типы `AI / DI / DO / Motor / ATV / VGA / VGD / VGA_EL / Equipment`.

Сборку я не запускал, потому что в текущей среде нет `dotnet CLI`. Код проверил по структуре проекта и по WPF-методу. Сейчас WEB-страница всё ещё отображает список через `RadzenDataList`, но API уже отдаёт данные так, чтобы следующим шагом спокойно заменить список на Tree.

## 31. Пользователь

Да, заработало! )) Просмотри еще раз WPF-проект и подумай, что нам лучше сейчас делать дальше. Также схематично распиши как у нас все запросы/ответы проходят и нет ли унас где-то производитеьной, или если будет например 20 клиентов, затычек. 

Еще, я бы убрал TechLogger а использовал стандартный.

## 32. ChatGPT

Отлично, значит мы попали в правильную WPF-логику. Сейчас ещё раз сравню WPF и текущий TechMES, отдельно посмотрю поток `Web -> Runtime.Service -> CtApi/PostgreSQL/SignalR`, и сразу уберу зависимость от `TechLogger` в CtApi-инфраструктуре.

## 33. Tool

The output of this plugin was redacted.

## 34. Tool

The output of this plugin was redacted.

## 35. Tool

The output of this plugin was redacted.

## 36. Tool

The output of this plugin was redacted.

## 37. Tool

The output of this plugin was redacted.

## 38. Tool

The output of this plugin was redacted.

## 39. Tool

The output of this plugin was redacted.

## 40. Tool

The output of this plugin was redacted.

## 41. ChatGPT

По производительности уже видно один важный момент: CtApi правильно закрыт одним `_apiGate`, это безопасно для нативной библиотеки, но все CtApi-вызовы становятся последовательной очередью. Для 20 клиентов это нормально для каталога/сообщений, но опасно для будущего частого live polling параметров — там нужен кэш/агрегация запросов, а не отдельный TagRead от каждого браузера.

## 42. Tool

The output of this plugin was redacted.

## 43. Tool

The output of this plugin was redacted.

## 44. Tool

The output of this plugin was redacted.

## 45. Tool

The output of this plugin was redacted.

## 46. Tool

The output of this plugin was redacted.

## 47. Tool

The output of this plugin was redacted.

## 48. Tool

The output of this plugin was redacted.

## 49. Tool

The output of this plugin was redacted.

## 50. Tool

The output of this plugin was redacted.

## 51. Tool

The output of this plugin was redacted.

## 52. Tool

The output of this plugin was redacted.

## 53. Tool

The output of this plugin was redacted.

## 54. Tool

The output of this plugin was redacted.

## 55. Tool

The output of this plugin was redacted.

## 56. Tool

The output of this plugin was redacted.

## 57. Tool

The output of this plugin was redacted.

## 58. Tool

The output of this plugin was redacted.

## 59. Tool

The output of this plugin was redacted.

## 60. Tool

The output of this plugin was redacted.

## 61. Tool

The output of this plugin was redacted.

## 62. ChatGPT

Супер, что заработало ))

Я ещё раз сверил WPF-логику с текущим TechMES и подготовил обновлённый архив, где убрал `TechLogger` shim и перевёл legacy `CtApi.cs` на стандартный `Microsoft.Extensions.Logging`.

[Скачать TechMES_RemoveTechLogger_ArchitectureReview.zip](sandbox:/mnt/data/TechMES_RemoveTechLogger_ArchitectureReview.zip)

Сборку я не запускал: в этой среде нет `dotnet CLI`.

## Что изменил в архиве

В `TechMES.Infrastructure.CtApi/Legacy/CtApi.cs`:

```csharp
using Microsoft.Extensions.Logging;
```

Вместо старого:

```csharp
using TechLogger;
```

Добавил внутрь legacy wrapper-а стандартный logger:

```csharp
/// <summary>
/// Стандартный logger .NET.
///
/// В WPF-проекте этот wrapper писал в отдельный старый logger.
/// В TechMES используем общий механизм Microsoft.Extensions.Logging,
/// чтобы все сообщения Runtime.Service попадали в один pipeline логирования.
/// </summary>
private readonly ILogger? _logger;

public CtApi(ILogger? logger = null)
{
    _logger = logger;
}
```

И в `CtApiNativeClient` теперь передаём logger в legacy wrapper:

```csharp
_ctApi = new LegacyCtApiClient(logger);
```

Также удалил файл:

```text
TechMES.Infrastructure.CtApi/Compatibility/TechLoggerShim.cs
```

То есть теперь отдельного `TechLogger` namespace в проекте нет.

---

## Как сейчас проходят запросы/ответы

### 1. Загрузка каталога оборудования

Схема сейчас такая:

```text
Browser
  ↓ Blazor Server UI
TechMES.Web /equipment
  ↓ HTTP GET /api/equipment
TechMES.Runtime.Service
  ↓ IEquipmentCatalogProvider
CtApiEquipmentCatalogProvider
  ↓ ICtApiNativeClient
CtApiNativeClient
  ↓ legacy CtApi.cs
CtApi.dll / Plant SCADA
```

Ответ идёт назад так:

```text
Plant SCADA
  → CtApi rows + Cicode result
  → EquipmentDto[]
  → EquipmentListResponse
  → TechMES.Web
  → Equipment page / filters / selected equipment
```

Сейчас catalog provider уже делает правильно, как WPF:

```text
Tag=*_HASHCODE  → обычное оборудование
Tag=*_EQUIP     → group nodes
EquipGetProperty(name, "Type", 3) → реальный SCADA type
EquipRefBrowse... category EquipGroup → дочерние элементы групп
```

Это хорошая база под Tree.

---

### 2. Messages

```text
Browser
  ↓ Blazor Server page /messages
TechMES.Web
  ↓ HTTP GET /api/messages
TechMES.Runtime.Service
  ↓ IMessageStore
PostgreSqlMessageStore
  ↓ SQL
PostgreSQL
```

Когда сообщение создаётся/редактируется/удаляется:

```text
TechMES.Web
  ↓ POST/DELETE
Runtime.Service
  ↓ PostgreSQL
  ↓ SignalR event "MessagesChanged"
All opened WEB clients
  ↓ reload via HTTP GET /api/messages
```

То есть SignalR не передаёт весь список сообщений. Он только говорит: “данные изменились”. Это правильно.

---

### 3. Runtime status

```text
RuntimeStatusBlock
  ↓ каждые 30 секунд
GET /api/health
  ↓
Runtime.Service
  ↓ IMessageStore.GetActiveMessageCountAsync()
  ↓
PostgreSQL
```

Это лёгкий запрос. Для 20 клиентов нормально.

---

### 4. SCADA tag read/write

Сейчас низкоуровневая схема такая:

```text
Browser / future Param page
  ↓ HTTP
Runtime.Service /api/scada/tags/...
  ↓ IPlantScadaGateway
CtApiPlantScadaGateway
  ↓ ICtApiNativeClient
CtApiNativeClient
  ↓ CtApi.cs
CtApi.dll
```

Но для будущего Param я бы **не делал много прямых `/api/scada/tags/{tag}` запросов из WEB**. Это быстро создаст очередь.

Лучше делать так:

```text
Param page
  ↓ GET /api/param/{equipmentName}
Runtime.Service
  ↓ ParamSnapshotProvider
  ↓ читает пачку нужных tags через CtApi
  ↓ возвращает один DTO
WEB обновляет UI одним ответом
```

---

## Где могут быть затычки при 20 клиентах

### 1. CtApi — главный потенциальный bottleneck

Сейчас `CtApiNativeClient` имеет общий `_apiGate`:

```csharp
private readonly SemaphoreSlim _apiGate = new(1, 1);
```

Это правильно, потому что старый CtApi wrapper лучше не дёргать параллельно из разных потоков.

Но цена такая:

```text
20 клиентов одновременно читают tags
→ все CtApi calls становятся в одну очередь
→ UI может начать ждать
```

Для каталога оборудования это не страшно, потому что каталог кэшируется.

Для будущих параметров это важно. Нельзя делать так:

```text
20 клиентов × 30 tags × каждые 2 секунды
```

Это уже будет тяжело.

Правильнее:

```text
Runtime.Service читает нужное оборудование сам
→ кэширует snapshot на 1-2 секунды
→ все WEB-клиенты получают один и тот же готовый snapshot
```

Ещё лучше позже:

```text
WEB подписывается на SignalR group по equipmentName
Runtime.Service сам обновляет Param snapshot
Runtime.Service рассылает изменения только подписанным клиентам
```

---

### 2. Messages сейчас нормально

Для 20 клиентов проблем не вижу.

Что есть сейчас:

```text
Save/Edit/Delete
→ PostgreSQL
→ SignalR event
→ все клиенты reload messages
```

Если сообщений будет немного/средне — отлично.

Если сообщений станет тысячи, тогда нужно будет добавить:

```text
/api/messages?page=1&pageSize=50
```

Но сейчас это не первоочередное.

---

### 3. Equipment catalog сейчас нормально

Сейчас provider грузит каталог один раз и держит в памяти:

```csharp
private List<EquipmentDto> _cache = [];
```

Для 20 клиентов это хорошо:

```text
первый запуск Runtime.Service → CtApi загрузка
потом клиенты → получают готовый cache
```

Единственное, позже стоит добавить кнопку/endpoint:

```text
POST /api/equipment/reload
```

Чтобы можно было перечитать каталог без перезапуска сервиса.

---

### 4. Health worker нормально, но надо следить

`PlantScadaHealthWorker` проверяет CtApi каждые 10 секунд. Это нормально.

Но важно: если потом WEB тоже начнёт часто дёргать `/api/scada/health`, а внутри будет реальный CtApi probe — это тоже пойдёт в общую CtApi очередь.

Поэтому я бы разделил:

```text
Runtime.Service сам проверяет CtApi в фоне
WEB читает только последнее сохранённое состояние
```

А не заставляет каждый браузер делать реальный probe.

---

## Что лучше делать дальше

Я бы шёл в таком порядке.

### Шаг 1. Переделать Equipment page из списка в Tree

У нас уже есть поля:

```csharp
NodeId
ParentNodeId
IsGroup
IsEquipmentChildNode
ParentName
```

Поэтому следующий логичный шаг — заменить `RadzenDataList` на Tree/TreeDataGrid.

Цель:

```text
Station / root equipment
Equipment group
  ├── child equipment
  └── child equipment
```

И сразу заложить поведение:

```text
Group node selected
→ Info можно открыть
→ Param пока не открывать
→ SOE/Trends позже скрывать

Normal equipment selected
→ Info / Param / Messages context работают
```

---

### Шаг 2. Сделать нормальный общий SelectedEquipment flow

Сейчас `SelectedEquipmentState` уже есть в WEB. Это хорошо.

Дальше нужно чётко закрепить:

```text
Equipment page выбирает equipment
→ SelectedEquipmentState обновляется
→ Header показывает выбор
→ Info page использует этот выбор
→ Param page использует этот выбор
```

Это уже почти готово.

---

### Шаг 3. Потом Info

После Tree я бы переносил Info, а не Param.

Почему:

- Info в основном работает через PostgreSQL;
- меньше риска перегрузить CtApi;
- можно быстрее получить полезный WEB-модуль;
- фото/PDF/notes хорошо ложатся на WEB.

Схема будет такая:

```text
TechMES.Web /info
  ↓ GET /api/info/{equipmentName}
Runtime.Service
  ↓ IEquipmentInfoStore
PostgreSQL
```

Файлы лучше не тащить всегда вместе со списком. Лучше:

```text
GET /api/info/{equipment}/summary
GET /api/info/photos/{id}
GET /api/info/documents/{id}
```

---

### Шаг 4. Param делать через snapshot, не через отдельные tag-запросы

Для Param я бы сразу делал правильно:

```text
GET /api/param/{equipmentName}/snapshot
```

Runtime.Service внутри сам решает:

```text
Motor → какие tags читать
AI    → какие tags читать
VGD   → какие refs читать
```

И возвращает один DTO:

```csharp
public sealed class ParamSnapshotResponse
{
    public string EquipmentName { get; set; } = "";
    public EquipmentTypeGroup TypeGroup { get; set; }
    public DateTime Time { get; set; }
    public List<ParamValueDto> Values { get; set; } = [];
}
```

Так мы не создадим 20×N отдельных HTTP и CtApi запросов.

---

## Мой вывод

Сейчас архитектура стала правильной:

```text
WEB не знает про CtApi
WEB не знает про PostgreSQL напрямую
WEB работает только через Runtime.Service
Runtime.Service работает через interfaces/adapters
CtApi спрятан в TechMES.Infrastructure.CtApi
PostgreSQL спрятан в TechMES.Infrastructure.PostgreSql
```

Главный риск на будущее — **не перегрузить CtApi частыми параметрами от каждого клиента**.

Поэтому следующий лучший шаг:

```text
1. Equipment Tree
2. выбор group/child/normal node
3. Info module
4. Param через snapshot/cache/SignalR groups
```

А `TechLogger` я уже убрал в приложенном архиве.

## 63. Пользователь

давай

## 64. ChatGPT

Начинаю с первого шага: переведу страницу оборудования на Tree, используя уже добавленные `NodeId/ParentNodeId/IsGroup/IsEquipmentChildNode`. Постараюсь сделать это без ломки текущих фильтров, выбора оборудования и общего `SelectedEquipmentState`.

## 65. Tool

The output of this plugin was redacted.

## 66. Tool

The output of this plugin was redacted.

## 67. Tool

The output of this plugin was redacted.

## 68. Tool

The output of this plugin was redacted.

## 69. Tool

The output of this plugin was redacted.

## 70. ChatGPT

Перехожу к правке самой страницы `Equipment.razor`: вместо плоского `DataList` сделаю древовидное отображение с раскрытием групп. Важно: API уже отдаёт child-ноды, поэтому Runtime.Service сейчас трогать не нужно.

## 71. Tool

The output of this plugin was redacted.

## 72. Tool

The output of this plugin was redacted.

## 73. Tool

The output of this plugin was redacted.

## 74. Tool

The output of this plugin was redacted.

## 75. Tool

The output of this plugin was redacted.

## 76. Tool

The output of this plugin was redacted.

## 77. Tool

The output of this plugin was redacted.

## 78. Tool

The output of this plugin was redacted.

## 79. Tool

The output of this plugin was redacted.

## 80. Tool

The output of this plugin was redacted.

## 81. Tool

The output of this plugin was redacted.

## 82. ChatGPT

Сделал следующий шаг — перевёл страницу **Equipment** с плоского списка на дерево.

Архив:

[Скачать TechMES_EquipmentTree.zip](sandbox:/mnt/data/TechMES_EquipmentTree.zip)

Что изменил:

1. В `TechMES.Web/Components/Pages/Equipment.razor`
   - убрал плоский `RadzenDataList`;
   - добавил tree-like отображение;
   - добавил раскрытие/сворачивание group-нод;
   - child-ноды теперь отображаются с отступом под группой;
   - фильтры Station / Type / Search продолжают работать;
   - если найден child-node, его parent-группа автоматически добавляется в дерево;
   - выбранный node сохраняется в `SelectedEquipmentState`.

2. В `TechMES.Runtime.Service/Equipment/InMemoryEquipmentCatalogProvider.cs`
   - добавил заполнение `NodeId`, `ParentNodeId`, `IsEquipmentChildNode`;
   - это нужно, чтобы Tree UI можно было проверить даже без реального CtApi.

3. `TechLogger` в этом архиве уже не используется — осталась версия со стандартным `Microsoft.Extensions.Logging`.

Схема теперь такая:

```text
Runtime.Service
  ↓
EquipmentDto[]
  - root equipment
  - group nodes
  - child equipment nodes

WEB /equipment
  ↓
строит дерево по NodeId / ParentNodeId
  ↓
выбранный узел кладёт в SelectedEquipmentState
```

Сборку я не запускал, потому что в среде нет `dotnet CLI`.

После проверки у тебя на машине важно посмотреть:

```text
1. Открывается ли /equipment
2. Видны ли group-ноды Equipment
3. Раскрываются ли группы
4. Работают ли фильтры по Station / Type
5. При выборе child-ноды правильно ли обновляется верхний SelectedEquipmentBlock
```

Если это работает, следующий логичный шаг — сделать поведение страниц `Info` и `Param` в зависимости от выбранного узла: group node / ordinary equipment / child node.

## 83. Пользователь

Не работатет отображение сгруппированных нодов если выбран тип Equipment, если выбран ALL, то все норм

## 84. ChatGPT

Понял: ошибка именно в фильтрации Tree — при `ALL` parent-группы остаются, а при `Equipment` child-ноды, скорее всего, отсекаются по `TypeGroup`, поэтому группы выглядят пустыми или не строятся. Проверю текущий `Equipment.razor` и поправлю логику так, чтобы режим `Equipment` показывал group-ноды вместе с их дочерними узлами.

## 85. Tool

The output of this plugin was redacted.

## 86. Tool

The output of this plugin was redacted.

## 87. Tool

The output of this plugin was redacted.

## 88. Tool

The output of this plugin was redacted.

## 89. Tool

The output of this plugin was redacted.

## 90. Tool

The output of this plugin was redacted.

## 91. ChatGPT

Да, причина именно в фильтре `Type = Equipment`.

При `All` дерево строилось нормально, потому что в него попадали и group-ноды, и child-ноды.  
А при `Equipment` фильтр оставлял только group-ноды:

```csharp
equipment.TypeGroup == Equipment
```

Но дочерние элементы внутри этих групп имеют типы `Motor`, `AI`, `DI`, `VGA` и т.д., поэтому они отсеивались. В результате группа отображалась без детей.

Исправил:

[Скачать TechMES_EquipmentTree_TypeEquipmentFix.zip](sandbox:/mnt/data/TechMES_EquipmentTree_TypeEquipmentFix.zip)

Что изменил в `Equipment.razor`:

```csharp
// Если выбран Type = Equipment,
// то вместе с group-нодой дополнительно включаем её дочерние узлы.
if (isEquipmentTypeFilter && equipment.IsGroup)
{
    IncludeNodeWithChildren(equipment, includedNodeIds);
    _expandedNodeIds.Add(GetNodeId(equipment));
}
```

Добавил метод:

```csharp
private void IncludeNodeWithChildren(
    EquipmentDto equipment,
    HashSet<string> includedNodeIds)
{
    var parentNodeId = GetNodeId(equipment);

    var children = _allEquipments
        .Where(x => string.Equals(x.ParentNodeId, parentNodeId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    foreach (var child in children)
    {
        includedNodeIds.Add(GetNodeId(child));

        // Оставляем метод рекурсивным на будущее.
        // Сейчас у нас обычно один уровень: Equipment group -> child equipment.
        IncludeNodeWithChildren(child, includedNodeIds);
    }
}
```

Теперь логика такая:

```text
Type = All
→ показываем обычные root-ноды, группы и child-ноды

Type = Motor / AI / DI / ...
→ показываем найденные элементы + их parent-группу, если это child-node

Type = Equipment
→ показываем group-ноды + их дочерние элементы, даже если дочерние элементы имеют другой TypeGroup
```

Сборку не запускал, но правка точечная только в `Equipment.razor`.
