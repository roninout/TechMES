using System.Globalization;
using System.Text;
using NCalc;
using NCalc.Handlers;
using TechMES.Calc.Exceptions;

namespace TechMES.Calc.Formula;

/// <summary>
/// Проверяет и вычисляет пользовательскую математическую формулу.
///
/// NCalc используется только как математический движок. До передачи выражения
/// в NCalc выполняется собственная строгая проверка синтаксиса, поэтому формула
/// не может обращаться к типам, методам, строкам или произвольным параметрам.
/// </summary>
public static class FormulaExpressionEngine
{
    public const int MaximumExpressionLength = 2000;

    private static readonly IReadOnlyDictionary<string, string> AllowedFunctions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["abs"] = "Abs",
            ["min"] = "Min",
            ["max"] = "Max",
            ["round"] = "FormulaRound",
            ["floor"] = "Floor",
            ["ceil"] = "Ceiling",
            ["sqrt"] = "Sqrt",
            ["pow"] = "Pow",
            ["exp"] = "Exp",
            ["ln"] = "Ln",
            ["log10"] = "Log10",
            ["logn"] = "Log",
            ["sin"] = "Sin",
            ["cos"] = "Cos",
            ["tan"] = "Tan",
            ["asin"] = "Asin",
            ["acos"] = "Acos",
            ["atan"] = "Atan",
            ["atan2"] = "Atan2",
            ["clamp"] = "Clamp",
            ["if"] = "if"
        };

    private static readonly HashSet<string> AllowedKeywords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "true",
            "false",
            "and",
            "or",
            "not"
        };

    /// <summary>
    /// Возвращает стабильный список переменных a..z, фактически используемых формулой.
    /// Если выражение недопустимо, выбрасывает CalculationException.
    /// </summary>
    public static IReadOnlyList<string> Validate(string formula)
    {
        var parsed = ParseAndNormalize(formula);
        ValidateWithNCalc(parsed.NormalizedExpression, parsed.ReferencedVariables);
        return parsed.ReferencedVariables;
    }

    /// <summary>
    /// Вычисляет формулу по переданным значениям переменных a..z.
    /// </summary>
    public static double Evaluate(string formula, IReadOnlyDictionary<string, double> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);

        var parsed = ParseAndNormalize(formula);
        var normalizedVariables = NormalizeVariables(variables);

        foreach (var variable in parsed.ReferencedVariables)
        {
            if (!normalizedVariables.ContainsKey(variable))
            {
                throw new CalculationException(
                    "formula.variable-missing",
                    $"Formula variable '{variable}' does not have a configured value.");
            }
        }

        try
        {
            var expression = CreateExpression(parsed.NormalizedExpression);

            foreach (var variable in normalizedVariables)
                expression.Parameters[variable.Key] = variable.Value;

            var rawResult = expression.Evaluate();

            if (rawResult is bool)
            {
                throw new CalculationException(
                    "formula.result-not-numeric",
                    "Formula result must be numeric, but the expression returned a Boolean value.");
            }

            var result = Convert.ToDouble(rawResult, CultureInfo.InvariantCulture);

            if (!double.IsFinite(result))
            {
                throw new CalculationException(
                    "formula.result-not-finite",
                    "Formula result must be a finite number.");
            }

            return result;
        }
        catch (CalculationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculationException(
                "formula.evaluation-failed",
                $"Formula evaluation failed: {exception.Message}");
        }
    }

    private static Expression CreateExpression(string normalizedExpression)
    {
        var configuration = ExpressionConfiguration.FromOptions(ExpressionOptions.IgnoreCaseAtBuiltInFunctions | ExpressionOptions.OverflowProtection);
        var expression = new Expression(normalizedExpression, configuration, cultureInfo: CultureInfo.InvariantCulture);

        // Константы получают отдельные внутренние имена, чтобы переменная [e]
        // не могла подменить число Эйлера в выражениях вроде [e] + ln(e).
        expression.Parameters["__constant_pi"] = Math.PI;
        expression.Parameters["__constant_e"] = Math.E;
        expression.Functions["Clamp"] = EvaluateClamp;
        expression.Functions["FormulaRound"] = EvaluateRound;

        return expression;
    }

    private static object EvaluateRound(FunctionData arguments)
    {
        if (arguments.Count is < 1 or > 2)
        {
            throw new CalculationException(
                "formula.function-arguments-invalid",
                "round() takes one or two arguments: round(value) or round(value, digits).");
        }

        var value = Convert.ToDouble(arguments.Evaluate(0), CultureInfo.InvariantCulture);

        if (!double.IsFinite(value))
        {
            throw new CalculationException(
                "formula.function-argument-not-finite",
                "round() value must be a finite number.");
        }

        if (arguments.Count == 1)
            return Math.Round(value, MidpointRounding.AwayFromZero);

        var rawDigits = Convert.ToDouble(arguments.Evaluate(1), CultureInfo.InvariantCulture);

        if (!double.IsFinite(rawDigits) || rawDigits != Math.Truncate(rawDigits) || rawDigits is < 0 or > 15)
        {
            throw new CalculationException(
                "formula.function-arguments-invalid",
                "round() digits must be an integer from 0 through 15.");
        }

        return Math.Round(value, (int)rawDigits, MidpointRounding.AwayFromZero);
    }

    private static object EvaluateClamp(FunctionData arguments)
    {
        if (arguments.Count != 3)
        {
            throw new CalculationException(
                "formula.function-arguments-invalid",
                "clamp() takes exactly three arguments: clamp(value, minimum, maximum).");
        }

        var value = Convert.ToDouble(arguments.Evaluate(0), CultureInfo.InvariantCulture);
        var minimum = Convert.ToDouble(arguments.Evaluate(1), CultureInfo.InvariantCulture);
        var maximum = Convert.ToDouble(arguments.Evaluate(2), CultureInfo.InvariantCulture);

        if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            throw new CalculationException(
                "formula.function-argument-not-finite",
                "clamp() arguments must be finite numbers.");
        }

        if (minimum > maximum)
        {
            throw new CalculationException(
                "formula.function-range-invalid",
                "clamp() minimum cannot be greater than maximum.");
        }

        return Math.Clamp(value, minimum, maximum);
    }

    private static void ValidateWithNCalc(string normalizedExpression, IReadOnlyList<string> referencedVariables)
    {
        try
        {
            var expression = CreateExpression(normalizedExpression);

            foreach (var variable in referencedVariables)
                expression.Parameters[variable] = 1d;

            if (expression.HasErrors())
                throw expression.Error ?? new InvalidOperationException("Formula parser did not return an error description.");
        }
        catch (CalculationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new CalculationException(
                "formula.syntax-invalid",
                $"Formula is invalid: {exception.Message}");
        }
    }

    private static IReadOnlyDictionary<string, double> NormalizeVariables(IReadOnlyDictionary<string, double> variables)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            var key = variable.Key?.Trim().ToLowerInvariant();

            if (!IsVariableName(key))
            {
                throw new CalculationException(
                    "formula.variable-name-invalid",
                    $"Formula variable '{variable.Key}' is invalid. Only names a..z are allowed.");
            }

            if (!double.IsFinite(variable.Value))
            {
                throw new CalculationException(
                    "formula.variable-not-finite",
                    $"Formula variable '{key}' must be a finite number.");
            }

            if (!result.TryAdd(key!, variable.Value))
            {
                throw new CalculationException(
                    "formula.variable-duplicate",
                    $"Formula variable '{key}' is specified more than once.");
            }
        }

        return result;
    }

    private static ParsedFormula ParseAndNormalize(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new CalculationException(
                "formula.expression-empty",
                "Formula expression is required.");
        }

        var trimmed = formula.Trim();

        if (trimmed.Length > MaximumExpressionLength)
        {
            throw new CalculationException(
                "formula.expression-too-long",
                $"Formula expression cannot contain more than {MaximumExpressionLength} characters.");
        }

        var normalized = new StringBuilder(trimmed.Length);
        var referencedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;

        while (index < trimmed.Length)
        {
            var current = trimmed[index];

            if (char.IsWhiteSpace(current))
            {
                normalized.Append(current);
                index++;
                continue;
            }

            if (char.IsDigit(current) || current == '.')
            {
                index = CopyNumber(trimmed, index, normalized);
                continue;
            }

            if (current == '[')
            {
                var closingIndex = trimmed.IndexOf(']', index + 1);

                if (closingIndex < 0)
                {
                    throw SyntaxError(index, "Formula variable is missing a closing ']'.");
                }

                var variable = trimmed[(index + 1)..closingIndex].Trim().ToLowerInvariant();

                if (!IsVariableName(variable))
                {
                    throw SyntaxError(index, $"Formula variable '{variable}' is invalid. Only [a]..[z] are allowed.");
                }

                referencedVariables.Add(variable);
                normalized.Append('[').Append(variable).Append(']');
                index = closingIndex + 1;
                continue;
            }

            if (char.IsLetter(current))
            {
                index = CopyIdentifier(trimmed, index, normalized);
                continue;
            }

            if (IsSingleCharacterToken(current))
            {
                normalized.Append(current);
                index++;
                continue;
            }

            if ((current == '&' || current == '|') && index + 1 < trimmed.Length && trimmed[index + 1] == current)
            {
                normalized.Append(current).Append(current);
                index += 2;
                continue;
            }

            if ((current == '>' || current == '<' || current == '=' || current == '!') && index + 1 < trimmed.Length && trimmed[index + 1] == '=')
            {
                normalized.Append(current).Append('=');
                index += 2;
                continue;
            }

            if (current is '>' or '<' or '=')
            {
                normalized.Append(current);
                index++;
                continue;
            }

            throw SyntaxError(index, $"Character '{current}' is not allowed in a formula.");
        }

        return new ParsedFormula(
            normalized.ToString(),
            referencedVariables.OrderBy(variable => variable, StringComparer.Ordinal).ToArray());
    }

    private static int CopyIdentifier(string formula, int startIndex, StringBuilder normalized)
    {
        var index = startIndex + 1;

        while (index < formula.Length && char.IsLetterOrDigit(formula[index]))
            index++;

        var identifier = formula[startIndex..index];
        var nextNonWhitespace = index;

        while (nextNonWhitespace < formula.Length && char.IsWhiteSpace(formula[nextNonWhitespace]))
            nextNonWhitespace++;

        if (nextNonWhitespace < formula.Length && formula[nextNonWhitespace] == '(')
        {
            if (!AllowedFunctions.TryGetValue(identifier, out var canonicalFunction))
                throw SyntaxError(startIndex, $"Function '{identifier}' is not allowed.");

            normalized.Append(canonicalFunction);
            return index;
        }

        if (string.Equals(identifier, "pi", StringComparison.OrdinalIgnoreCase) || string.Equals(identifier, "e", StringComparison.OrdinalIgnoreCase))
        {
            normalized.Append("[__constant_").Append(identifier.ToLowerInvariant()).Append(']');
            return index;
        }

        if (AllowedKeywords.Contains(identifier))
        {
            normalized.Append(identifier.ToLowerInvariant());
            return index;
        }

        throw SyntaxError(startIndex, $"Identifier '{identifier}' is not allowed. Variables must be written as [a]..[z].");
    }

    private static int CopyNumber(string formula, int startIndex, StringBuilder normalized)
    {
        var index = startIndex;
        var hasDigits = false;

        while (index < formula.Length && char.IsDigit(formula[index]))
        {
            hasDigits = true;
            normalized.Append(formula[index++]);
        }

        if (index < formula.Length && formula[index] == '.')
        {
            normalized.Append('.');
            index++;

            while (index < formula.Length && char.IsDigit(formula[index]))
            {
                hasDigits = true;
                normalized.Append(formula[index++]);
            }
        }

        if (!hasDigits)
            throw SyntaxError(startIndex, "A decimal point must be part of a number.");

        if (index < formula.Length && formula[index] is 'e' or 'E')
        {
            normalized.Append('e');
            index++;

            if (index < formula.Length && formula[index] is '+' or '-')
                normalized.Append(formula[index++]);

            var exponentStart = index;

            while (index < formula.Length && char.IsDigit(formula[index]))
                normalized.Append(formula[index++]);

            if (exponentStart == index)
                throw SyntaxError(startIndex, "Scientific notation exponent is incomplete.");
        }

        return index;
    }

    private static bool IsVariableName(string? value)
    {
        return value is { Length: 1 } && value[0] is >= 'a' and <= 'z';
    }

    private static bool IsSingleCharacterToken(char value)
    {
        return value is '+' or '-' or '*' or '/' or '%' or '(' or ')' or ',' or '!';
    }

    private static CalculationException SyntaxError(int position, string message)
    {
        return new CalculationException("formula.syntax-not-allowed", $"{message} Position: {position + 1}.");
    }

    private sealed record ParsedFormula(string NormalizedExpression, IReadOnlyList<string> ReferencedVariables);
}