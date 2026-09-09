namespace TechMES.Web.Components.Calc.Formula;

public partial class FormulaConfigurationPanel
{
    // Имена соответствуют AllowedFunctions в FormulaExpressionEngine.
    // Это подсказки редактора; проверка и вычисление остаются на сервере.
    private static readonly FormulaFunctionHelp[] FormulaFunctions =
    [
        new("abs", "abs(x)", "Returns the absolute value. x: a number.", "abs(-5) = 5", "abs([a])"),
        new("min", "min(x, y)", "Returns the smaller of two numbers. x, y: numbers.", "min(3, 7) = 3", "min([a], 0)"),
        new("max", "max(x, y)", "Returns the larger of two numbers. x, y: numbers.", "max(3, 7) = 7", "max([a], 0)"),
        new("round", "round(x[, digits])", "Rounds x to the specified decimal places; midpoint values round away from zero. digits: optional integer from 0 to 15, default 0.", "round(2.5) = 3", "round([a], 2)"),
        new("floor", "floor(x)", "Returns the greatest integer less than or equal to x. x: a number.", "floor(-1.2) = -2", "floor([a])"),
        new("ceil", "ceil(x)", "Returns the smallest integer greater than or equal to x. x: a number.", "ceil(1.2) = 2", "ceil([a])"),
        new("sqrt", "sqrt(x)", "Returns the square root. x: a non-negative number.", "sqrt(9) = 3", "sqrt([a])"),
        new("pow", "pow(x, exponent)", "Raises x to the specified exponent. Both arguments must produce a finite real result.", "pow(2, 3) = 8", "pow([a], 2)"),
        new("exp", "exp(x)", "Returns e raised to x. x: the exponent.", "exp(0) = 1", "exp([a])"),
        new("ln", "ln(x)", "Returns the natural logarithm, base e. x: a positive number.", "ln(e) = 1", "ln([a])"),
        new("log10", "log10(x)", "Returns the base-10 logarithm. x: a positive number.", "log10(100) = 2", "log10([a])"),
        new("logn", "logn(x, base)", "Returns the logarithm in the specified base. x > 0; base > 0 and base must not equal 1.", "logn(8, 2) = 3", "logn([a], 10)"),
        new("sin", "sin(angle)", "Returns the sine. angle: radians.", "sin(pi / 2) = 1", "sin([a])"),
        new("cos", "cos(angle)", "Returns the cosine. angle: radians.", "cos(0) = 1", "cos([a])"),
        new("tan", "tan(angle)", "Returns the tangent. angle: radians; avoid angles where the tangent is undefined.", "tan(0) = 0", "tan([a])"),
        new("asin", "asin(x)", "Returns the inverse sine in radians. x: a number from -1 to 1.", "asin(0) = 0", "asin([a])"),
        new("acos", "acos(x)", "Returns the inverse cosine in radians. x: a number from -1 to 1.", "acos(1) = 0", "acos([a])"),
        new("atan", "atan(x)", "Returns the inverse tangent in radians. x: a number.", "atan(0) = 0", "atan([a])"),
        new("atan2", "atan2(y, x)", "Returns the angle in radians for the point (x, y), taking the quadrant into account. The first argument is y.", "atan2(1, 1) = pi / 4", "atan2([a], 1)"),
        new("clamp", "clamp(x, minimum, maximum)", "Limits x to the inclusive range. All arguments must be finite; minimum must not exceed maximum.", "clamp(12, 0, 10) = 10", "clamp([a], 0, 100)"),
        new("if", "if(condition, whenTrue, whenFalse)", "Evaluates the selected branch. condition: a Boolean expression; whenTrue, whenFalse: numeric expressions.", "if(2 > 1, 10, 0) = 10", "if([a] > 0, [a], 0)")
    ];

    private sealed record FormulaFunctionHelp(string Name, string Signature, string Description, string Example, string Template)
    {
        public string Tooltip => $"{Signature}\n{Description}\nExample: {Example}\nDrag into Expression, or click to insert at the caret.";
    }
}