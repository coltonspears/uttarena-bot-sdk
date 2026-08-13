namespace UttArena.PolicyValueBot;

internal readonly record struct NetworkOutput(float[] Logits, float Value);

internal sealed class PolicyValueNetwork
{
    internal const int FeatureCount = 7 * 9 * 9;
    internal const int ActionCount = 81;

    private readonly object _evaluationLock = new();
    private readonly Workspace _workspace = new();
    private readonly float[] _localFc1Weight;
    private readonly float[] _localFc1Bias;
    private readonly float[] _localFc2Weight;
    private readonly float[] _localFc2Bias;
    private readonly float[] _macroFc1Weight;
    private readonly float[] _macroFc1Bias;
    private readonly float[] _macroFc2Weight;
    private readonly float[] _macroFc2Bias;
    private readonly float[] _policyFc1Weight;
    private readonly float[] _policyFc1Bias;
    private readonly float[] _policyFc2Weight;
    private readonly float[] _policyFc2Bias;
    private readonly float[] _valueFc1Weight;
    private readonly float[] _valueFc1Bias;
    private readonly float[] _valueFc2Weight;
    private readonly float[] _valueFc2Bias;

    public PolicyValueNetwork(WeightSet weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _localFc1Weight = weights["local_fc1.weight"];
        _localFc1Bias = weights["local_fc1.bias"];
        _localFc2Weight = weights["local_fc2.weight"];
        _localFc2Bias = weights["local_fc2.bias"];
        _macroFc1Weight = weights["macro_fc1.weight"];
        _macroFc1Bias = weights["macro_fc1.bias"];
        _macroFc2Weight = weights["macro_fc2.weight"];
        _macroFc2Bias = weights["macro_fc2.bias"];
        _policyFc1Weight = weights["policy_fc1.weight"];
        _policyFc1Bias = weights["policy_fc1.bias"];
        _policyFc2Weight = weights["policy_fc2.weight"];
        _policyFc2Bias = weights["policy_fc2.bias"];
        _valueFc1Weight = weights["value_fc1.weight"];
        _valueFc1Bias = weights["value_fc1.bias"];
        _valueFc2Weight = weights["value_fc2.weight"];
        _valueFc2Bias = weights["value_fc2.bias"];
    }

    public NetworkOutput Evaluate(float[] features, bool[]? legalMask = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (features.Length != FeatureCount)
        {
            throw new ArgumentException($"Features must contain exactly {FeatureCount} values.", nameof(features));
        }

        if (legalMask is not null && legalMask.Length != ActionCount)
        {
            throw new ArgumentException("Legal mask must contain exactly 81 values.", nameof(legalMask));
        }

        for (var index = 0; index < features.Length; index++)
        {
            if (!float.IsFinite(features[index]))
            {
                throw new ArgumentException("Features must contain only finite FP32 values.", nameof(features));
            }
        }

        lock (_evaluationLock)
        {
            return EvaluateCore(features, legalMask);
        }
    }

    private NetworkOutput EvaluateCore(float[] features, bool[]? legalMask)
    {
        var workspace = _workspace;
        for (var board = 0; board < 9; board++)
        {
            ExtractLocalBoard(features, board, workspace.LocalInput);
            DenseRelu(
                workspace.LocalInput,
                63,
                _localFc1Weight,
                _localFc1Bias,
                workspace.LocalHidden,
                64);
            DenseRelu(
                workspace.LocalHidden,
                64,
                _localFc2Weight,
                _localFc2Bias,
                workspace.LocalEmbedding,
                32);
            workspace.LocalEmbedding.AsSpan().CopyTo(
                workspace.AllLocalEmbeddings.AsSpan(board * 32, 32));
        }

        DenseRelu(
            workspace.AllLocalEmbeddings,
            288,
            _macroFc1Weight,
            _macroFc1Bias,
            workspace.MacroHidden,
            256);
        DenseRelu(
            workspace.MacroHidden,
            256,
            _macroFc2Weight,
            _macroFc2Bias,
            workspace.MacroEmbedding,
            128);

        var logits = new float[ActionCount];
        for (var board = 0; board < 9; board++)
        {
            workspace.AllLocalEmbeddings
                .AsSpan(board * 32, 32)
                .CopyTo(workspace.PolicyInput);
            workspace.MacroEmbedding.CopyTo(workspace.PolicyInput, 32);
            DenseRelu(
                workspace.PolicyInput,
                160,
                _policyFc1Weight,
                _policyFc1Bias,
                workspace.PolicyHidden,
                64);
            Dense(
                workspace.PolicyHidden,
                64,
                _policyFc2Weight,
                _policyFc2Bias,
                logits.AsSpan(board * 9, 9),
                9);
        }

        DenseRelu(
            workspace.MacroEmbedding,
            128,
            _valueFc1Weight,
            _valueFc1Bias,
            workspace.ValueHidden,
            64);
        var value = MathF.Tanh(DotRow(
            workspace.ValueHidden,
            _valueFc2Weight,
            _valueFc2Bias[0],
            0,
            64));

        if (legalMask is not null)
        {
            for (var action = 0; action < ActionCount; action++)
            {
                if (!legalMask[action])
                {
                    logits[action] = float.NegativeInfinity;
                }
            }
        }

        return new NetworkOutput(logits, value);
    }

    private static void ExtractLocalBoard(float[] features, int board, float[] destination)
    {
        var boardRow = board / 3;
        var boardColumn = board % 3;
        for (var channel = 0; channel < 7; channel++)
        {
            var channelOffset = channel * 81;
            var destinationOffset = channel * 9;
            for (var cell = 0; cell < 9; cell++)
            {
                var row = (boardRow * 3) + (cell / 3);
                var column = (boardColumn * 3) + (cell % 3);
                destination[destinationOffset + cell] =
                    features[channelOffset + (row * 9) + column];
            }
        }
    }

    private static void DenseRelu(
        float[] input,
        int inputSize,
        float[] weight,
        float[] bias,
        float[] output,
        int outputSize)
    {
        for (var row = 0; row < outputSize; row++)
        {
            var value = DotRow(input, weight, bias[row], row, inputSize);
            output[row] = value > 0 ? value : 0;
        }
    }

    private static void Dense(
        float[] input,
        int inputSize,
        float[] weight,
        float[] bias,
        Span<float> output,
        int outputSize)
    {
        for (var row = 0; row < outputSize; row++)
        {
            output[row] = DotRow(input, weight, bias[row], row, inputSize);
        }
    }

    private static float DotRow(
        float[] input,
        float[] weight,
        float bias,
        int row,
        int inputSize)
    {
        var sum = bias;
        var weightOffset = row * inputSize;
        for (var column = 0; column < inputSize; column++)
        {
            sum += input[column] * weight[weightOffset + column];
        }

        return sum;
    }

    private sealed class Workspace
    {
        public float[] LocalInput { get; } = new float[63];
        public float[] LocalHidden { get; } = new float[64];
        public float[] LocalEmbedding { get; } = new float[32];
        public float[] AllLocalEmbeddings { get; } = new float[288];
        public float[] MacroHidden { get; } = new float[256];
        public float[] MacroEmbedding { get; } = new float[128];
        public float[] PolicyInput { get; } = new float[160];
        public float[] PolicyHidden { get; } = new float[64];
        public float[] ValueHidden { get; } = new float[64];
    }
}
