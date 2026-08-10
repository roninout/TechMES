using TechMES.Calc.Abstractions;
using TechMES.Contracts.Calc;

namespace TechMES.Runtime.Service.Calc;

/// <summary>
/// Проверяет связи Job -> Job.
///
/// Отвечает за существование source Job/output, enabled-состояние,
/// self-reference и циклические зависимости.
/// </summary>
internal sealed class CalcDependencyGraphValidator(CalculationCatalog catalog)
{
    /// <summary>
    /// Проверяет новый или изменяемый Job перед сохранением.
    /// </summary>
    public CalcJobValidationResult ValidateSave(long? jobId, CalcJobSaveRequest request, IReadOnlyList<CalcJobDto> storedJobs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(storedJobs);

        if (!catalog.TryGet(request.DefinitionCode, out var targetDefinition) || targetDefinition is null)
            return Invalid("definition.not-found", $"Calculation definition '{request.DefinitionCode}' was not found.");

        var candidateId = jobId ?? -1L;
        var jobsById = storedJobs
            .Where(job => !jobId.HasValue || job.Id != jobId.Value)
            .ToDictionary(job => job.Id);

        foreach (var input in request.Inputs.Where(input => input.SourceType == CalcInputSourceTypeDto.CalculationOutput))
        {
            var sourceJobId = input.SourceJobId!.Value;

            if (jobId.HasValue && sourceJobId == jobId.Value)
                return Invalid("dependency.self-reference", "Calculation job cannot depend on its own output.");

            if (!jobsById.TryGetValue(sourceJobId, out var sourceJob))
                return Invalid("dependency.source-not-found", $"Source calculation job {sourceJobId} was not found.");

            if (request.Enabled && !sourceJob.Enabled)
                return Invalid("dependency.source-disabled", $"Source calculation job '{sourceJob.Name}' must be enabled before this job can be enabled.");

            var outputValidation = ValidateSourceOutput(sourceJob, input.SourceOutputKey!);

            if (!outputValidation.IsValid)
                return outputValidation;
        }

        var edges = storedJobs
            .Where(job => !jobId.HasValue || job.Id != jobId.Value)
            .ToDictionary(job => job.Id, GetDependencies);

        edges[candidateId] = GetDependencies(request.Inputs);

        if (IsInCycle(candidateId, edges))
            return Invalid("dependency.cycle", "Calculation dependency creates a circular Job -> Job reference.");

        return CalcJobValidationResult.Success();
    }

