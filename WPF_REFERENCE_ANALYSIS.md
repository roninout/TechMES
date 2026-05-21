# WPF Reference Analysis: TechEquipments -> TechMES Web

Этот файл фиксирует разбор WPF-проекта `TechEquipments`, который используется как функциональный эталон для переноса в WEB-версию `TechMES`.

## Источник

- Архив пользователя: `D:/C_Recipe/Work/TechEquipments/TechEquipments.zip`
- Извлечено сюда: `_reference/TechEquipments/TechEquipments`
- WPF solution: `_reference/TechEquipments/TechEquipments/TechEquipments.sln`
- WEB solution: `TechMES.sln`

Важно: в WPF `appsettings.json` есть реальные CtApi/DB настройки. При переносе в WEB не копировать секреты в git; выносить в локальные настройки, переменные окружения или user-secrets.

## Короткий вывод

WPF-проект - это не просто UI для параметров. Это рабочая операторская оболочка вокруг:

- CtApi/Cicode/SCADA;
- каталога оборудования и дерева групп Equipment;
- чтения/записи параметров оборудования;
- связей PLC, DI/DO, DryRun, linked ATV;
- live/history трендов;
- SOE событий по STW/STW01 трендам;
- PostgreSQL-справочника Info;
- сообщений для смен/устройств;
- QR-сканирования и генерации QR;
- локального восстановления состояния пользователя.

Текущий WEB уже частично повторяет WPF:

- есть архитектура `TechMES.Web` -> `TechMES.Runtime.Service` -> Application/Infrastructure adapters;
- каталог оборудования уже сделан WPF-compatible через CtApi flow `*_HASHCODE`, `*_EQUIP`, `EquipGetProperty(..., "Type", 3)`;
- `EquipmentDto` уже содержит `NodeId`, `ParentNodeId`, `IsGroup`, `IsEquipmentChildNode`;
- страница `/equipment` уже строит дерево;
- модуль Messages уже перенесен ближе всего к WPF-логике: HTTP API, PostgreSQL/InMemory store, SignalR live updates;
- `Param` и `Info` пока в WEB являются заглушками и должны стать основными следующими зонами миграции.

## Архитектура WPF

`App.xaml.cs` строит Generic Host и регистрирует зависимости:

- `PgDbContext` для EventPicker DB;
- `PgInfoDbContext` для `srd_db`;
- `IAppRuntimeContext -> AppRuntimeContext`;
- `IDbService -> PgDbService`;
- `IEquipInfoService -> EquipInfoService`;
- `IMessageService -> MessageService`;
- `CtApiService` как singleton, `ICtApiService`, `IHostedService`;
- `IEquipmentService -> EquipmentService`;
- `IUserStateService -> JsonUserStateService`;
- QR services;
- `MainViewModel`, `MainWindow`.

WPF запускается как single-instance приложение через mutex `Global\TechEquipments_SingleInstance`.

## CtApi слой

Ключевой сервис: `Services/CtApi/CtApiService.cs`.

Основные возможности:

- `OpenAsync`, `IsConnected`, `IsConnectionAvailable`;
- `TagReadAsync`, `TagWriteAsync`;
- `FindAsync`;
- `CicodeAsync`;
- `GetTrnData`;
- `UserInfo`, `LoginAsync`, `LogoutAsync`, `GetPrivAsync`;
- health monitoring и reconnect.

WPF сериализует native вызовы через gate/semaphore. Это важно сохранить и в Runtime.Service: CtApi/native DLL нельзя дергать бездумно параллельно.

## Каталог оборудования

Ключевые файлы:

- `Services/EquipmentService/EquipmentService.cs`
- `Services/EquipmentService/IEquipmentService.cs`
- `Services/EquipmentList/EquipmentListController.cs`
- `Model/EquipListBoxItem.cs`
- `Enum/EquipTypeGroup.cs`

WPF не строит список оборудования через прямую таблицу `EQUIP`. Правильный рабочий flow такой:

