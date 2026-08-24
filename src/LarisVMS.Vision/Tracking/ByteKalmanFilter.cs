// Ported from ByteTrack — https://github.com/FoundationVision/ByteTrack
// SPDX-License-Identifier: MIT
// Copyright (c) 2021 Yifu Zhang
//
// C# port of deploy/TensorRT/cpp/src/kalmanFilter.cpp (byte_kalman::KalmanFilter).
// The Eigen fixed-size matrices are replaced with plain arrays and explicit loops; the constants
// and update equations are unchanged.
//
// Ported verbatim from aitest (g:\Projects\aitest\src\Aitest.Vision\Tracking\ByteKalmanFilter.cs),
// which already unit-tests this against independently-computed reference vectors.

namespace LarisVMS.Vision.Tracking;

/// <summary>
/// The 8-state constant-velocity filter ByteTrack tracks boxes with.
///
/// State is [x, y, a, h, vx, vy, va, vh] — box centre, aspect ratio (w/h), height, and their
/// velocities. Measurements are [x, y, a, h].
///
/// Note this is deliberately not <c>YoloDotNet.Trackers.KalmanFilter</c>, which is a 4-state
/// centre-point filter. That one tracks position only and cannot reproduce ByteTrack's behaviour,
/// because box size is part of what the association step relies on.
/// </summary>
internal sealed class ByteKalmanFilter
{
    private const int Dim = 8;
    private const int MeasurementDim = 4;

    private const float StdWeightPosition = 1f / 20f;
    private const float StdWeightVelocity = 1f / 160f;

    private readonly float[,] _motionMat = new float[Dim, Dim];
    private readonly float[,] _updateMat = new float[MeasurementDim, Dim];

    public ByteKalmanFilter()
    {
        // Constant velocity model: position += velocity * dt, with dt = 1 frame.
        for (var i = 0; i < Dim; i++)
        {
            _motionMat[i, i] = 1f;
        }

        for (var i = 0; i < MeasurementDim; i++)
        {
            _motionMat[i, MeasurementDim + i] = 1f;
            _updateMat[i, i] = 1f;
        }
    }

    /// <summary>Creates a track's initial state from its first observed box.</summary>
    public (float[] Mean, float[,] Covariance) Initiate(ReadOnlySpan<float> measurement)
    {
        var mean = new float[Dim];
        for (var i = 0; i < MeasurementDim; i++)
        {
            mean[i] = measurement[i];
            mean[MeasurementDim + i] = 0f;
        }

        var h = measurement[3];
        var std = new[]
        {
            2 * StdWeightPosition * h,
            2 * StdWeightPosition * h,
            1e-2f,
            2 * StdWeightPosition * h,
            10 * StdWeightVelocity * h,
            10 * StdWeightVelocity * h,
            1e-5f,
            10 * StdWeightVelocity * h,
        };

        var covariance = new float[Dim, Dim];
        for (var i = 0; i < Dim; i++)
        {
            covariance[i, i] = std[i] * std[i];
        }

        return (mean, covariance);
    }

