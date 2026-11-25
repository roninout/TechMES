# TechMES

Учебно-практический старт WEB-версии TechMES.

## Проекты

- `TechMES.Contracts` — общие DTO/request/response классы для обмена между WEB и Runtime Service.
- `TechMES.Application` — внутренние интерфейсы приложения. Сейчас здесь находится `IMessageStore`.
- `TechMES.Infrastructure.PostgreSql` — PostgreSQL-адаптер для Messages. Здесь находится вся SQL-логика сообщений.
- `TechMES.Runtime.Service` — backend API и будущий Windows Service. Сейчас работает как Web API.
- `TechMES.Web` — Blazor Web App + Radzen UI.

## Текущая схема Messages

```text
TechMES.Web
  -> MessageApiClient
    -> HTTP /api/messages
      -> TechMES.Runtime.Service
        -> IMessageStore
          -> PostgreSqlMessageStore
            -> PostgreSQL / srd_db
```

## Почему так

`TechMES.Web` не должен знать, какая БД используется.
`TechMES.Runtime.Service` тоже не должен содержать SQL прямо в `Program.cs`.

Runtime Service работает через интерфейс `IMessageStore`, а конкретная реализация
подключается как adapter через `MessageStorage:Provider` в `appsettings.json`.

## Как запускать

1. Проверить строку подключения в `TechMES.Runtime.Service/appsettings.json`.
2. Запустить `TechMES.Runtime.Service`.
3. Проверить `https://localhost:7101/api/health`.
4. Запустить `TechMES.Web`.
5. Открыть `/messages`.

Если PostgreSQL недоступен, можно временно вернуть:

```json
"MessageStorage": {
  "Provider": "InMemory"
}
```