1. Обычное оборудование:
   - `FindAsync("Tag", "Tag=*_HASHCODE", "", "EQUIPMENT", "TAG", "COMMENT")`.
2. Группы Equipment:
   - `FindAsync("Tag", "Tag=*_EQUIP", "", "EQUIPMENT", "TAG", "COMMENT")`.
3. Реальный SCADA type:
   - `EquipGetProperty("{equipment}", "Type", 3)`.
4. Фильтр поддерживаемых типов:
   - `EquipTypeRegistry.IsSupportedType(type)`.
5. Группа типа:
   - `EquipTypeRegistry.GetGroup(type)`.
6. Для group node:
   - `EquipRefBrowseOpen("CLUSTER=...;EQUIP=...;REFCAT=EquipGroup", "REFEQUIP")`.
7. Child nodes внутри группы ссылаются на уже найденное plain equipment, но получают собственный `NodeId`.

Модель WPF `EquipListBoxItem` содержит:

- `Equipment`, `Tag`, `Type`, `Station`, `Description`;
- `TypeGroup`;
- `NodeId`, `ParentNodeId`;
- `IsGroup`, `IsEquipmentChildNode`, `IsPlainEquipmentNode`;
- `IsFavorite`, признаки наличия Info-данных;
- `DisplayText`.

Группы типов WPF:

- `All`;
- `Equipment`;
- `VGA`, `VGA_EL`, `VGD`;
- `Motor`;
- `AI`, `DI`, `DO`;
- `Atv`;
- `Favorites`.

В WEB contract сейчас нет `All` и `Favorites`, и это нормально: это UI-фильтры, а не типы оборудования. Но для UX страницы Equipment/Param они понадобятся как режимы фильтра.

## Левая панель и выбор оборудования

WPF использует DevExpress `TreeListControl`:

- `KeyFieldName="NodeId"`;
- `ParentFieldName="ParentNodeId"`;
- `RootValue="0"`;
- `AutoExpandAllNodes="True"`;
- выбор привязан к `SelectedListBoxEquipment`.

`EquipmentListController` делает:

- `ICollectionView` фильтрацию и сортировку;
- список станций только по plain equipment;
- станционный probe tag для health monitor;
- debounce поиска;
- память последнего оборудования для пары `Station|TypeGroup`;
- подавление побочных эффектов при refresh/restore selection.

Фильтры WPF:

- `Equipment`: показывать только group nodes и их child nodes;
- `Favorites`: показывать только plain equipment с `IsFavorite`;
- остальные типы: показывать plain equipment по station и type group;
- `All`: plain equipment всех типов.

Логика выбора:

- group node открывает Info/DB, но не запускает Param polling и SOE;
- normal/child node запускает актуальную вкладку: Param polling, Info reload, DB reload или SOE load;
- при смене оборудования WPF сбрасывает устаревшие Param refs, linked ATV, DryRun и overlays.

## Param: чтение параметров

Ключевые файлы:

- `Services/Params/ParamController.cs`
- `Services/Params/ParamWriteController.cs`
- `Services/Refs/ParamRefsController.cs`
- `Model/Equip/*.cs`
- `Model/Common/*.cs`
- `Views/Param/*.xaml`

Модели параметров:

- `AIParam`;
- `DIParam`;
- `DOParam`;
- `AtvParam`;
- `MotorParam`;
- `VGAParam`;
- `VGA_ElParam`;
- `VGDParam`.

Общий механизм:

1. По выбранному equipment определяется type group.
2. Берется соответствующий `*Param` class.
3. По публичным properties строится список equip items.
4. Для каждого item:
   - `TagInfo("{equip}.{item}", 0)` -> real tag name;
   - `TagCheckIfExists(tag)` проверяется и кешируется;
   - `TagReadAsync(tag)` читает значение.
5. Значение конвертируется в `bool`, `int`, `uint`, `double`, `string`.
6. Для `R` читается unit через `TagInfo(tag, 1)`.
7. Канал читается через `EquipGetProperty(equip, "Custom1", 3)`.