    /// <summary>Advances the state one frame. Mean and covariance are updated in place.</summary>
    public void Predict(float[] mean, float[,] covariance)
    {
        var h = mean[3];
        var std = new[]
        {
            StdWeightPosition * h,
            StdWeightPosition * h,
            1e-2f,
            StdWeightPosition * h,
            StdWeightVelocity * h,
            StdWeightVelocity * h,
            1e-5f,
            StdWeightVelocity * h,
        };

        // mean = F * mean
        var newMean = new float[Dim];
        for (var i = 0; i < Dim; i++)
        {
            var sum = 0f;
            for (var j = 0; j < Dim; j++)
            {
                sum += _motionMat[i, j] * mean[j];
            }

            newMean[i] = sum;
        }

        Array.Copy(newMean, mean, Dim);

        // covariance = F * P * F^T + Q
        var fp = Multiply(_motionMat, covariance);
        var fpft = MultiplyTransposed(fp, _motionMat);

        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < Dim; j++)
            {
                covariance[i, j] = fpft[i, j] + (i == j ? std[i] * std[i] : 0f);
            }
        }
    }

    /// <summary>Projects state into measurement space, adding observation noise.</summary>
    private (float[] Mean, float[,] Covariance) Project(float[] mean, float[,] covariance)
    {
        var h = mean[3];
        var std = new[]
        {
            StdWeightPosition * h,
            StdWeightPosition * h,
            1e-1f,
            StdWeightPosition * h,
        };

        var projectedMean = new float[MeasurementDim];
        for (var i = 0; i < MeasurementDim; i++)
        {
            var sum = 0f;
            for (var j = 0; j < Dim; j++)
            {
                sum += _updateMat[i, j] * mean[j];
            }

            projectedMean[i] = sum;
        }

        // H * P * H^T + R
        var projectedCov = new float[MeasurementDim, MeasurementDim];
        for (var i = 0; i < MeasurementDim; i++)
        {
            for (var j = 0; j < MeasurementDim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Dim; k++)
                {
                    for (var l = 0; l < Dim; l++)
                    {
                        sum += _updateMat[i, k] * covariance[k, l] * _updateMat[j, l];
                    }
                }

                projectedCov[i, j] = sum + (i == j ? std[i] * std[i] : 0f);
            }
        }

        return (projectedMean, projectedCov);
    }

    /// <summary>Corrects the state with an observed box. Mean and covariance change in place.</summary>
    public void Update(float[] mean, float[,] covariance, ReadOnlySpan<float> measurement)
    {
        var (projectedMean, projectedCov) = Project(mean, covariance);

        // B = P * H^T  (8x4)
        var b = new float[Dim, MeasurementDim];
        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < MeasurementDim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Dim; k++)
                {
                    sum += covariance[i, k] * _updateMat[j, k];
                }

                b[i, j] = sum;
            }
        }

        // Kalman gain K = B * S^-1, obtained by solving S * K^T = B^T.
        var gain = SolveGain(projectedCov, b);

        // mean += K * (measurement - projected_mean)
        Span<float> innovation = stackalloc float[MeasurementDim];
        for (var i = 0; i < MeasurementDim; i++)
        {
            innovation[i] = measurement[i] - projectedMean[i];
        }

        for (var i = 0; i < Dim; i++)
        {
            var sum = 0f;
            for (var j = 0; j < MeasurementDim; j++)
            {
                sum += gain[i, j] * innovation[j];
            }

            mean[i] += sum;
        }

        // covariance -= K * S * K^T
        var ks = new float[Dim, MeasurementDim];
        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < MeasurementDim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < MeasurementDim; k++)
                {
                    sum += gain[i, k] * projectedCov[k, j];
                }

                ks[i, j] = sum;
            }
        }

        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < Dim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < MeasurementDim; k++)
                {
                    sum += ks[i, k] * gain[j, k];
                }

                covariance[i, j] -= sum;
            }
        }
    }

    /// <summary>
    /// Solves S * X = B^T for the 8x4 Kalman gain, via Gaussian elimination with partial pivoting
    /// on the 4x4 innovation covariance. The reference uses Eigen's Cholesky solve; S is symmetric
    /// positive definite here, so both give the same answer, and pivoting is more forgiving of the
    /// near-singular cases that show up when a track's height collapses.
    /// </summary>
    private static float[,] SolveGain(float[,] s, float[,] b)
    {
        var a = new float[MeasurementDim, MeasurementDim + Dim];

        for (var i = 0; i < MeasurementDim; i++)
        {
            for (var j = 0; j < MeasurementDim; j++)
            {
                a[i, j] = s[i, j];
            }

            // Augment with B^T (4x8).
            for (var j = 0; j < Dim; j++)
            {
                a[i, MeasurementDim + j] = b[j, i];
            }
        }

        for (var col = 0; col < MeasurementDim; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < MeasurementDim; row++)
            {
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col]))
                {
                    pivot = row;
                }
            }

            if (pivot != col)
            {
                for (var j = 0; j < MeasurementDim + Dim; j++)
                {
                    (a[col, j], a[pivot, j]) = (a[pivot, j], a[col, j]);
                }
            }

            var diagonal = a[col, col];
            if (Math.Abs(diagonal) < 1e-12f)
            {
                diagonal = diagonal < 0 ? -1e-12f : 1e-12f;
            }

            for (var j = col; j < MeasurementDim + Dim; j++)
            {
                a[col, j] /= diagonal;
            }

            for (var row = 0; row < MeasurementDim; row++)
            {
                if (row == col)
                {
                    continue;
                }

                var factor = a[row, col];
                if (factor == 0f)
                {
                    continue;
                }

                for (var j = col; j < MeasurementDim + Dim; j++)
                {
                    a[row, j] -= factor * a[col, j];
                }
            }
        }

        // X is 4x8; the gain is its transpose.
        var gain = new float[Dim, MeasurementDim];
        for (var i = 0; i < MeasurementDim; i++)
        {
            for (var j = 0; j < Dim; j++)
            {
                gain[j, i] = a[i, MeasurementDim + j];
            }
        }

        return gain;
    }

    private static float[,] Multiply(float[,] left, float[,] right)
    {
        var result = new float[Dim, Dim];
        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < Dim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Dim; k++)
                {
                    sum += left[i, k] * right[k, j];
                }

                result[i, j] = sum;
            }
        }

        return result;
    }

    /// <summary>Computes left * right^T.</summary>
    private static float[,] MultiplyTransposed(float[,] left, float[,] right)
    {
        var result = new float[Dim, Dim];
        for (var i = 0; i < Dim; i++)
        {
            for (var j = 0; j < Dim; j++)
            {
                var sum = 0f;
                for (var k = 0; k < Dim; k++)
                {
                    sum += left[i, k] * right[j, k];
                }

                result[i, j] = sum;
            }
        }

        return result;
    }
}
