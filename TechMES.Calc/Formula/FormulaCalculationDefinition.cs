using System.Globalization;
using TechMES.Calc.Abstractions;
using TechMES.Calc.Exceptions;
using TechMES.Calc.Parameters;
using TechMES.Calc.Results;

namespace TechMES.Calc.Formula;

/// <summary>
/// Универсальный Job-расчёт по пользовательской математической формуле.
///
/// a..z являются обычными ProcessInput и поэтому получают значения через
/// существующий Calc runtime: SCADA tag, константа или выход другого расчёта.
/// </summary>
public sealed class FormulaCalculationDefinition : CalculationDefinitionBase
{
    public const string DefinitionCode = "formula.expression";
    public const string ExpressionKey = "expression";
    public const string OutputMinimumKey = "outputMinimum";
    public const string OutputMaximumKey = "outputMaximum";
    public const string ResultOutputKey = "result";

    private static readonly IReadOnlyList<CalculationParameterDefinition> ParameterDefinitions = CreateParameters();

    private static readonly IReadOnlyList<CalculationOutputDefinition> OutputDefinitions =
    [
        new(
            ResultOutputKey,
            "Result",
            Unit: null,
            Decimals: 6,
            Order: 0,
            Description: "Result of the configured mathematical expression.")
    ];

    public override string Code => DefinitionCode;

    public override string Name => "Formula";

    public override string Category => "Formula";

    public override string Version => "1";

    public override IReadOnlyList<CalculationParameterDefinition> Parameters => ParameterDefinitions;

    public override IReadOnlyList<CalculationOutputDefinition> Outputs => OutputDefinitions;

    protected override CalculationResult CalculateCore(CalculationParameterSet parameters, bool includeTrace)
    {
        var formula = parameters.GetRequiredString(ExpressionKey);
        var variableValues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var letter = 'a'; letter <= 'z'; letter++)
        {
            var key = letter.ToString();

            if (parameters.TryGetValue(key, out var rawValue) && rawValue is not null)
                variableValues[key] = parameters.GetRequiredDouble(key);
        }

        var result = FormulaExpressionEngine.Evaluate(formula, variableValues);
        ValidateOutputRange(parameters, result);

        var trace = includeTrace
            ? CreateTrace(formula, variableValues, result)
            : Array.Empty<CalculationTraceItem>();

        return CalculationResult.Success(
            [new CalculationOutput(ResultOutputKey, "Result", result)],
            trace: trace);
    }

    private static IReadOnlyList<CalculationParameterDefinition> CreateParameters()
    {
        var parameters = new List<CalculationParameterDefinition>
        {
            new(
                ExpressionKey,
                "Expression",
                CalculationParameterType.Text,
                IsRequired: true,
                Order: 0,
                Description: "Mathematical expression. Process variables must be written as [a]..[z]."),

            new(
                OutputMinimumKey,
                "Output minimum",
                CalculationParameterType.Number,
                IsRequired: false,
                Order: 1,
                Description: "Optional lower safety boundary for the calculated result."),

            new(
                OutputMaximumKey,
                "Output maximum",
                CalculationParameterType.Number,
                IsRequired: false,
                Order: 2,
                Description: "Optional upper safety boundary for the calculated result.")
        };

        for (var letter = 'a'; letter <= 'z'; letter++)
        {
            var key = letter.ToString();

            parameters.Add(new CalculationParameterDefinition(
                key,
                key,
                CalculationParameterType.Number,
                IsRequired: false,
                Decimals: 6,
                Order: 100 + letter - 'a',
                Description: $"Formula process variable [{key}].",
                Role: CalculationParameterRole.ProcessInput));
        }

        return parameters;
    }

    private static void ValidateOutputRange(CalculationParameterSet parameters, double result)
    {
        var hasMinimum = parameters.TryGetValue(OutputMinimumKey, out var rawMinimum) && rawMinimum is not null;
        var hasMaximum = parameters.TryGetValue(OutputMaximumKey, out var rawMaximum) && rawMaximum is not null;

        if (hasMinimum != hasMaximum)
        {
            throw new CalculationException(
                "formula.output-range-incomplete",
                "Formula output minimum and maximum must be configured together.");
        }

        if (!hasMinimum)
            return;

        var minimum = parameters.GetRequiredDouble(OutputMinimumKey);
        var maximum = parameters.GetRequiredDouble(OutputMaximumKey);

        if (minimum >= maximum)
        {
            throw new CalculationException(
                "formula.output-range-invalid",
                "Formula output minimum must be less than output maximum.");
        }

        if (result < minimum || result > maximum)
        {
            throw new CalculationException(
                "formula.output-out-of-range",
                $"Formula result {result.ToString("G17", CultureInfo.InvariantCulture)} is outside the allowed range {minimum.ToString("G17", CultureInfo.InvariantCulture)}..{maximum.ToString("G17", CultureInfo.InvariantCulture)}.");
        }
    }

    private static IReadOnlyList<CalculationTraceItem> CreateTrace(string formula, IReadOnlyDictionary<string, double> variables, double result)
    {
        var trace = new List<CalculationTraceItem>
        {
            new(ExpressionKey, "Expression", formula)
        };

        foreach (var variable in variables.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            trace.Add(new CalculationTraceItem(
                variable.Key,
                $"Variable {variable.Key}",
                variable.Value.ToString("G17", CultureInfo.InvariantCulture)));
        }

        trace.Add(new CalculationTraceItem(
            ResultOutputKey,
            "Result",
            result.ToString("G17", CultureInfo.InvariantCulture)));

        return trace;
    }
}