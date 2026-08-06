using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Calc;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// Read-only API каталога алгоритмов и ручного тестирования.
///
/// Эти endpoints не читают PostgreSQL, не обращаются к CtApi,
/// не запускают Calc.Service и не записывают результаты.
/// </summary>
public static class CalcEndpoints
{
    /// <summary>
    /// Регистрирует endpoints расчётного модуля.
    /// </summary>
    public static IEndpointRouteBuilder MapCalcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
            "/api/calc/definitions",
            GetDefinitions);

        app.MapGet(
            "/api/calc/definitions/{code}",
            GetDefinition);

        app.MapPost(
            "/api/calc/test",
            TestCalculation);

        return app;
    }

    /// <summary>
    /// Возвращает описания всех алгоритмов,
    /// встроенных в установленную версию TechMES.Calc.
    /// </summary>
    private static IResult GetDefinitions(CalculationCatalog catalog)
    {
        var response = new CalcDefinitionsResponse
        {
            Items = catalog
                .GetAll()
                .Select(CalcContractMapper.ToDefinitionDto)
                .ToList()
        };

        return Results.Ok(response);
    }

    /// <summary>
    /// Возвращает описание одного алгоритма по стабильному коду.
    /// </summary>
    private static IResult GetDefinition(string code, CalculationCatalog catalog)
    {
        if (!catalog.TryGet(code, out var definition) || definition is null)
        {
            return Results.NotFound(
                new CalcApiErrorResponse
                {
                    ErrorCode = "definition.not-found",
                    ErrorMessage =
                        $"Calculation definition '{code}' was not found."
                });
        }

        return Results.Ok(CalcContractMapper.ToDefinitionDto(definition));
    }

    /// <summary>
    /// Выполняет один ручной расчёт по переданным значениям.
    ///
    /// Ошибки валидации алгоритма возвращаются внутри CalcTestResponse.
    /// Ошибки структуры JSON-запроса возвращаются с HTTP 400.
    /// </summary>
    private static IResult TestCalculation(CalcTestRequest? request, CalculationCatalog catalog)
    {
        if (request is null)
        {
            return Results.BadRequest(
                new CalcApiErrorResponse
                {
                    ErrorCode = "request.missing",
                    ErrorMessage =
                        "Calculation test request is required."
                });
        }

        var definitionCode = request.DefinitionCode?.Trim() ?? "";

        if (definitionCode.Length == 0)
        {
            return Results.BadRequest(
                new CalcApiErrorResponse
                {
                    ErrorCode = "definition.code-empty",
                    ErrorMessage = "Calculation definition code is required."
                });
        }

        if (!catalog.TryGet(definitionCode, out var definition) || definition is null)
        {
            return Results.NotFound(
                new CalcApiErrorResponse
                {
                    ErrorCode = "definition.not-found",
                    ErrorMessage =
                        $"Calculation definition '{definitionCode}' was not found."
                });
        }

        try
        {
            var parameters = CalcContractMapper.ToParameterSet(request.Parameters);
            var result = definition.Calculate(parameters, request.IncludeTrace);

            return Results.Ok(CalcContractMapper.ToTestResponse(definition, result));
        }
        catch (CalculationException exception)
        {
            /*
             * Сюда попадают ошибки преобразования самого JSON,
             * возникшие до вызова CalculationDefinitionBase.
             *
             * Ошибки параметров внутри алгоритма уже преобразуются
             * расчётным ядром в обычный CalculationResult.Failure.
             */
            return Results.BadRequest(CalcContractMapper.ToFailureResponse(definition, exception.Code, exception.Message));
        }
    }
}