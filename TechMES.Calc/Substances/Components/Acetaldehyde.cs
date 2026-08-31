using TechMES.Calc.Content;
using TechMES.Calc.Exceptions;
using static TechMES.Calc.Content.ContentCorrelationMath;

namespace TechMES.Calc.Substances.Components;

internal class Acetaldehyde : LegacySubstance, IContentSubstanceModel
{
    #region fields & props

    private const double molarMass = 44.0;

    public override double MolarMass => molarMass;
    public override bool IsSteam => isSteam;

    #endregion

    public Acetaldehyde(bool _isSteam) : base(_isSteam)
    {
    }

    #region Density / Capacity

    public override double GetDensity(float temperature, float pressure)
    {
        return 0.0;
    }

    public override double GetCapacity(float temperature)
    {
        return 0.0;
    }

    #endregion

    #region Content

    private static readonly double[] AcaPoPressures =
    [
        0.3, 0.35, 0.4, 0.45, 0.5,
        0.55, 0.6, 0.65, 0.7, 0.75,
        0.8, 0.85, 0.9, 0.95, 1.0,
        1.1, 1.2, 1.3, 1.4, 1.5,
        1.6, 1.7, 1.8, 1.9, 2.0
    ];

    private static readonly CoefSet[] AcaPoCoefs =
    [
        new() { a0 = 0.4124895500, a1 = -0.0775958820, a2 = 0.0000813520, a3 = 0.0000101050, a4 = 0.0000001967, a5 = -0.0000000232 },
        new() { a0 = 0.6640816300, a1 = -0.0775024770, a2 = 0.0000609626, a3 = 0.0000035141, a4 = 0.0000004776, a5 = -0.0000000128 },
        new() { a0 = 0.8887442600, a1 = -0.0778619020, a2 = 0.0001132785, a3 = -0.0000030906, a4 = 0.0000005252, a5 = -0.0000000078 },
        new() { a0 = 1.0948275000, a1 = -0.0790502420, a2 = 0.0002599963, a3 = -0.0000151188, a4 = 0.0000009228, a5 = -0.0000000147 },
        new() { a0 = 1.2890629000, a1 = -0.0812818820, a2 = 0.0004600882, a3 = -0.0000240482, a4 = 0.0000009584, a5 = -0.0000000111 },
        new() { a0 = 1.4775151000, a1 = -0.0851828010, a2 = 0.0008268674, a3 = -0.0000445428, a4 = 0.0000015537, a5 = -0.0000000193 },
        new() { a0 = 1.6616453000, a1 = -0.0893593240, a2 = 0.0010530678, a3 = -0.0000469716, a4 = 0.0000013062, a5 = -0.0000000129 },
        new() { a0 = 1.8447888000, a1 = -0.0942519400, a2 = 0.0012710901, a3 = -0.0000479594, a4 = 0.0000010866, a5 = -0.0000000085 },
        new() { a0 = 2.0476778000, a1 = -0.1051332700, a2 = 0.0020785393, a3 = -0.0000809445, a4 = 0.0000017885, a5 = -0.0000000149 },
        new() { a0 = 2.2632663000, a1 = -0.1184354600, a2 = 0.0029767516, a3 = -0.0001135167, a4 = 0.0000023917, a5 = -0.0000000196 },
        new() { a0 = 2.4548613000, a1 = -0.1241521900, a2 = 0.0029572556, a3 = -0.0000965326, a4 = 0.0000017293, a5 = -0.0000000118 },
        new() { a0 = 2.6836581000, a1 = -0.1381667400, a2 = 0.0036905066, a3 = -0.0001166195, a4 = 0.0000020074, a5 = -0.0000000134 },
        new() { a0 = 2.9875272000, a1 = -0.1659114000, a2 = 0.0054111750, a3 = -0.0001730256, a4 = 0.0000029579, a5 = -0.0000000200 },
        new() { a0 = 3.2484188000, a1 = -0.1823621900, a2 = 0.0060579534, a3 = -0.0001824040, a4 = 0.0000029222, a5 = -0.0000000186 },
        new() { a0 = 3.4963758000, a1 = -0.1950478300, a2 = 0.0063714787, a3 = -0.0001796887, a4 = 0.0000026938, a5 = -0.0000000160 },
        new() { a0 = 3.9999790000, a1 = -0.2176298700, a2 = 0.0066482967, a3 = -0.0001618103, a4 = 0.0000020708, a5 = -0.0000000104 },
        new() { a0 = 5.0350447000, a1 = -0.3173425700, a2 = 0.0115221990, a3 = -0.0002856318, a4 = 0.0000036822, a5 = -0.0000000190 },
        new() { a0 = 6.3590169000, a1 = -0.4456969700, a2 = 0.0174359320, a3 = -0.0004257921, a4 = 0.0000053706, a5 = -0.0000000272 },
        new() { a0 = 7.3173812000, a1 = -0.5089863500, a2 = 0.0191330990, a3 = -0.0004367805, a4 = 0.0000051427, a5 = -0.0000000244 },
        new() { a0 = 8.1988183000, a1 = -0.5540264700, a2 = 0.0196625920, a3 = -0.0004165117, a4 = 0.0000045437, a5 = -0.0000000199 },
        new() { a0 = 9.8764292000, a1 = -0.6910229500, a2 = 0.0246045960, a3 = -0.0005057746, a4 = 0.0000053368, a5 = -0.0000000227 },
        new() { a0 = 11.9065720000, a1 = -0.8570048700, a2 = 0.0304830690, a3 = -0.0006100389, a4 = 0.0000062487, a5 = -0.0000000258 },
        new() { a0 = 14.2097140000, a1 = -1.0378511000, a2 = 0.0364838730, a3 = -0.0007078517, a4 = 0.0000070107, a5 = -0.0000000279 },
        new() { a0 = 16.6967540000, a1 = -1.2269440000, a2 = 0.0425109400, a3 = -0.0008021653, a4 = 0.0000077165, a5 = -0.0000000299 },
        new() { a0 = 20.3376970000, a1 = -1.5204801000, a2 = 0.0523822500, a3 = -0.0009684913, a4 = 0.0000091079, a5 = -0.0000000345 }
    ];

