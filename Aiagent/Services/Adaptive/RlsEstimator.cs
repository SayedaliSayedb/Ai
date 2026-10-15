namespace Aiagent.Services.Adaptive;

/// <summary>Numerically defensive Recursive Least Squares linear estimator.</summary>
public sealed class RlsEstimator
{
    private readonly object _gate = new();
    private readonly int _dimensions;
    private readonly double _forgettingFactor;
    private readonly double[,] _covariance;
    private readonly double[] _theta;
    private long _updateCount;
    private double _residualEwma;
    private bool _hasResidual;
    private int _minConcurrency = int.MaxValue;
    private int _maxConcurrency = int.MinValue;

    public RlsEstimator(int dimensions, double forgettingFactor, double initialCovariance)
    {
        if (dimensions < 1) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (forgettingFactor <= 0 || forgettingFactor > 1)
            throw new ArgumentOutOfRangeException(nameof(forgettingFactor));
        if (initialCovariance <= 0) throw new ArgumentOutOfRangeException(nameof(initialCovariance));

        _dimensions = dimensions;
        _forgettingFactor = forgettingFactor;
        _theta = new double[dimensions];
        _covariance = new double[dimensions, dimensions];
        for (var i = 0; i < dimensions; i++)
            _covariance[i, i] = initialCovariance;
    }

    public long UpdateCount { get { lock (_gate) return _updateCount; } }
    public double ResidualEwma { get { lock (_gate) return _hasResidual ? _residualEwma : 0; } }
    public int MinObservedConcurrency { get { lock (_gate) return _minConcurrency == int.MaxValue ? 0 : _minConcurrency; } }
    public int MaxObservedConcurrency { get { lock (_gate) return _maxConcurrency == int.MinValue ? 0 : _maxConcurrency; } }

    public bool Update(double[] features, double observation, int observedConcurrency)
    {
        if (features is null || features.Length != _dimensions || !double.IsFinite(observation)) return false;
        if (features.Any(v => !double.IsFinite(v))) return false;

        lock (_gate)
        {
            var predicted = Dot(_theta, features);
            var residual = observation - predicted;
            var px = new double[_dimensions];
            for (var i = 0; i < _dimensions; i++)
                for (var j = 0; j < _dimensions; j++)
                    px[i] += _covariance[i, j] * features[j];

            var denominator = _forgettingFactor;
            for (var i = 0; i < _dimensions; i++)
                denominator += features[i] * px[i];

            if (!double.IsFinite(denominator) || denominator <= 1e-12)
                return false;

            var gain = new double[_dimensions];
            for (var i = 0; i < _dimensions; i++)
                gain[i] = px[i] / denominator;

            for (var i = 0; i < _dimensions; i++)
                _theta[i] += gain[i] * residual;

            // RLS covariance update: P = (P - K * x' * P) / lambda.
            // P is kept approximately symmetric to reduce floating-point drift.
            var xTP = new double[_dimensions];
            for (var j = 0; j < _dimensions; j++)
                for (var i = 0; i < _dimensions; i++)
                    xTP[j] += features[i] * _covariance[i, j];

            for (var i = 0; i < _dimensions; i++)
                for (var j = 0; j < _dimensions; j++)
                    _covariance[i, j] = (_covariance[i, j] - gain[i] * xTP[j]) / _forgettingFactor;

            for (var i = 0; i < _dimensions; i++)
            {
                for (var j = i + 1; j < _dimensions; j++)
                {
                    var symmetric = (_covariance[i, j] + _covariance[j, i]) / 2.0;
                    _covariance[i, j] = symmetric;
                    _covariance[j, i] = symmetric;
                }
                if (!double.IsFinite(_theta[i])) _theta[i] = 0;
                if (!double.IsFinite(_covariance[i, i]) || _covariance[i, i] < 1e-9)
                    _covariance[i, i] = 1e-9;
            }

            _residualEwma = _hasResidual ? 0.9 * _residualEwma + 0.1 * Math.Abs(residual) : Math.Abs(residual);
            _hasResidual = true;
            _updateCount++;
            _minConcurrency = Math.Min(_minConcurrency, observedConcurrency);
            _maxConcurrency = Math.Max(_maxConcurrency, observedConcurrency);
            return true;
        }
    }

    public bool TryPredict(double[] features, out double prediction)
    {
        prediction = 0;
        if (features is null || features.Length != _dimensions || features.Any(v => !double.IsFinite(v)))
            return false;
        lock (_gate)
        {
            if (_updateCount < 2) return false;
            prediction = Dot(_theta, features);
            return double.IsFinite(prediction) && prediction >= 0;
        }
    }

    private static double Dot(double[] left, double[] right)
    {
        var sum = 0.0;
        for (var i = 0; i < left.Length; i++) sum += left[i] * right[i];
        return sum;
    }
}