Важные оптимизации WPF:

- кеш `TagInfo`;
- кеш `TagInfo(..., 1)` для units;
- кеш `TagCheckIfExists`;
- кеш канала;
- ограничение параллельного `TagRead` через `CtApi:TagReadParallelism`;
- общий semaphore `_paramRwGate`, чтобы чтение и запись не пересекались.

Polling:

- раз в 5 секунд;
- только на вкладке Param;
- не читает при CtApi offline;
- не читает во время записи/редактирования;
- после записи делает короткую паузу;
- обновляет существующие UI rows без полного пересоздания, чтобы не мигал интерфейс.

## Param: запись параметров

`ParamWriteController` обрабатывает изменения UI.

Правила:

- запись только если активна вкладка Param;
- изменения, пришедшие от refresh, не записываются обратно;
- `ForceCmd` требует подтверждения при включении;
- bool пишется как `1/0`;
- числа пишутся invariant culture;
- право на запись проверяется через `IAppRuntimeContext.DevicePrivilege > 0`;
- запись идет через `WriteEquipItemAsync` или `WriteTagNameAsync`;
- operator action логируется через Cicode `SaveActionOperators(...)` best-effort.

Для WEB это означает:

- в Runtime.Service нужен отдельный Param API;
- запись должна оставаться на стороне Runtime.Service, не в Blazor;
- нужен `AllowWrites`, privilege check и operator audit;
- нужны DTO с явным `TagName/EquipItem/Value/CanWrite/IsForced`.

## Param: Settings sections и refs

`ParamRefsController` отвечает за:

- `DiDo`;
- `Plc`;
- `DryRun`;
- `Atv`;
- переходы по linked equipment;
- сброс Param UI при смене type group;
- синхронизацию коллекций без мигания.

Страницы settings:

- `None`;
- `Plc`;
- `DiDo`;
- `Alarm`;
- `TimeWork`;
- `DryRun`;
- `Atv`.

DI/DO section:

- refs читаются через `EquipRef` category `TabDIDO`;
- refs разделяются на DI и DO по `EquipTypeGroup`;
- для каждого ref читаются `DIParam` или `DOParam`;
- строки сортируются по каналу `ChanelShort`.

PLC section:

- refs читаются через `TabPLC`;
- поля `REFEQUIP`, `REFITEM`, `COMMENT`, custom field;
- строка получает `PlcTypeCustom`;
- real tag name и value читаются через `TagInfo`/`TagRead`;
- ключ строки: `EquipName + RefItem + Type`.

DryRun:

- связан с WinOpened refs;
- `_dryRunDI` и `_dryRunAI`;
- отображает linked DI/AI.

Linked ATV для Motor:

- ищется через `WinOpened` и `ASSOC="__EquipmentSic"`;
- если linked equipment имеет type group `Atv`, читается `AtvParam`;
- показывается внутри Motor Param.

## Trends

Ключевые файлы:

- `Services/Trends/ParamTrendController.cs`
- `Services/Trends/TrendItemsAttribute.cs`
- `Services/Trends/TrendSeriesStyleAttribute.cs`
- `Model/Trend/TrendPoint.cs`

Логика:

- trend items берутся из атрибутов модели, fallback `R`;
- trend tag name: `_SATrend_GetTrendTag(cluster, equip, item)`;
- live mode читает последние точки и держит X-axis около текущего времени;
- history mode не трогает visual range пользователя;
- при прокрутке влево догружает историю кусками по 60 минут;
- хранит до 24 часов точек как safety trim;
- несколько серий масштабируются в базовую Y-ось.

Для WEB лучше вынести это в Runtime.Service как API:

- endpoint для live points;
- endpoint для history window;
- DTO с raw value и plotted/scaled value;
- browser UI может рисовать chart, но не должен сам ходить в CtApi.

## SOE

Ключевые файлы:

- `Services/SOE/SoeController.cs`
- `Services/EquipmentService/EquipmentService.cs`
- `Enum/SoeEventMapper.cs`
- `DTO/EquipmentSOEDto.cs`

