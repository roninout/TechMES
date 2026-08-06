using Microsoft.Extensions.Options;
using TechMES.Application.Calc;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Contracts.Calc;
using TechMES.Runtime.Service.Calc;
using TechMES.Runtime.Service.Runtime;
using TechMES.Runtime.Service.Settings;
using System.Security.Cryptography;
using System.Text;

namespace TechMES.Runtime.Service.Endpoints;

/// <summary>
/// API каталога алгоритмов, ручного тестирования
/// и конфигурации расчётных заданий.
/// </summary>
public static class CalcEndpoints
{
    /// <summary>
    /// Регистрирует endpoints расчётного модуля.
    /// </summary>
    public static IEndpointRouteBuilder MapCalcEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/calc/definitions", GetDefinitions);
        app.MapGet("/api/calc/definitions/{code}", GetDefinition);
        app.MapPost("/api/calc/test", TestCalculation);

        app.MapGet("/api/calc/configuration/snapshot", GetConfigurationSnapshotAsync);

        app.MapGet("/api/calc/jobs", GetJobsAsync);
        app.MapGet("/api/calc/jobs/{id:long}", GetJobAsync);
        app.MapPost("/api/calc/jobs", CreateJobAsync);
        app.MapPut("/api/calc/jobs/{id:long}", UpdateJobAsync);
        app.MapDelete("/api/calc/jobs/{id:long}", DeleteJobAsync);

