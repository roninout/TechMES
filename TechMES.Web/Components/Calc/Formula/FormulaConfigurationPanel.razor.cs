using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Radzen;
using TechMES.Contracts.Calc;
using TechMES.Contracts.Equipment;
using TechMES.Contracts.Param;
using TechMES.Web.Clients;

namespace TechMES.Web.Components.Calc.Formula;

public partial class FormulaConfigurationPanel : IDisposable
{
    private const string DefinitionCode = "formula.expression";

    [Inject] private CalcApiClient CalcApi { get; set; } = default!;
    [Inject] private ParamApiClient ParamApi { get; set; } = default!;
    [Inject] private CalcProcessInputResolver InputResolver { get; set; } = default!;
    [Inject] private NotificationService Notifications { get; set; } = default!;
    [Inject] private DialogService Dialogs { get; set; } = default!;

    [Parameter] public CalcJobDto? Job { get; set; }
    [Parameter] public CalcJobStateDto? State { get; set; }
    [Parameter] public IReadOnlyList<EquipmentDto> EquipmentCatalog { get; set; } = [];
    [Parameter] public EventCallback<long> JobChanged { get; set; }
    [Parameter] public EventCallback<long> JobDeleted { get; set; }
    [Parameter] public EventCallback NewFormulaRequested { get; set; }
    [Parameter] public bool PageBusy { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private readonly List<VariableEditor> _variables = [];

    private CalcDefinitionDto? _definition;
    private CalcJobDto? _job;

    private bool _loadAttempted, _loaded, _disposed, _busy, _readingOutput;
    private bool _enabled, _allowWrites, _outputChecked;

    private string _name = "New formula", _description = "", _expression = "[a]", _outputTag = "";
    private string _error = "", _testMessage = "", _outputMessage = "", _liveError = "";
    private string _savedSignature = "", _savedExecutionSignature = "", _testedSignature = "";

    private int _periodMs = 5000;
    private double? _minimum, _maximum, _testResult, _outputValue, _checkedOutputValue;
    private DateTimeOffset _nextOutputRead;
    private DateTimeOffset? _outputReadAt;

    public bool IsBusy => PageBusy || _busy || (!_loaded && _error.Length == 0);
    public bool HasPendingChanges => _loaded && EditorSignature() != _savedSignature;

    private bool InputsReady => _variables.All(item => item.HasTag && item.Checked && item.Value.HasValue);
    private bool TestPassed => _testResult.HasValue && _testedSignature == ExecutionSignature();
    private bool CanEnableWrites => _enabled && InputsReady && TestPassed && _outputChecked && _minimum.HasValue && _maximum.HasValue && ConfigurationError.Length == 0;
    private bool CanKeepWrites => _job?.WriteEnabled == true && _enabled && ExecutionSignature() == _savedExecutionSignature;
    private bool CanSave => _definition is not null && ConfigurationError.Length == 0 && _variables.All(item => item.Source.Length == 0 || item.HasTag) && (!_enabled || _variables.All(item => item.HasTag)) && (!_allowWrites || CanEnableWrites || CanKeepWrites);

    private string SavedOutputTag => _job?.Outputs.FirstOrDefault(item => item.OutputKey == "result")?.TagName?.Trim() ?? "";
    private bool HasCurrentState => _job is not null && State?.JobId == _job.Id && State.ConfigurationRevision == _job.Revision;

    // Для сохранённого адреса показываем циклическое чтение того же выходного тега.
    // Для нового несохранённого адреса доступен только результат его проверки.
    private double? CurrentOutputValue => string.Equals(_outputTag.Trim(), SavedOutputTag, StringComparison.OrdinalIgnoreCase) ? _outputValue : _outputChecked ? _checkedOutputValue : null;

    private string ConfigurationError
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_name) || _periodMs <= 0)
                return "Name and a positive calculation period are required.";

            if (string.IsNullOrWhiteSpace(_expression))
                return "Expression is required.";

            var referenced = ReferencedVariables(_expression);

            if (!referenced.SetEquals(_variables.Select(item => item.Key)))
                return "Use every displayed variable in the expression. Add fields for additional variables: [a], [b], [c], ...";

            if (_minimum.HasValue != _maximum.HasValue)
                return "Enter both output limits or leave both empty.";

            if (_minimum.HasValue && (!double.IsFinite(_minimum.Value) || !double.IsFinite(_maximum!.Value) || _minimum.Value >= _maximum.Value))
                return "Output limits must be finite numbers and minimum must be less than maximum.";

            return "";
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!_loadAttempted)
        {
            _loadAttempted = true;

            try
            {
                var definitions = await CalcApi.GetDefinitionsAsync(_cts.Token);

                if (_disposed) return;

                _definition = definitions.FirstOrDefault(item => item.Code == DefinitionCode);

                if (_definition is null)
                    _error = "Formula definition is not available in Runtime.";
            }
            catch (OperationCanceledException) when (_disposed)
            {
                return;
            }
            catch (Exception exception)
            {
                _error = exception.Message;
            }
        }

        if (_definition is null || _disposed) return;

        if (!_loaded)
        {
            LoadEditor(Job);
        }
        else if (Job is not null && (_job is null || Job.Id != _job.Id || Job.Revision > _job.Revision))
        {
            // Polling значений не меняет редактор. Внешнее изменение конфигурации
            // при несохранённом вводе оставляет старую Revision для проверки конфликта.
            if (HasPendingChanges || _busy)
                _error = "The saved configuration has changed. Your edits are retained; refresh to load the new version.";
            else
                LoadEditor(Job);
        }

        if (!_busy && DateTimeOffset.UtcNow >= _nextOutputRead)
            await RefreshOutputValueAsync();
    }

    private void LoadEditor(CalcJobDto? job)
    {
        _job = job;
        _name = job?.Name ?? "New formula";
        _description = job?.Description ?? "";
        _periodMs = job?.PeriodMs ?? 5000;
        _enabled = job?.Enabled == true;
        _allowWrites = job?.WriteEnabled == true;
        _expression = ReadConstant("expression") is { ValueKind: JsonValueKind.String } expression ? expression.GetString() ?? "[a]" : "[a]";
        _minimum = ReadNumber(ReadConstant("outputMinimum"));
        _maximum = ReadNumber(ReadConstant("outputMaximum"));
        _outputTag = SavedOutputTag;
        _outputChecked = false;
        _outputMessage = _testMessage = _error = _liveError = _testedSignature = "";
        _testResult = _outputValue = null;
        _outputReadAt = null;
        _nextOutputRead = DateTimeOffset.MinValue;
        _variables.Clear();

        // Черновик может содержать формулу с [b], но ещё не иметь привязки b.
        // Число строк восстанавливаем и по формуле, и по сохранённым входам.
        var keys = ReferencedVariables(_expression);

        foreach (var input in job?.Inputs ?? [])
        {
            if (input.ParameterKey.Length == 1 && input.ParameterKey[0] is >= 'a' and <= 'z')
                keys.Add(input.ParameterKey);
        }

        var count = keys.Count == 0 ? 1 : keys.Max(key => key[0]) - 'a' + 1;

        for (var index = 0; index < count; index++)
        {
            var key = ((char)('a' + index)).ToString();
            var stored = job?.Inputs.FirstOrDefault(item => item.ParameterKey == key);
            var tag = stored?.TagName?.Trim() ?? "";
            var source = string.IsNullOrWhiteSpace(stored?.SourceReference) ? tag : stored.SourceReference.Trim();

            _variables.Add(new VariableEditor
            {
                Key = key,
                Source = source,
                Tag = tag,
                ResolvedSource = source,
                MaxAgeSeconds = stored?.MaxAgeSeconds
            });
        }

        _loaded = true;
        _savedExecutionSignature = ExecutionSignature();
        _savedSignature = EditorSignature();
    }

    private void ChangeSource(VariableEditor item, string? value)
    {
        var source = (value ?? "").Trim();

        if (item.Source == source) return;

        item.Source = source;
        item.Tag = item.ResolvedSource = item.Message = "";
        item.Checked = false;
        item.Value = null;
        InvalidateTest();
    }

    private void ChangeExpression(string? value)
    {
        if (_expression == (value ?? "")) return;

        _expression = value ?? "";
        InvalidateTest();
    }

    private void ChangeRange(double? value, bool minimum)
    {
        if (minimum) _minimum = value;
        else _maximum = value;

        InvalidateTest();
    }

    private void ChangeOutput(string? value)
    {
        var tag = (value ?? "").Trim();

        if (_outputTag == tag) return;

        _outputTag = tag;
        _outputChecked = false;
        _outputMessage = "";
        InvalidateTest();
    }

    private void ChangeEnabled(bool value)
    {
        _enabled = value;

        if (!value)
            _allowWrites = false;
    }

    private void ChangeVariableCount(bool add)
    {
        if (add && _variables.Count < 26)
            _variables.Add(new VariableEditor { Key = ((char)('a' + _variables.Count)).ToString() });
        else if (!add && _variables.Count > 1)
            _variables.RemoveAt(_variables.Count - 1);

        InvalidateTest();
    }

    private void InvalidateTest()
    {
        _testedSignature = _testMessage = "";
        _testResult = null;
        _allowWrites = false;
    }

    private async Task CheckInputCoreAsync(VariableEditor item)
    {
        var before = ExecutionSignature();
        var result = await InputResolver.ResolveAsync(item.Source, EquipmentCatalog, _cts.Token);

        item.Checked = result.Success && !string.IsNullOrWhiteSpace(result.ResolvedTagName) && result.CurrentValue.HasValue && double.IsFinite(result.CurrentValue.Value);
        item.Value = item.Checked ? result.CurrentValue : null;
        item.Message = item.Checked ? result.Resolution ?? "Numeric tag found." : result.Message ?? "Numeric tag was not found.";

        if (item.Checked)
        {
            item.Tag = result.ResolvedTagName!.Trim();
            item.ResolvedSource = item.Source;
        }

        // При временном отказе чтения сохраняем прежнюю привязку:
        // она нужна, чтобы отключить существующий Job даже без связи со SCADA.
        if (ExecutionSignature() != before)
            InvalidateTest();
    }

    private Task CheckAllAsync() => RunActionAsync(async () =>
    {
        // Общая кнопка проверяет входы и выход в рамках одной операции IsBusy.
        // Старый результат проверки выхода скрываем до начала запросов.
        _outputChecked = false;
        _outputMessage = "";
        _checkedOutputValue = null;

        await CheckAllCoreAsync();
        await CheckOutputCoreAsync();
    });

    private async Task CheckAllCoreAsync()
    {
        foreach (var item in _variables)
            await CheckInputCoreAsync(item);
    }

    private async Task CheckOutputCoreAsync()
    {
        _outputChecked = false;
        _outputMessage = "";
        _checkedOutputValue = null;

        var source = _outputTag.Trim();

        if (source.Length == 0)
        {
            _outputMessage = "Enter an output tag to check the output. Input checks are complete.";
            return;
        }

        var result = await ParamApi.CheckNumericTagAsync(new ParamTagCheckRequest { TagName = source, RequireTrend = true }, _cts.Token);

        _outputChecked = result.Found && result.TrendFound && result.CurrentValue.HasValue && double.IsFinite(result.CurrentValue.Value) && !string.IsNullOrWhiteSpace(result.TagName);

        if (!_outputChecked)
        {
            _outputMessage = result.Message ?? "Numeric tag with a trend reference was not resolved.";
            return;
        }

        var resolvedTag = result.TagName.Trim();

        // В Job должен попасть реальный Variable Tag, который затем получит TagWrite.
        // Изменение назначения записи требует повторного теста формулы.
        if (!string.Equals(_outputTag, resolvedTag, StringComparison.Ordinal))
        {
            _outputTag = resolvedTag;
            InvalidateTest();
        }

        // Значение проверки используется для нового, ещё не сохранённого адреса.
        // Сохранённый выходной тег отображается через циклическое чтение _outputValue.
        _checkedOutputValue = result.CurrentValue;
        _outputMessage = "Trend reference resolved.";
    }

    private Task TestAsync() => RunActionAsync(async () =>
    {
        _testedSignature = _testMessage = "";
        _testResult = null;

        await CheckAllCoreAsync();

        if (!InputsReady)
        {
            _testMessage = "Check the input sources. Some current values are unavailable.";
            return;
        }

        var parameters = new Dictionary<string, object?> { ["expression"] = _expression.Trim() };

        if (_minimum.HasValue && _maximum.HasValue)
        {
            parameters["outputMinimum"] = _minimum.Value;
            parameters["outputMaximum"] = _maximum.Value;
        }

        foreach (var item in _variables)
            parameters[item.Key] = item.Value!.Value;

        var result = await CalcApi.TestAsync(DefinitionCode, parameters, includeTrace: true, ct: _cts.Token);
        var output = result.Outputs.FirstOrDefault(item => item.Key == "result");

        if (!result.IsSuccess || output is null || !double.IsFinite(output.Value))
        {
            _testMessage = result.ErrorMessage ?? "The formula did not return a finite result.";
            return;
        }

        _testResult = output.Value;
        _testedSignature = ExecutionSignature();
    });

    private Task DeleteAsync() => RunActionAsync(async () =>
    {
        if (_job is null) return;

        var job = _job;
        var confirmed = await Dialogs.Confirm($"Delete formula job '{job.Name}'? Unsaved changes will also be discarded. The SCADA tag and its history will remain.", "Delete formula", new ConfirmOptions { OkButtonText = "Delete", CancelButtonText = "Cancel" });

        if (confirmed != true || _disposed) return;

        // При отказе API редактор и список сохраняются. Общий RunActionAsync покажет ошибку.
        await CalcApi.DeleteJobAsync(job.Id, _cts.Token);

        // Удалённый Id больше нельзя отправить через Save даже при ошибке обновления списка.
        LoadEditor(null);

        Notifications.Notify(NotificationSeverity.Success, "Formula", $"Job '{job.Name}' deleted.", 4000);
        await JobDeleted.InvokeAsync(job.Id);
    });

    private Task SaveAsync() => RunActionAsync(async () =>
    {
        if (!CanSave) return;

        var request = BuildRequest();
        var saved = _job is null ? await CalcApi.CreateJobAsync(request, _cts.Token) : await CalcApi.UpdateJobAsync(_job.Id, request, _cts.Token);

        LoadEditor(saved);
        Notifications.Notify(NotificationSeverity.Success, "Formula", $"Job '{saved.Name}' saved.", 4000);

        // Сначала запоминаем полученный Id, затем обновляем список страницы.
        // Ошибка обновления списка не должна приводить к повторному Create.
        await JobChanged.InvokeAsync(saved.Id);
    });

    private CalcJobSaveRequest BuildRequest()
    {
        var inputs = new List<CalcJobInputSaveDto> { Constant("expression", _expression.Trim(), 0) };

        if (_minimum.HasValue && _maximum.HasValue)
        {
            inputs.Add(Constant("outputMinimum", _minimum.Value, 1));
            inputs.Add(Constant("outputMaximum", _maximum.Value, 2));
        }

        for (var index = 0; index < _variables.Count; index++)
        {
            var item = _variables[index];

            if (item.HasTag)
            {
                inputs.Add(new CalcJobInputSaveDto
                {
                    ParameterKey = item.Key,
                    SourceType = CalcInputSourceTypeDto.Tag,
                    TagName = item.Tag,
                    SourceReference = item.Source,
                    MaxAgeSeconds = item.MaxAgeSeconds,
                    SortOrder = 100 + index
                });
            }
        }

        return new CalcJobSaveRequest
        {
            Name = _name.Trim(),
            Description = string.IsNullOrWhiteSpace(_description) ? null : _description.Trim(),
            EquipmentName = null,
            DefinitionCode = DefinitionCode,
            DefinitionVersion = _definition!.Version,
            Enabled = _enabled,
            WriteEnabled = _allowWrites,
            PeriodMs = _periodMs,
            ExpectedRevision = _job?.Revision,
            SortOrder = _job?.SortOrder ?? 0,
            Inputs = inputs,
            Outputs = [new CalcJobOutputSaveDto { OutputKey = "result", TagName = string.IsNullOrWhiteSpace(_outputTag) ? null : _outputTag.Trim(), WriteEnabled = _allowWrites, Scale = 1d, Offset = 0d }]
        };
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (IsBusy || _disposed) return;

        _busy = true;
        _error = "";

        try
        {
            await action();
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            _error = exception.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RefreshOutputValueAsync()
    {
        var tag = SavedOutputTag;

        if (_readingOutput || tag.Length == 0) return;

        _readingOutput = true;
        var jobId = _job!.Id;
        var revision = _job.Revision;

        try
        {
            // Тренд проверяется кнопкой Check и Runtime при сохранении.
            // Для live-значения требуется только числовое чтение сохранённого тега.
            var response = await ParamApi.CheckNumericTagAsync(new ParamTagCheckRequest { TagName = tag, RequireTrend = false }, _cts.Token);

            if (_disposed || _job?.Id != jobId || _job.Revision != revision) return;

            _outputValue = response.Found && response.CurrentValue.HasValue && double.IsFinite(response.CurrentValue.Value) ? response.CurrentValue : null;
            _liveError = _outputValue.HasValue ? "" : response.Message ?? "Output value is unavailable.";

            if (_outputValue.HasValue)
                _outputReadAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        catch (Exception exception)
        {
            if (!_disposed && _job?.Id == jobId && _job.Revision == revision)
            {
                _outputValue = null;
                _liveError = exception.Message;
            }
        }
        finally
        {
            _nextOutputRead = DateTimeOffset.UtcNow.AddSeconds(5);
            _readingOutput = false;
        }
    }

    private double? InputValue(VariableEditor item)
    {
        var stored = _job?.Inputs.FirstOrDefault(input => input.ParameterKey == item.Key);

        return HasCurrentState && item.HasTag && string.Equals(stored?.TagName, item.Tag, StringComparison.OrdinalIgnoreCase)
            ? RuntimeNumber(State?.LastInputs, item.Key) ?? item.Value
            : item.Value;
    }

    private double? RuntimeResult => HasCurrentState ? RuntimeNumber(State?.LastOutputs, "result") : null;

    private JsonElement? ReadConstant(string key) => _job?.Inputs.FirstOrDefault(item => item.ParameterKey == key && item.SourceType == CalcInputSourceTypeDto.Constant)?.ConstantValue;

    private static double? ReadNumber(JsonElement? value) => value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var result) && double.IsFinite(result) ? result : null;

    private static double? RuntimeNumber(JsonElement? json, string key) => json is { ValueKind: JsonValueKind.Object } root && root.TryGetProperty(key, out var value) ? ReadNumber(value) : null;

    private static string FormatNumber(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "—";

    private static string FormatTime(DateTimeOffset? value) => value?.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") ?? "—";

    private static HashSet<string> ReferencedVariables(string expression) => Regex.Matches(expression, @"\[\s*([a-zA-Z])\s*\]").Select(match => match.Groups[1].Value.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static CalcJobInputSaveDto Constant(string key, object value, int order) => new() { ParameterKey = key, SourceType = CalcInputSourceTypeDto.Constant, ConstantValue = JsonSerializer.SerializeToElement(value), SortOrder = order };

    // JSON исключает неоднозначность разделителей в произвольном тексте формулы.
    // Текущие значения и статусы Check в подпись редактирования не входят.
    private string ExecutionSignature() => JsonSerializer.Serialize(new
    {
        Expression = _expression.Trim(),
        Minimum = _minimum?.ToString("G17", CultureInfo.InvariantCulture),
        Maximum = _maximum?.ToString("G17", CultureInfo.InvariantCulture),
        Output = _outputTag.Trim(),
        Variables = _variables.Select(item => new { item.Key, Source = item.Source.Trim(), item.Tag, item.MaxAgeSeconds })
    });

    private string EditorSignature() => JsonSerializer.Serialize(new
    {
        Name = _name.Trim(),
        Description = _description.Trim(),
        Period = _periodMs,
        Enabled = _enabled,
        Writes = _allowWrites,
        Execution = ExecutionSignature()
    });

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed class VariableEditor
    {
        public string Key { get; init; } = "";
        public string Source { get; set; } = "";
        public string Tag { get; set; } = "";
        public string ResolvedSource { get; set; } = "";
        public string Message { get; set; } = "";
        public int? MaxAgeSeconds { get; init; }
        public double? Value { get; set; }
        public bool Checked { get; set; }

        public bool HasTag => Tag.Length > 0 && string.Equals(Source.Trim(), ResolvedSource, StringComparison.OrdinalIgnoreCase);
    }
}