    public double GetContent(float temperature, float pressureBarAbsolute, ContentSystem system, int configurationCode)
    {
        if (isSteam)
            throw new CalculationException("content.phase.unsupported", "ACA Content correlation is defined only for liquid Acetaldehyde.");

        if (system != ContentSystem.AcaPo)
            throw new CalculationException("content.system.unsupported", $"Acetaldehyde Content correlation is not defined for system '{system}'.");

        return CalculateAcaPoContent(temperature, pressureBarAbsolute, configurationCode);
    }

    private static double CalculateAcaPoContent(float temperature, float pressureBarAbsolute, int configurationCode)
    {
        var numOfRange = GetNumOfFormula(AcaPoPressures, pressureBarAbsolute, out double deviation);
        double content;

        if (numOfRange == 0)
        {
            content = GetPolynomValue(temperature, AcaPoCoefs[0]);
        }
        else if (numOfRange == AcaPoPressures.Length)
        {
            content = GetPolynomValue(temperature, AcaPoCoefs[^1]);
        }
        else if (1 - deviation < 0.1)
        {
            content = GetPolynomValue(temperature, AcaPoCoefs[numOfRange]);
        }
        else
        {
            var tmpCount1 = GetPolynomValue(temperature, AcaPoCoefs[numOfRange - 1]);
            var tmpCount2 = GetPolynomValue(temperature, AcaPoCoefs[numOfRange]);

            content = tmpCount1 - (tmpCount1 - tmpCount2) * deviation;
        }

        if (configurationCode % 10 == 1)
            return content * 100.0;

        return Math.Max(0.0, Math.Min(100.0, content * 100.0));
    }

    #endregion
}