        return app;
    }

    /// <summary>
    /// Возвращает все встроенные алгоритмы.
    /// </summary>
    private static IResult GetDefinitions(CalculationCatalog catalog)
    {
        return Results.Ok(new CalcDefinitionsResponse
        {
            Items = catalog.GetAll().Select(CalcContractMapper.ToDefinitionDto).ToList()
        });
    }

    /// <summary>
    /// Возвращает описание одного алгоритма.
    /// </summary>
    private static IResult GetDefinition(string code, CalculationCatalog catalog)
    {
        return catalog.TryGet(code, out var definition) && definition is not null
            ? Results.Ok(CalcContractMapper.ToDefinitionDto(definition))
            : NotFound("definition.not-found", $"Calculation definition '{code}' was not found.");
    }

    /// <summary>
    /// Выполняет ручной read-only расчёт.
    /// </summary>
    private static IResult TestCalculation(CalcTestRequest? request, CalculationCatalog catalog)
    {
        if (request is null)
            return BadRequest("request.missing", "Calculation test request is required.");

        var code = request.DefinitionCode?.Trim() ?? "";

        if (code.Length == 0)
            return BadRequest("definition.code-empty", "Calculation definition code is required.");

        if (!catalog.TryGet(code, out var definition) || definition is null)
            return NotFound("definition.not-found", $"Calculation definition '{code}' was not found.");

        try
        {
            var parameters = CalcContractMapper.ToParameterSet(request.Parameters);
            var result = definition.Calculate(parameters, request.IncludeTrace);
            return Results.Ok(CalcContractMapper.ToTestResponse(definition, result));
        }
        catch (CalculationException ex)
        {
            return Results.BadRequest(CalcContractMapper.ToFailureResponse(definition, ex.Code, ex.Message));
        }
    }

    /// <summary>
    /// Возвращает все сохранённые задания.
    /// </summary>
    private static async Task<IResult> GetJobsAsync(ICalcJobStore store, CancellationToken ct)
    {
        var jobs = await store.GetAllAsync(ct);
        return Results.Ok(new CalcJobsResponse { Items = jobs.ToList() });
    }

    /// <summary>
    /// Возвращает одно сохранённое задание.
    /// </summary>
    private static async Task<IResult> GetJobAsync(long id, ICalcJobStore store, CancellationToken ct)
    {
        var job = await store.GetAsync(id, ct);
        return job is null ? NotFound("job.not-found", $"Calculation job {id} was not found.") : Results.Ok(job);
    }

    /// <summary>
    /// Создаёт новое задание в shadow/read-only режиме.
    /// </summary>
    private static async Task<IResult> CreateJobAsync(CalcJobSaveRequest? request, ICalcJobStore store, CalcJobValidator validator, HttpContext httpContext, IAppRuntimeContext runtime, IOptions<CalcConfigurationOptions> options, CancellationToken ct)
    {
        if (!options.Value.EditingEnabled)
            return EditingDisabled();

        var validation = validator.Validate(request, isUpdate: false);

        if (!validation.IsValid)
            return BadRequest(validation.ErrorCode!, validation.ErrorMessage!);

        try
        {
            var job = await store.CreateAsync(request!, ResolveActor(httpContext, runtime), ct);
            return Results.Created($"/api/calc/jobs/{job.Id}", job);
        }
        catch (ArgumentException ex)
        {
            return BadRequest("job.invalid", ex.Message);
        }
    }

    /// <summary>
    /// Обновляет задание при совпадении ExpectedRevision.
    /// </summary>
    private static async Task<IResult> UpdateJobAsync(long id, CalcJobSaveRequest? request, ICalcJobStore store, CalcJobValidator validator, HttpContext httpContext, IAppRuntimeContext runtime, IOptions<CalcConfigurationOptions> options, CancellationToken ct)
    {
        if (!options.Value.EditingEnabled)
            return EditingDisabled();

        var validation = validator.Validate(request, isUpdate: true);

        if (!validation.IsValid)
            return BadRequest(validation.ErrorCode!, validation.ErrorMessage!);

        try
        {
            var job = await store.UpdateAsync(id, request!, ResolveActor(httpContext, runtime), ct);
            return job is null ? NotFound("job.not-found", $"Calculation job {id} was not found.") : Results.Ok(job);
        }
        catch (CalcJobRevisionConflictException ex)
        {
            return Results.Conflict(new CalcJobRevisionConflictResponse
            {
                ErrorMessage = ex.Message,
                JobId = ex.JobId,
                ExpectedRevision = ex.ExpectedRevision,
                CurrentRevision = ex.CurrentRevision
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest("job.invalid", ex.Message);
        }
    }

    /// <summary>
    /// Удаляет задание, если от него не зависят другие расчёты.
    /// </summary>
    private static async Task<IResult> DeleteJobAsync(long id, ICalcJobStore store, IOptions<CalcConfigurationOptions> options, CancellationToken ct)
    {
        if (!options.Value.EditingEnabled)
            return EditingDisabled();

        try
        {
            return await store.DeleteAsync(id, ct)
                ? Results.NoContent()
                : NotFound("job.not-found", $"Calculation job {id} was not found.");
        }
        catch (CalcJobDependencyException ex)
        {
            return Results.Conflict(new CalcApiErrorResponse
            {
                ErrorCode = "job.has-dependencies",
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// Возвращает read-only снимок enabled-заданий для Calc.Service.
    ///
    /// В snapshot попадают только задания, которые прошли повторную
    /// проверку по текущему CalculationCatalog Runtime.Service.
    /// </summary>
    private static async Task<IResult> GetConfigurationSnapshotAsync(ICalcJobStore store, CalcJobValidator validator, CancellationToken ct)
    {
        var enabledJobs = (await store.GetAllAsync(ct))
            .Where(job => job.Enabled)
            .OrderBy(job => job.SortOrder)
            .ThenBy(job => job.Id)
            .ToList();

        var jobs = new List<CalcExecutionJobDto>();
        var issues = new List<CalcConfigurationIssueDto>();

        foreach (var job in enabledJobs)
        {
            var validation = validator.ValidateStored(job);

            if (!validation.IsValid)
            {
                issues.Add(new CalcConfigurationIssueDto
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    ErrorCode = validation.ErrorCode ?? "job.invalid",
                    ErrorMessage = validation.ErrorMessage ?? "Calculation job configuration is invalid."
                });

                continue;
            }

            jobs.Add(ToExecutionJob(job));
        }

        return Results.Ok(new CalcConfigurationSnapshotDto
        {
            Version = BuildSnapshotVersion(enabledJobs),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            EnabledJobCount = enabledJobs.Count,
            Jobs = jobs,
            Issues = issues
        });
    }

    /// <summary>
    /// Преобразует сохранённое задание в компактную модель выполнения.
    /// </summary>
    private static CalcExecutionJobDto ToExecutionJob(CalcJobDto job)
    {
        return new CalcExecutionJobDto
        {
            Id = job.Id,
            Revision = job.Revision,
            Name = job.Name,
            EquipmentName = job.EquipmentName,
            DefinitionCode = job.DefinitionCode,
            DefinitionVersion = job.DefinitionVersion,
            PeriodMs = job.PeriodMs,
            SortOrder = job.SortOrder,
            WriteEnabled = job.WriteEnabled,

            Inputs = job.Inputs
                .OrderBy(input => input.SortOrder)
                .ThenBy(input => input.Id)
                .Select(input => new CalcExecutionInputDto
                {
                    ParameterKey = input.ParameterKey,
                    SourceType = input.SourceType,
                    TagName = input.TagName,
                    ConstantValue = input.ConstantValue?.Clone(),
                    MaxAgeSeconds = input.MaxAgeSeconds,
                    SortOrder = input.SortOrder
                })
                .ToList(),

            Outputs = job.Outputs
                .OrderBy(output => output.SortOrder)
                .ThenBy(output => output.Id)
                .Select(output => new CalcExecutionOutputDto
                {
                    OutputKey = output.OutputKey,
                    TagName = output.TagName,
                    WriteEnabled = output.WriteEnabled,
                    Scale = output.Scale,
                    Offset = output.Offset,
                    SortOrder = output.SortOrder
                })
                .ToList()
        };
    }

    /// <summary>
    /// Создаёт компактную версию snapshot по Id и Revision
    /// всех enabled-заданий.
    ///
    /// Revision увеличивается при каждом поддерживаемом изменении задания.
    /// </summary>
    private static string BuildSnapshotVersion(IReadOnlyList<CalcJobDto> jobs)
    {
        var source = string.Join("|", jobs
            .OrderBy(job => job.Id)
            .Select(job => $"{job.Id}:{job.Revision}"));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Определяет автора изменения конфигурации.
    /// </summary>
    private static string ResolveActor(HttpContext context, IAppRuntimeContext runtime)
    {
        var windowsUser = context.User.Identity?.Name;

        return string.IsNullOrWhiteSpace(windowsUser)
            ? runtime.DeviceName
            : windowsUser.Trim();
    }

    private static IResult BadRequest(string code, string message) =>
        Results.BadRequest(new CalcApiErrorResponse { ErrorCode = code, ErrorMessage = message });

    private static IResult NotFound(string code, string message) =>
        Results.NotFound(new CalcApiErrorResponse { ErrorCode = code, ErrorMessage = message });

    private static IResult EditingDisabled() =>
        Results.Json(
            new CalcApiErrorResponse
            {
                ErrorCode = "calc.configuration-editing-disabled",
                ErrorMessage = "Calculation configuration editing is disabled."
            },
            statusCode: StatusCodes.Status403Forbidden);
}