    /// <summary>
    /// Проверяет весь enabled snapshot.
    ///
    /// Возвращает только дополнительные graph-ошибки.
    /// Базовые ошибки алгоритмов уже проверены CalcJobValidator.
    /// </summary>
    public IReadOnlyDictionary<long, CalcJobValidationResult> ValidateSnapshot(IReadOnlyList<CalcJobDto> allJobs, IReadOnlyList<CalcJobDto> individuallyValidEnabledJobs)
    {
        var issues = new Dictionary<long, CalcJobValidationResult>();
        var allById = allJobs.ToDictionary(job => job.Id);
        var validIds = individuallyValidEnabledJobs.Select(job => job.Id).ToHashSet();

        foreach (var job in individuallyValidEnabledJobs)
        {
            foreach (var input in job.Inputs.Where(input => input.SourceType == CalcInputSourceTypeDto.CalculationOutput))
            {
                if (!input.SourceJobId.HasValue || input.SourceJobId.Value == job.Id)
                {
                    issues[job.Id] = Invalid("dependency.self-reference", $"Calculation job '{job.Name}' contains an invalid self-reference.");
                    break;
                }

                if (!allById.TryGetValue(input.SourceJobId.Value, out var sourceJob))
                {
                    issues[job.Id] = Invalid("dependency.source-not-found", $"Source calculation job {input.SourceJobId.Value} was not found.");
                    break;
                }

                if (!sourceJob.Enabled)
                {
                    issues[job.Id] = Invalid("dependency.source-disabled", $"Source calculation job '{sourceJob.Name}' is disabled.");
                    break;
                }

                if (!validIds.Contains(sourceJob.Id))
                {
                    issues[job.Id] = Invalid("dependency.source-invalid", $"Source calculation job '{sourceJob.Name}' has invalid configuration.");
                    break;
                }

                var outputValidation = ValidateSourceOutput(sourceJob, input.SourceOutputKey ?? "");

                if (!outputValidation.IsValid)
                {
                    issues[job.Id] = outputValidation;
                    break;
                }
            }
        }

        /*
         * Циклы ищем только среди непосредственно корректных enabled Jobs.
         */
        var cycleCandidates = individuallyValidEnabledJobs
            .Where(job => !issues.ContainsKey(job.Id))
            .ToList();

        var cycleCandidateIds = cycleCandidates.Select(job => job.Id).ToHashSet();

        var edges = cycleCandidates.ToDictionary(
            job => job.Id,
            job => GetDependencies(job).Where(cycleCandidateIds.Contains).ToHashSet());

        foreach (var job in cycleCandidates)
        {
            if (IsInCycle(job.Id, edges))
                issues[job.Id] = Invalid("dependency.cycle", $"Calculation job '{job.Name}' participates in a circular dependency.");
        }

        /*
         * Если source Job исключён из snapshot из-за graph-ошибки,
         * все его потребители также должны быть исключены.
         */
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var job in individuallyValidEnabledJobs.Where(job => !issues.ContainsKey(job.Id)))
            {
                var invalidSourceId = GetDependencies(job).FirstOrDefault(issues.ContainsKey);

                if (invalidSourceId <= 0)
                    continue;

                var sourceName = allById.TryGetValue(invalidSourceId, out var sourceJob)
                    ? sourceJob.Name
                    : invalidSourceId.ToString();

                issues[job.Id] = Invalid(
                    "dependency.source-invalid",
                    $"Source calculation job '{sourceName}' is excluded from the active calculation graph.");

                changed = true;
            }
        }

        return issues;
    }

    private CalcJobValidationResult ValidateSourceOutput(CalcJobDto sourceJob, string outputKey)
    {
        if (!catalog.TryGet(sourceJob.DefinitionCode, out var definition) || definition is null)
            return Invalid("dependency.source-definition-not-found", $"Definition '{sourceJob.DefinitionCode}' of source job '{sourceJob.Name}' was not found.");

        if (!string.Equals(definition.Version, sourceJob.DefinitionVersion, StringComparison.Ordinal))
            return Invalid("dependency.source-version-mismatch", $"Source job '{sourceJob.Name}' uses unsupported definition version '{sourceJob.DefinitionVersion}'.");

        if (!definition.Outputs.Any(output => string.Equals(output.Key, outputKey.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Invalid("dependency.output-not-found", $"Output '{outputKey}' was not found in source job '{sourceJob.Name}'.");

        return CalcJobValidationResult.Success();
    }

    private static HashSet<long> GetDependencies(CalcJobDto job)
    {
        return job.Inputs
            .Where(input => input.SourceType == CalcInputSourceTypeDto.CalculationOutput && input.SourceJobId.HasValue)
            .Select(input => input.SourceJobId!.Value)
            .ToHashSet();
    }

    private static HashSet<long> GetDependencies(IEnumerable<CalcJobInputSaveDto> inputs)
    {
        return inputs
            .Where(input => input.SourceType == CalcInputSourceTypeDto.CalculationOutput && input.SourceJobId.HasValue)
            .Select(input => input.SourceJobId!.Value)
            .ToHashSet();
    }

    /// <summary>
    /// Проверяет, существует ли путь из start обратно в start.
    /// </summary>
    private static bool IsInCycle(long start, IReadOnlyDictionary<long, HashSet<long>> edges)
    {
        if (!edges.TryGetValue(start, out var first))
            return false;

        var stack = new Stack<long>(first);
        var visited = new HashSet<long>();

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current == start)
                return true;

            if (!visited.Add(current) || !edges.TryGetValue(current, out var next))
                continue;

            foreach (var dependency in next)
                stack.Push(dependency);
        }

        return false;
    }

    private static CalcJobValidationResult Invalid(string code, string message)
    {
        return CalcJobValidationResult.Failure(code, message);
    }
}