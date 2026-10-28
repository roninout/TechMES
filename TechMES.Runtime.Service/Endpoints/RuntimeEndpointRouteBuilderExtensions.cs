namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Единая точка подключения всех HTTP endpoints Runtime.Service.
/// </summary>
public static class RuntimeEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Регистрирует HTTP API всех модулей Runtime.Service.
    /// Сохраняет существующий модуль рецептов и добавляет определение глобальных ролей.
    /// </summary>
    public static IEndpointRouteBuilder MapRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthEndpoints();
        app.MapMessageEndpoints();
        app.MapEquipmentEndpoints();
        app.MapInfoEndpoints();
        app.MapParamEndpoints();

        // Определение отображаемых ролей пользователя по Windows-группам.
        app.MapRoleEndpoints();

        app.MapEventLogEndpoints();
        app.MapSoeEndpoints();
        app.MapActiveAlarmEndpoints();
        app.MapAlarmSummaryEndpoints();
        app.MapEventJournalEndpoints();
        app.MapScadaEndpoints();
        app.MapCalcEndpoints();

        // Обязательная регистрация API рецептов, параметров и стадий.
        // Без неё WEB открывает страницу, но её запросы к Runtime возвращают HTTP 404.
        app.MapRecipeEndpoints();

        return app;
    }
}