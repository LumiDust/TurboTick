using HarmonyLib;
using UnityEngine;

namespace TurboTick;

/// <summary>
/// Replaces the few <see cref="Mathf"/> functions whose results differ between operating systems with
/// implementations that only use correctly-rounded IEEE operations, so every peer computes the same bits.
///
/// Measured, not assumed. <c>tools/mathprobe</c> hashes the exact bit pattern of each function over a wide input
/// range and is run on both machines; the same .NET 10.0.14 gives:
///
/// <code>
/// Windows: sin=DCCF49050088 cos=782AF3E9E29C log=65D989582CF4 log10=8ADE2A1EDF7E pow=61175B07F041
/// Linux  : sin=1F543E96EC4D cos=BA5805900833 log=41945028D7CF log10=61A345298874 pow=16F8B5A42243
/// Windows: sqrt=318E17E14BAB tan=E3E45123F51E atan=0D69E7CBABB0 atan2=B16D91EDD0BA exp=3462321ACA9B
/// Linux  : sqrt=318E17E14BAB tan=E3E45123F51E atan=0D69E7CBABB0 atan2=B16D91EDD0BA exp=3462321ACA9B
/// </code>
///
/// <c>Mathf.Sin</c> and friends call into the platform's maths library, which is not required to round the same
/// way. This alone explains the divergence signature that took several rounds to pin down: the peers' RNG state
/// (<see cref="SimulationRandom.Capture"/>) was <em>identical</em> while a pawn's job, an item's hit points and a
/// piece of filth differed - because <c>Rand.Gaussian</c> is
/// <c>Mathf.Sqrt(-2 * Mathf.Log(v1)) * Mathf.Sin(2pi * v2)</c> (<c>Verse/Rand.cs:151-156</c>), and one draw can
/// land on the other side of a threshold. It also explains why the divergence was intermittent and why it took
/// thousands of ticks: only draws that straddle a threshold change anything.
///
/// The functions that already agree bit-for-bit (<c>Sqrt</c>, <c>Tan</c>, <c>Atan</c>, <c>Atan2</c>, <c>Exp</c>)
/// are left alone - every replacement is a risk, and there is no reason to take one that buys nothing.
/// </summary>
internal static class DeterministicMath
{
    private const double TwoPi = 6.283185307179586476925286766559;
    private const double InvTwoPi = 0.15915494309189533576888376337251;
    private const double PiOverTwo = 1.5707963267948966192313216916398;
    private const double InvPiOverTwo = 0.63661977236758134307553505349006;
    private const double Ln2 = 0.69314718055994530941723212145818;
    private const double Ln10 = 2.3025850929940456840179914546844;

    /// <summary>A digest of this implementation over a fixed input set. Two machines must print the same value;
    /// it is the cheapest way to prove both ends replaced the same functions with the same code.
    ///
    /// Sanity check outside the game: `tools/mathprobe --deterministic` prints the same digest and agrees across
    /// Windows and Linux (`efb6508c…ec28bb`), while the platform implementations do not
    /// (`80d5e7fe…` vs `a33362a6…`).</summary>
    internal static string Fingerprint()
    {
        var buffer = new System.Text.StringBuilder();
        for (var i = 1; i <= 256; i++)
        {
            var unit = i / 257f;
            var angle = i * 6.2831855f / 256f;
            Append(buffer, SinS(angle));
            Append(buffer, CosS(angle));
            Append(buffer, LogS(unit));
            Append(buffer, Log10S(unit));
            Append(buffer, PowS(unit, 0.37f));
            Append(buffer, PowS(1.13f, i * 0.01f));
        }
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(buffer.ToString()));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Checks this implementation against values that are known without a reference library, and reports the
    /// first contradiction.
    ///
    /// <para>Why a self-check and not a unit test: <see cref="Fingerprint"/> only proves that two machines run the same
    /// code - it happily agrees on a wrong answer (that is exactly what happened: the fingerprint matched across
    /// Windows and Linux while <c>Log(1.0)</c> returned -36.04 on both). The failure this catches was a bias
    /// off by 1023 in the exponent removal, which made every logarithm and every power wrong by a fixed factor, and no
    /// cross-machine comparison could see it.</para>
    ///
    /// <para>Returns null when every identity holds, otherwise a sentence naming the first one that does not.</para>
    /// </summary>
    internal static string? SelfCheck()
    {
        // log(1) == 0 and log(e) == 1 are definitional, so no tolerance question arises: these must be exact.
        if (MathF.Abs(Log(1f)) > 1e-6f) return $"Log(1)={Log(1f):0.000000}（应为 0）";
        if (MathF.Abs(Log(2f) - 0.6931472f) > 1e-4f) return $"Log(2)={Log(2f):0.000000}（应为 0.693147）";
        if (MathF.Abs(Log(10f) - 2.3025851f) > 1e-4f) return $"Log(10)={Log(10f):0.000000}（应为 2.302585）";
        // A power that the construction driver actually computes: `Pow(0.88, 1.7/95)` must stay very close to 1,
        // because the failure threshold `1 - Pow(chance, num/work)` is what decides whether a wall survives.
        var near = Pow(0.88f, 1.7f / 95f);
        if (MathF.Abs(near - 0.99768f) > 1e-3f) return $"Pow(0.88, 0.0179)={near:0.000000}（应约为 0.99768）";
        if (MathF.Abs(Pow(2f, 10f) - 1024f) > 0.05f) return $"Pow(2,10)={Pow(2f, 10f):0.000000}（应为 1024）";
        if (MathF.Abs(Sin(0f)) > 1e-6f) return $"Sin(0)={Sin(0f):0.000000}（应为 0）";
        if (MathF.Abs(Cos(0f) - 1f) > 1e-6f) return $"Cos(0)={Cos(0f):0.000000}（应为 1）";
        if (MathF.Abs(Log10(1000f) - 3f) > 1e-4f) return $"Log10(1000)={Log10(1000f):0.000000}（应为 3）";
        return null;
    }

