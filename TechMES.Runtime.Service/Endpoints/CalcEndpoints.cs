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

        app.MapPost("/api/calc/service/heartbeat", ReceiveServiceHeartbeat);
        app.MapGet("/api/calc/service/status", GetServiceStatus);
        app.MapGet("/api/calc/service/health", GetServiceHealth);

        app.MapGet("/api/calc/states", GetStatesAsync);
        app.MapGet("/api/calc/jobs/{id:long}/state", GetJobStateAsync);
        app.MapPost("/api/calc/execution/results", SaveExecutionResultsAsync);

        app.MapGet("/api/calc/jobs", GetJobsAsync);
        app.MapGet("/api/calc/jobs/{id:long}", GetJobAsync);
        app.MapPost("/api/calc/jobs", CreateJobAsync);
        app.MapPut("/api/calc/jobs/{id:long}", UpdateJobAsync);
        app.MapDelete("/api/calc/jobs/{id:long}", DeleteJobAsync);

        return app;
    }

    /// <summary>
    /// Принимает heartbeat и возвращает текущее состояние ownership.
    ///
    /// Только один InstanceId одновременно получает IsLeaseOwner=true.
    /// </summary>
    private static IResult ReceiveServiceHeartbeat(CalcServiceHeartbeatRequest? request, CalcServiceHeartbeatRegistry registry)
    {
        var error = ValidateServiceHeartbeat(request);

        if (error is not null)
            return Results.BadRequest(error);

        return Results.Ok(registry.RecordAndAcquire(request!));
    }

    /// <summary>
    /// Возвращает текущее состояние Calc.Service.
    /// </summary>
    private static IResult GetServiceStatus(CalcServiceHeartbeatRegistry registry)
    {
        return Results.Ok(registry.GetStatus());
    }

    /// <summary>
    /// Возвращает HTTP health-state Calc.Service.
    ///
    /// Maintenance использует именно этот endpoint:
    /// 200 означает, что активный Calc.Service владеет действующим lease;
    /// 503 означает, что Calc.Service сейчас недоступен.
    ///
    /// Сам Calc.Service отдельный HTTP listener не открывает.
    /// </summary>
    private static IResult GetServiceHealth(CalcServiceHeartbeatRegistry registry)
    {
        var status = registry.GetStatus();

        return status.Availability == CalcServiceAvailabilityDto.Online
            ? Results.Ok(status)
            : Results.Json(status, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>
    /// Проверяет структуру heartbeat до сохранения в memory-state.
    /// </summary>
    private static CalcApiErrorResponse? ValidateServiceHeartbeat(CalcServiceHeartbeatRequest? request)
    {
        if (request is null)
            return ApiError("service.heartbeat-missing", "Calc Service heartbeat is required.");

        if (string.IsNullOrWhiteSpace(request.InstanceId))
            return ApiError("service.instance-empty", "Calc Service instance id is required.");

        if (request.InstanceId.Trim().Length > 200)
            return ApiError("service.instance-too-long", "Calc Service instance id cannot exceed 200 characters.");

        if (string.IsNullOrWhiteSpace(request.MachineName))
            return ApiError("service.machine-empty", "Calc Service machine name is required.");

        if (request.MachineName.Trim().Length > 200)
            return ApiError("service.machine-too-long", "Calc Service machine name cannot exceed 200 characters.");

        if (request.ProcessId <= 0)
            return ApiError("service.process-invalid", "Calc Service process id must be greater than zero.");

        if ((request.ServiceVersion?.Length ?? 0) > 100)
            return ApiError("service.version-too-long", "Calc Service version cannot exceed 100 characters.");

        if (request.StartedAtUtc == default)
            return ApiError("service.started-at-invalid", "Calc Service start time is required.");

        return null;
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
    private static async Task<IResult> CreateJobAsync(CalcJobSaveRequest? request, ICalcJobStore store, CalcJobValidator validator, CalcDependencyGraphValidator dependencyValidator, HttpContext httpContext, IAppRuntimeContext runtime, IOptions<CalcConfigurationOptions> options, CancellationToken ct)
    {
        if (!options.Value.EditingEnabled)
            return EditingDisabled();

        var validation = validator.Validate(request, isUpdate: false);

        if (!validation.IsValid)
            return BadRequest(validation.ErrorCode!, validation.ErrorMessage!);

        var storedJobs = await store.GetAllAsync(ct);
        var dependencyValidation = dependencyValidator.ValidateSave(null, request!, storedJobs);

        if (!dependencyValidation.IsValid)
            return BadRequest(dependencyValidation.ErrorCode!, dependencyValidation.ErrorMessage!);

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
    private static async Task<IResult> UpdateJobAsync(long id, CalcJobSaveRequest? request, ICalcJobStore store, CalcJobValidator validator, CalcDependencyGraphValidator dependencyValidator, HttpContext httpContext, IAppRuntimeContext runtime, IOptions<CalcConfigurationOptions> options, CancellationToken ct)
    {
        if (!options.Value.EditingEnabled)
            return EditingDisabled();

        var validation = validator.Validate(request, isUpdate: true);

        if (!validation.IsValid)
            return BadRequest(validation.ErrorCode!, validation.ErrorMessage!);

        var storedJobs = await store.GetAllAsync(ct);
        var dependencyValidation = dependencyValidator.ValidateSave(id, request!, storedJobs);

        if (!dependencyValidation.IsValid)
            return BadRequest(dependencyValidation.ErrorCode!, dependencyValidation.ErrorMessage!);

        try
        {
            var job = await store.UpdateAsync(id, request!, ResolveActor(httpContext, runtime), ct);

            return job is null
                ? NotFound("job.not-found", $"Calculation job {id} was not found.")
                : Results.Ok(job);
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
    /// Возвращает проверенный DAG enabled-заданий для Calc.Service.
    /// </summary>
    private static async Task<IResult> GetConfigurationSnapshotAsync(ICalcJobStore store, CalcJobValidator validator, CalcDependencyGraphValidator dependencyValidator, CancellationToken ct)
    {
        var allJobs = (await store.GetAllAsync(ct)).ToList();

        var enabledJobs = allJobs
            .Where(job => job.Enabled)
            .OrderBy(job => job.SortOrder)
            .ThenBy(job => job.Id)
            .ToList();

        var individuallyValid = new List<CalcJobDto>();
        var issues = new List<CalcConfigurationIssueDto>();

        foreach (var job in enabledJobs)
        {
            var validation = validator.ValidateStored(job);

            if (validation.IsValid)
            {
                individuallyValid.Add(job);
                continue;
            }

            issues.Add(new CalcConfigurationIssueDto
            {
                JobId = job.Id,
                JobName = job.Name,
                ErrorCode = validation.ErrorCode ?? "job.invalid",
                ErrorMessage = validation.ErrorMessage ?? "Calculation job configuration is invalid."
            });
        }

        var graphIssues = dependencyValidator.ValidateSnapshot(allJobs, individuallyValid);
        var jobs = new List<CalcExecutionJobDto>();

        foreach (var job in individuallyValid)
        {
            if (graphIssues.TryGetValue(job.Id, out var graphIssue))
            {
                issues.Add(new CalcConfigurationIssueDto
                {
                    JobId = job.Id,
                    JobName = job.Name,
                    ErrorCode = graphIssue.ErrorCode ?? "dependency.invalid",
                    ErrorMessage = graphIssue.ErrorMessage ?? "Calculation dependency is invalid."
                });

                continue;
            }

            jobs.Add(ToExecutionJob(job));
        }

        return Results.Ok(new CalcConfigurationSnapshotDto
        {
            Version = BuildSnapshotVersion(enabledJobs, jobs, issues),
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            EnabledJobCount = enabledJobs.Count,
            Jobs = jobs,
            Issues = issues.OrderBy(issue => issue.JobId).ToList()
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
                    SourceJobId = input.SourceJobId,
                    SourceOutputKey = input.SourceOutputKey,
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
    /// Формирует версию из Revision заданий и результата их проверки.
    ///
    /// Благодаря этому Calc.Service увидит изменение snapshot не только
    /// при редактировании job, но и когда Runtime начал принимать
    /// или отклонять задание после обновления каталога/валидатора.
    /// </summary>
    private static string BuildSnapshotVersion(IReadOnlyList<CalcJobDto> enabledJobs, IReadOnlyList<CalcExecutionJobDto> acceptedJobs, IReadOnlyList<CalcConfigurationIssueDto> issues)
    {
        var source = new StringBuilder();

        foreach (var job in enabledJobs.OrderBy(job => job.Id))
            source.Append("job:").Append(job.Id).Append(':').Append(job.Revision).Append(';');

        foreach (var job in acceptedJobs.OrderBy(job => job.Id))
            source.Append("accepted:").Append(job.Id).Append(':').Append(job.DefinitionCode).Append(':').Append(job.DefinitionVersion).Append(';');

        foreach (var issue in issues.OrderBy(issue => issue.JobId))
            source.Append("issue:").Append(issue.JobId).Append(':').Append(issue.ErrorCode).Append(';');

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString()));
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

    /// <summary>
    /// Возвращает состояния всех заданий.
    /// </summary>
    private static async Task<IResult> GetStatesAsync(ICalcJobStateStore store, CancellationToken ct)
    {
        var states = await store.GetAllAsync(ct);
        return Results.Ok(new CalcJobStatesResponse { Items = states.ToList() });
    }

    /// <summary>
    /// Возвращает текущее состояние одного задания.
    /// </summary>
    private static async Task<IResult> GetJobStateAsync(long id, ICalcJobStateStore store, CancellationToken ct)
    {
        var state = await store.GetAsync(id, ct);

        return state is null
            ? NotFound("state.not-found", $"Calculation state for job {id} was not found.")
            : Results.Ok(state);
    }

    /// <summary>
    /// Принимает пакет результатов только от текущего владельца lease.
    ///
    /// Это первый fencing barrier. Позже такая же проверка будет
    /// обязательна перед контролируемой записью в SCADA.
    /// </summary>
    private static async Task<IResult> SaveExecutionResultsAsync(CalcExecutionResultBatchRequest? request, ICalcJobStateStore store, CalcServiceHeartbeatRegistry heartbeatRegistry, CancellationToken ct)
    {
        var error = ValidateExecutionResults(request);

        if (error is not null)
            return Results.BadRequest(error);

        if (!heartbeatRegistry.IsLeaseOwner(request!.ServiceInstanceId, request.LeaseEpoch, request.LeaseToken))
        {
            return Results.Conflict(ApiError(
                "service.lease-not-owned",
                "The calculation result was rejected because this Calc Service instance does not own the current execution lease."));
        }

        return Results.Ok(await store.SaveResultsAsync(request, ct));
    }

    /// <summary>
    /// Проверяет структуру пакета до обращения к PostgreSQL.
    /// </summary>
    private static CalcApiErrorResponse? ValidateExecutionResults(CalcExecutionResultBatchRequest? request)
    {
        if (request is null)
            return ApiError("request.missing", "Calculation execution result request is required.");

        if (string.IsNullOrWhiteSpace(request.ServiceInstanceId))
            return ApiError("service.instance-empty", "Calc Service instance id is required.");

        if (request.ServiceInstanceId.Trim().Length > 200)
            return ApiError("service.instance-too-long", "Calc Service instance id cannot exceed 200 characters.");

        if (string.IsNullOrWhiteSpace(request.LeaseEpoch))
            return ApiError("service.lease-epoch-empty", "Calc Service lease epoch is required.");

        if (request.LeaseEpoch.Trim().Length > 100)
            return ApiError("service.lease-epoch-too-long", "Calc Service lease epoch cannot exceed 100 characters.");

        if (request.LeaseToken <= 0)
            return ApiError("service.lease-token-invalid", "Calc Service lease token must be greater than zero.");

        if (request.Items is null || request.Items.Count == 0)
            return ApiError("result.items-empty", "At least one calculation result is required.");

        if (request.Items.Count > 500)
            return ApiError("result.items-too-many", "A maximum of 500 calculation results can be submitted at once.");

        foreach (var item in request.Items)
        {
            var itemError = ValidateExecutionResult(item);

            if (itemError is not null)
                return itemError;
        }

        return null;
    }

    /// <summary>
    /// Проверяет один результат выполнения.
    /// </summary>
    private static CalcApiErrorResponse? ValidateExecutionResult(CalcExecutionResultItemDto? item)
    {
        if (item is null)
            return ApiError("result.item-null", "Calculation result item cannot be null.");

        if (item.JobId <= 0)
            return ApiError("result.job-id-invalid", "Calculation result JobId must be greater than zero.");

        if (item.ConfigurationRevision <= 0)
            return ApiError("result.revision-invalid", "Calculation result revision must be greater than zero.");

        if (item.ServiceCycleNumber <= 0)
            return ApiError("result.cycle-invalid", "Calculation result service cycle must be greater than zero.");

        if (string.IsNullOrWhiteSpace(item.DefinitionCode)
            || string.IsNullOrWhiteSpace(item.DefinitionVersion))
        {
            return ApiError("result.definition-empty", "Calculation definition code and version are required.");
        }

        if (item.Status is not CalcJobStateStatusDto.Success
            and not CalcJobStateStatusDto.Skipped
            and not CalcJobStateStatusDto.Error)
        {
            return ApiError("result.status-invalid", "Execution result status must be Success, Skipped or Error.");
        }

        if (item.StartedAtUtc == default || item.CompletedAtUtc == default
            || item.CompletedAtUtc < item.StartedAtUtc)
        {
            return ApiError("result.time-invalid", "Calculation result contains an invalid start or completion time.");
        }

        if (item.DurationMs < 0)
            return ApiError("result.duration-invalid", "Calculation result duration cannot be negative.");

        if (item.Inputs.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ApiError("result.inputs-invalid", "Calculation result Inputs must be a JSON object.");

        if (item.Outputs.ValueKind != System.Text.Json.JsonValueKind.Object)
            return ApiError("result.outputs-invalid", "Calculation result Outputs must be a JSON object.");

        foreach (var output in item.Outputs.EnumerateObject())
        {
            if (output.Value.ValueKind != System.Text.Json.JsonValueKind.Number
                || !output.Value.TryGetDouble(out var number)
                || !double.IsFinite(number))
            {
                return ApiError(
                    "result.output-invalid",
                    $"Calculation output '{output.Name}' must be a finite number.");
            }
        }

        if (item.Status is CalcJobStateStatusDto.Skipped or CalcJobStateStatusDto.Error
            && string.IsNullOrWhiteSpace(item.ReasonCode))
        {
            return ApiError("result.reason-empty", "Skipped and Error results require ReasonCode.");
        }

        if ((item.ReasonCode?.Length ?? 0) > 200
            || (item.ReasonMessage?.Length ?? 0) > 4000)
        {
            return ApiError("result.reason-too-long", "Calculation result reason is too long.");
        }

        return null;
    }

    private static CalcApiErrorResponse ApiError(string code, string message)
    {
        return new CalcApiErrorResponse
        {
            ErrorCode = code,
            ErrorMessage = message
        };
    }
}