Flow:

1. Для selected equipment строится main model.
2. Добавляются refs из `TabDIDO`.
3. Для каждого equipment определяется trend tag:
   - обычно `STW`;
   - для `Atv` используется `STW01`, если такой tag существует.
4. Trend history сканируется назад от текущего времени к началу дня окнами по 30 минут.
5. Берется изменение 16-битного слова.
6. Изменившийся bit переводится в код:
   - `1..16` - бит включился;
   - `17..32` - бит выключился.
7. `SoeEventMapper` переводит type + bit code в текст и event key.

SOE не загружается для Equipment group nodes.

## Info module

Ключевые файлы:

- `Services/Info/EquipInfoService.cs`
- `Services/Info/InfoController.cs`
- `Services/Info/ExcelInfoDocumentImportReader.cs`
- `DTO/EquipmentInfo*.cs`
- `Views/Info/InfoTabHost.xaml`

Это самый крупный не перенесенный блок.

Сущности/таблицы:

- `equip_info`;
- `equip_photo`;
- `equip_instruction`;
- `equip_scheme`;
- link tables для photo/instruction/scheme;
- `equip_info_pdf_view`;
- `equip_favorite`;
- `equip_supplier`;
- `equip_order`;
- `equip_note`;
- индексы и upsert-логика.

Функции:

- создание таблиц;
- чтение/сохранение карточки equipment;
- favorites;
- библиотека фото/инструкций/схем;
- связи файлов с equipment;
- PDF view state: page, zoom, anchor;
- import фото из папки;
- capture фото с камеры;
- import документов из Excel;
- справочник suppliers/orders/product codes;
- notes CRUD.

Для WEB переносить лучше по слоям:

1. Contracts DTO и Runtime API для чтения карточки Info.
2. PostgreSQL repository на основе `EquipInfoService`.
3. Минимальная страница Info: карточка + favorite + notes.
4. Фото.
5. Инструкции/схемы и PDF preview.
6. Bulk imports, camera, Excel import.

## Messages

Ключевые файлы WPF:

- `Services/Messages/MessageService.cs`
- `Services/Messages/MessageController.cs`
- `DTO/EquipmentMessageDto.cs`
- `Views/Message/MessageTabHost.xaml`

WPF таблицы:

- `equip_message`;
- `equip_message_view`.

Функции:

- active/inactive messages;
- create/edit/delete;
- type/subject/text;
- created/updated author;
- viewed per device;
- delayed mark viewed;
- author can toggle activity;
- refresh with selection preservation.

Текущий WEB уже переносит это направление:

- `TechMES.Contracts/Messages`;
- `TechMES.Application/Messages/IMessageStore`;
- `TechMES.Infrastructure.PostgreSql/Messages/PostgreSqlMessageStore.cs`;
- Runtime endpoints `/api/messages`;
- SignalR hub `/hubs/messages`;
- Blazor page `/messages`.

Оставшиеся вопросы по Messages:

- проверить совпадение SQL schema с WPF;
- проверить кодировку текста в `.razor` файлах;
- довести UX/фильтры до WPF-уровня при необходимости.

## DB tabs: Operator Actions и Alarm History

Ключевые файлы:

- `Services/DB/PgDbService.cs`
- `Services/DB/DbController.cs`
- `DTO/OperatorActDTO.cs`
- `DTO/AlarmHistoryDTO.cs`

Функции:

- `CanConnectAsync`;
- operator actions за выбранную дату и equipment filter;
- alarm history за выбранную дату и equipment filter;
- локальный день переводится в UTC range;
- фильтр по equipment.

Это отдельный будущий Runtime API + WEB pages/tabs.

## QR

Ключевые файлы:

- `Services/QR/QrController.cs`
- `Services/QR/QrCodeService.cs`
- `Services/QR/QrScannerService.cs`
- `Views/Qr/QrScanWindow.xaml`

Функции:

- генерация QR PNG для одного equipment;
- bulk generation для всего списка;
- структура папок `QRCodes/{Station}/{TypeGroup}`;
- подпись equipment name сверху PNG;
- сканирование через камеру;
- после scan:
  - найти equipment;
  - выставить station/type filters;
  - записать ExternalTag best-effort;
  - перейти на Param;
  - запустить polling.

Для WEB камера может быть реализована через browser APIs, но запись ExternalTag и выбор оборудования должны идти через Runtime.Service/state.

## User state

Ключевые файлы:

- `Services/UserState/JsonUserStateService.cs`
- `Model/UserState/UserState.cs`

Файл состояния:

- `%AppData%/TechEquipments/user-state.json`.

Хранит:

- `LastEquipName`;
- `DbDate`;
- `SelectedTab`;
- `SelectedStation`;
- `SelectedTypeFilter`;
- `LastEquipmentsByFilter`;
- `QrCameraIndex`.

WEB-аналог:

- часть состояния хранить в browser local storage;
- часть, связанную с устройством/станцией, хранить в Runtime.Service или БД;
- selection уже начат через `SelectedEquipmentState`, но он живет только в scoped Blazor state.

## Сопоставление с текущим WEB

Уже сделано/почти сделано:

- Solution split на Web/Runtime/Contracts/Application/Infrastructure.
- Runtime adapter для CtApi.
- Equipment catalog WPF-compatible.
- Tree fields в `EquipmentDto`.
- Tree-like UI в `/equipment`.
- `SelectedEquipmentState`.
- Messages API/store/page/live updates.

Не перенесено:

- полноценный Param module;
- Param write/privilege/audit;
- Param settings: PLC, DI/DO, DryRun, linked ATV;
- live/history trends;
- SOE;
- Info module;
- DB operator/alarm pages;
- QR;
- station health monitor;
- persistent user state;
- role/privilege model для WEB.

## Практический план миграции

Рекомендуемый порядок:

1. Починить кодировку видимых русских строк в WEB `.razor` файлах, если они действительно сохранены mojibake, а не только неверно отображаются в консоли.
2. Зафиксировать Equipment UX:
   - All/Equipment/Favorites filters;
   - station filter;
   - search debounce;
   - remembered selection per filter;
   - selected group behavior.
3. Начать Param backend:
   - contracts для param snapshot;
   - Runtime service чтение typed parameters;
   - кеши `TagInfo`, units, existence;
   - ограничение parallel read.
4. Сделать WEB Param read-only:
   - header selected equipment;
   - type-specific panels;
   - polling;
   - offline/error states.
5. Добавить write pipeline:
   - AllowWrites;
   - privilege;
   - confirm ForceCmd;
   - audit.
6. Перенести refs:
   - DI/DO;
   - PLC;
   - linked ATV;
   - DryRun.
7. Перенести Info минимально:
   - schema/service;
   - favorite;
   - base card;
   - notes.
8. После этого:
   - photos/documents/PDF state;
   - SOE;
   - trends;
   - DB operator/alarm;
   - QR.

## Важные риски

- CtApi/native вызовы требуют аккуратной сериализации и reconnect logic.
- WEB не должен напрямую зависеть от CtApi DLL.
- Запись в SCADA должна быть закрыта настройками и правами.
- Нельзя переносить реальные пароли в репозиторий.
- WPF использует локальное состояние и desktop-only features; в WEB часть придется переосмыслить.
- Дубли equipment в дереве нормальны: одно физическое оборудование может быть root node и child node.
- Для group node нельзя запускать Param/SOE как для обычного оборудования.
- `Atv` в WPF и `ATV` в WEB должны мапиться стабильно.

## Как использовать этот файл дальше

Перед каждой крупной задачей по переносу WPF-логики в WEB открывать этот файл и соответствующие WPF-файлы в `_reference/TechEquipments/TechEquipments`.

Этот документ - рабочая память проекта. Если чат будет потерян или сжат, его можно перечитать и продолжить без повторного анализа архива.
