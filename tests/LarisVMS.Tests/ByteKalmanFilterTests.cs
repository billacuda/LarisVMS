using LarisVMS.Vision.Tracking;

namespace LarisVMS.Tests;

/// <summary>
/// Verifies the Kalman filter against reference values computed by an independent NumPy
/// reimplementation of the same equations -- not by comparing the port against itself. Ported from
/// aitest's own Aitest.Vision.Tests.Tracking.ByteKalmanFilterTests (MIT), unchanged apart from its
/// namespace/using — this is a full MIT-licensed ByteTrack port (see ByteKalmanFilter.cs's own
/// header) that shipped with zero test coverage in this repo despite the production code having
/// been ported verbatim; this restores the same verification the algorithm had in aitest.
/// </summary>
public sealed class ByteKalmanFilterTests
{
    private const float Tolerance = 1e-3f;

    // tlwh = [100, 100, 50, 80] -> xyah = [125, 140, 0.625, 80]
    private static readonly float[] InitialMeasurement = [125f, 140f, 0.625f, 80f];

    [Fact]
    public void Initiate_SetsZeroVelocityAndExpectedCovariance()
    {
        var filter = new ByteKalmanFilter();

        var (mean, covariance) = filter.Initiate(InitialMeasurement);

        Assert.Equal([125f, 140f, 0.625f, 80f, 0f, 0f, 0f, 0f], mean, new FloatComparer(Tolerance));

        var expectedDiagonal = new float[] { 64f, 64f, 0.0001f, 64f, 25f, 25f, 0f, 25f };
        AssertDiagonal(covariance, expectedDiagonal);
    }

    [Fact]
    public void Predict_AdvancesStateAndInflatesUncertainty()
    {
        var filter = new ByteKalmanFilter();
        var (mean, covariance) = filter.Initiate(InitialMeasurement);

        filter.Predict(mean, covariance);

        // Zero initial velocity means position does not move, but uncertainty must grow --
        // that growth is what lets the next Update trust a new measurement.
        Assert.Equal([125f, 140f, 0.625f, 80f, 0f, 0f, 0f, 0f], mean, new FloatComparer(Tolerance));

        var expectedDiagonal = new float[] { 105f, 105f, 0.0002f, 105f, 25.25f, 25.25f, 0f, 25.25f };
        AssertDiagonal(covariance, expectedDiagonal);
    }

    [Fact]
    public void Update_CorrectsTowardMeasurementAndShrinksUncertainty()
    {
        var filter = new ByteKalmanFilter();
        var (mean, covariance) = filter.Initiate(InitialMeasurement);
        filter.Predict(mean, covariance);

        // Box centre moved by (3, 2); size unchanged.
        float[] measurement = [128f, 142f, 0.625f, 80f];
        filter.Update(mean, covariance, measurement);

        var expectedMean = new float[]
        {
            127.603306f, 141.735537f, 0.625f, 80f,
            0.619835f, 0.413223f, 0f, 0f,
        };
        Assert.Equal(expectedMean, mean, new FloatComparer(1e-2f));

        var expectedDiagonal = new float[]
        {
            13.884298f, 13.884298f, 0.000196f, 13.884298f,
            20.084711f, 20.084711f, 0f, 20.084711f,
        };
        AssertDiagonal(covariance, expectedDiagonal, tolerance: 1e-1f);
    }

    [Fact]
    public void Update_MovesMeanLessThanRawMeasurementDelta()
    {
        // A single update should blend prediction and measurement, not jump straight to the
        // observation -- if it does, the gain computation collapsed to identity.
        var filter = new ByteKalmanFilter();
        var (mean, covariance) = filter.Initiate(InitialMeasurement);
        filter.Predict(mean, covariance);

        float[] measurement = [200f, 140f, 0.625f, 80f]; // moved 75px in x
        filter.Update(mean, covariance, measurement);

        Assert.True(mean[0] > 125f && mean[0] < 200f,
            $"expected blended x between 125 and 200, got {mean[0]}");
    }

    private static void AssertDiagonal(float[,] covariance, float[] expected, float tolerance = Tolerance)
    {
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(
                Math.Abs(covariance[i, i] - expected[i]) <= tolerance,
                $"covariance[{i},{i}] = {covariance[i, i]}, expected {expected[i]} (+/-{tolerance})");
        }
    }

    private sealed class FloatComparer(float tolerance) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(float obj) => 0;
    }
}
