using TechMES.Calc.Content;

namespace TechMES.Calc.Tests;

/// <summary>
/// Архитектурные проверки Content.
/// Legacy ContentCalc разрешён только как regression oracle внутри тестового проекта. В production-сборке TechMES.Calc этого типа быть не должно.
/// </summary>
public sealed class ContentArchitectureTests
{
    [Fact]
    public void LegacyContentCalcIsNotCompiledIntoProductionAssembly()
    {
        var productionAssembly = typeof(ContentPropertyCalculator).Assembly;
        var legacyType = productionAssembly.GetType("TechMES.Calc.Content.ContentCalc", throwOnError: false, ignoreCase: false);

        Assert.Null(legacyType);
    }

    [Fact]
    public void LegacyContentCalcLivesOnlyInTestAssembly()
    {
        Assert.Equal(typeof(ContentArchitectureTests).Assembly, typeof(ContentCalc).Assembly);
    }
}