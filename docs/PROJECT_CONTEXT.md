# Project Context: TechMES

Этот файл нужен как постоянная рабочая память для локальной сессии Codex.

## Источники

- GitHub repo: `roninout/TechMES.git`
- Локальный WEB repo: `C:\Users\Kovalchuk_a\Documents\Codex\2026-05-20\new-chat\TechMES`
- ChatGPT share transcript: `CHATGPT_SHARE_TRANSCRIPT.md`
- WPF reference archive: `D:/C_Recipe/Work/TechEquipments/TechEquipments.zip`
- Extracted WPF reference: `_reference/TechEquipments/TechEquipments`
- Detailed WPF analysis: `WPF_REFERENCE_ANALYSIS.md`

## Что мы делаем

Мы переносим/переосмысливаем WPF-приложение `TechEquipments` в WEB-проект `TechMES`.

WPF является функциональным эталоном:

- каталог оборудования из CtApi;
- дерево Equipment/group/child nodes;
- Param чтение/запись;
- PLC/DI/DO/DryRun/linked ATV;
- trends;
- SOE;
- Info карточки, фото, инструкции, схемы, notes;
- Messages;
- DB operator actions/alarm history;
- QR;
- user state.

## Текущий WEB solution

Основные проекты:

- `TechMES.Web` - Blazor/Radzen UI.
- `TechMES.Runtime.Service` - HTTP API + SignalR, будущий Windows Service.
- `TechMES.Contracts` - DTO/contracts между Web и Runtime.
- `TechMES.Application` - interfaces/application abstractions.
- `TechMES.Infrastructure.CtApi` - CtApi/native adapter.
- `TechMES.Infrastructure.PostgreSql` - PostgreSQL adapters.

Архитектура: WEB не должен напрямую работать с CtApi или БД. WEB общается с Runtime.Service по HTTP/SignalR.

## Уже проверено

- `dotnet build TechMES.sln` проходил успешно.
- `dotnet test TechMES.sln --no-build` проходил, но тестовых проектов нет.
- Build warnings были в основном nullable/legacy warnings внутри `TechMES.Infrastructure.CtApi`.

## Уже реализовано в WEB

- Equipment contracts с `NodeId`, `ParentNodeId`, `IsGroup`, `IsEquipmentChildNode`.
- WPF-compatible CtApi equipment catalog:
  - `Tag=*_HASHCODE`;
  - `Tag=*_EQUIP`;
  - `EquipGetProperty(..., "Type", 3)`;
  - `EquipGroup` refs.
- `/equipment` как tree-like UI.
- `SelectedEquipmentState`.
- Messages contracts/store/API/page/SignalR.

## Основные незавершенные блоки

- Полноценный `Param`.
- Param write/privilege/audit.
- PLC/DI/DO/DryRun/linked ATV sections.
- Trends.
- SOE.
- Info module.
- DB pages.
- QR.
- User state persistence.
- Station health monitor.

## Практический следующий шаг

Лучший следующий крупный шаг: начать перенос `Param` с read-only Runtime API и WEB polling, потому что это центральный операторский сценарий и на нем завязаны trends, refs, writes и SOE.

Перед работой по WPF-логике сначала открыть `WPF_REFERENCE_ANALYSIS.md`.