    private static float SinS(float value) => (float)SinD(value);

    private static float CosS(float value) => (float)CosD(value);

    private static float LogS(float value) => (float)LogD(value);

    private static float Log10S(float value) => value <= 0f ? float.NaN : (float)(LogD(value) / Ln10);

    private static float PowS(float value, float exponent) => (float)ExpD(exponent * LogD(value));

    private static void Append(System.Text.StringBuilder buffer, float value) =>
        buffer.Append(BitConverter.SingleToInt32Bits(value).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).Append(char.MinValue);

    internal static float Sin(float value) => (float)SinD(value);

    internal static float Cos(float value) => (float)CosD(value);

    internal static float Log(float value) => (float)LogD(value);

    internal static float Log10(float value) => value < 0f || float.IsNaN(value)
        ? float.NaN
        : value == 0f ? float.NegativeInfinity : (float)(LogD(value) / Ln10);

    internal static float Pow(float value, float exponent)
    {
        if (exponent == 0f) return 1f;
        if (value <= 0f)
        {
            // Negative bases only have a real answer for integer exponents; keep that case exact.
            if (value == 0f) return exponent < 0f ? float.PositiveInfinity : 0f;
            if (exponent != MathF.Floor(exponent)) return float.NaN;
            var magnitude = (float)ExpD(exponent * LogD(-value));
            return MathF.Abs(exponent % 2f) < 0.5f ? magnitude : -magnitude;
        }
        return (float)ExpD(exponent * LogD(value));
    }

    private static double SinD(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
        // Reduce to r in [-pi/4, pi/4] and remember the quadrant. Two-part subtraction keeps the reduction
        // exact enough for float results even when x is a few million radians.
        var quadrant = Math.Floor(x * InvPiOverTwo + 0.5);
        var r = x - quadrant * PiOverTwo - quadrant * 6.123233995736765886130329661375e-17;
        switch ((int)((long)quadrant & 3))
        {
            case 0: return SinSeries(r);
            case 1: return CosSeries(r);
            case 2: return -SinSeries(r);
            default: return -CosSeries(r);
        }
    }

    private static double CosD(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) return double.NaN;
        var quadrant = Math.Floor(x * InvPiOverTwo + 0.5);
        var r = x - quadrant * PiOverTwo - quadrant * 6.123233995736765886130329661375e-17;
        switch ((int)((long)quadrant & 3))
        {
            case 0: return CosSeries(r);
            case 1: return -SinSeries(r);
            case 2: return -CosSeries(r);
            default: return SinSeries(r);
        }
    }

    // Taylor series; on |r| <= pi/4 the first dropped term is ~1e-17, which is far below float precision.
    private static double SinSeries(double r)
    {
        var r2 = r * r;
        return r * (1d + r2 * (-1d / 6d + r2 * (1d / 120d + r2 * (-1d / 5040d + r2 * (1d / 362880d
            + r2 * (-1d / 39916800d + r2 * (1d / 6227020800d + r2 * (-1d / 1307674368000d))))))));
    }

    private static double CosSeries(double r)
    {
        var r2 = r * r;
        return 1d + r2 * (-1d / 2d + r2 * (1d / 24d + r2 * (-1d / 720d + r2 * (1d / 40320d
            + r2 * (-1d / 3628800d + r2 * (1d / 479001600d + r2 * (-1d / 87178291200d)))))));
    }

    private static double LogD(double x)
    {
        if (double.IsNaN(x) || x < 0d) return double.NaN;
        if (x == 0d) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return double.PositiveInfinity;
        // Split into mantissa in [1, 2) and a power of two using only bit moves, then log(m) through the
        // atanh series, which converges quickly because |z| <= 1/3 there.
        var bits = BitConverter.DoubleToInt64Bits(x);
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            // Subnormal: scale up by 2^54, then subtract the extra exponent. The bias goes here too - the branch used
            // to leave the biased exponent in place, so a subnormal's logarithm was off by another 1023*ln2.
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984d);
            exponent = (int)((bits >> 52) & 0x7FF) - 54 - 1023;
            mantissa = bits & 0xFFFFFFFFFFFFFL;
        }
        else
        {
            // `- 1023`, not `- 1075`. The bias is 1023, and `m` below is rebuilt as `1.fraction` (in [1,2)), so the
            // fraction is already accounted for by the series. The integer-mantissa form would need `- 1075` together
            // with `log(M)` for `M = fraction * 2^52`, i.e. `2*series + 52*ln2`; this code adds only `2*series`.
            // Mixing the two conventions made every result short by exactly `52*ln2 = 36.0437`:
            // `Log(1.0)` returned -36.04, and because `Pow` is `exp(p * LogD(x))` it became `x^p * e^(-36.04p)`.
            //
            // Measured, and not a theory: `Mathf.Pow(0.88, 1.7/95)` - the exact expression
            // `JobDriver_ConstructFinishFrame` uses for its failure roll - returned 0.274 instead of 0.9954, so the
            // failure threshold was 0.73 instead of 0.002, and every wall was destroyed within a few ticks of being
            // started at 73% per tick. The colony could not build anything at all, and the probes counted
            // `完成=0 失败=13` while the deposit log proved the materials arrived. `Rand.Gaussian` was distorted the
            // same way (`Sqrt(-2 * Log(v1))` reads `Sqrt(-2*(ln v1 - 36.04))`, a much wider sigma), so this was never
            // only about construction.
            exponent -= 1023;
        }
        var m = BitConverter.Int64BitsToDouble(mantissa | 0x3FF0000000000000L);
        var z = (m - 1d) / (m + 1d);
        var z2 = z * z;
        var series = z * (1d + z2 * (1d / 3d + z2 * (1d / 5d + z2 * (1d / 7d + z2 * (1d / 9d
            + z2 * (1d / 11d + z2 * (1d / 13d + z2 * (1d / 15d + z2 * (1d / 17d + z2 * (1d / 19d
                + z2 * (1d / 21d + z2 / 23d)))))))))));
        return exponent * Ln2 + 2d * series;
    }

    private static double ExpD(double y)
    {
        if (double.IsNaN(y)) return double.NaN;
        if (y > 709d) return double.PositiveInfinity;
        if (y < -745d) return 0d;
        var k = Math.Floor(y / Ln2 + 0.5);
        var r = y - k * Ln2;
        // exp(r) for |r| <= ln2/2: eleven terms reach double precision.
        var series = 1d;
        var term = 1d;
        for (var n = 1; n <= 11; n++)
        {
            term *= r / n;
            series += term;
        }
        return Math.ScaleB(series, (int)k);
    }
}

[HarmonyPatch(typeof(Mathf), nameof(Mathf.Sin), [typeof(float)])]
internal static class DeterministicSinPatch
{
    private static bool Prefix(float f, ref float __result)
    {
        __result = DeterministicMath.Sin(f);
        return false;
    }
}

[HarmonyPatch(typeof(Mathf), nameof(Mathf.Cos), [typeof(float)])]
internal static class DeterministicCosPatch
{
    private static bool Prefix(float f, ref float __result)
    {
        __result = DeterministicMath.Cos(f);
        return false;
    }
}

[HarmonyPatch(typeof(Mathf), nameof(Mathf.Log), [typeof(float)])]
internal static class DeterministicLogPatch
{
    private static bool Prefix(float f, ref float __result)
    {
        __result = DeterministicMath.Log(f);
        return false;
    }
}

[HarmonyPatch(typeof(Mathf), nameof(Mathf.Log10), [typeof(float)])]
internal static class DeterministicLog10Patch
{
    private static bool Prefix(float f, ref float __result)
    {
        __result = DeterministicMath.Log10(f);
        return false;
    }
}

[HarmonyPatch(typeof(Mathf), nameof(Mathf.Pow), [typeof(float), typeof(float)])]
internal static class DeterministicPowPatch
{
    private static bool Prefix(float f, float p, ref float __result)
    {
        __result = DeterministicMath.Pow(f, p);
        return false;
    }
}


