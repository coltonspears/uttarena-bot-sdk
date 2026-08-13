using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace UttArena.PolicyValueBot;

internal sealed record TensorSpec(string Name, int[] Shape)
{
    public int ElementCount { get; } =
        Shape.Aggregate(1, (count, value) => checked(count * value));
}

internal sealed class WeightSet
{
    public WeightSet(
        Dictionary<string, float[]> tensors,
        int metadataBytes,
        long weightBytes,
        long totalBytes,
        string checksum)
    {
        Tensors = tensors;
        MetadataBytes = metadataBytes;
        WeightBytes = weightBytes;
        TotalBytes = totalBytes;
        Checksum = checksum;
    }

    public IReadOnlyDictionary<string, float[]> Tensors { get; }

    public int MetadataBytes { get; }

    public long WeightBytes { get; }

    public long TotalBytes { get; }

    public string Checksum { get; }

    public float[] this[string name] => Tensors[name];
}

internal static class WeightFile
{
    private static readonly byte[] Magic = "UTTAPV01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal const uint SchemaVersion = 1;
    internal const long ExpectedWeightBytes = 529_064;

    // Schema 1 requires this exact sorted, compact UTF-8 JSON document.
    private const string CanonicalMetadata =
        "{\"action_order\":\"board*9+cell\",\"affine_initialization\":\"torch.nn.Linear.reset_parameters\"," +
        "\"fp32_bytes\":529064,\"hidden_activation\":\"relu\",\"input_shape\":[7,9,9]," +
        "\"local_board_order\":\"row-major\",\"local_encoder\":[63,64,32]," +
        "\"macro_encoder\":[288,256,128],\"name\":\"hierarchical-shared-mlp\"," +
        "\"parameter_count\":132266,\"policy_head\":[160,64,9],\"value_activation\":\"tanh\"," +
        "\"value_head\":[128,64,1],\"version\":1}";

    private static readonly byte[] CanonicalMetadataBytes = Encoding.UTF8.GetBytes(CanonicalMetadata);

    internal static readonly TensorSpec[] ExpectedTensors =
    [
        new("local_fc1.weight", [64, 63]),
        new("local_fc1.bias", [64]),
        new("local_fc2.weight", [32, 64]),
        new("local_fc2.bias", [32]),
        new("macro_fc1.weight", [256, 288]),
        new("macro_fc1.bias", [256]),
        new("macro_fc2.weight", [128, 256]),
        new("macro_fc2.bias", [128]),
        new("policy_fc1.weight", [64, 160]),
        new("policy_fc1.bias", [64]),
        new("policy_fc2.weight", [9, 64]),
        new("policy_fc2.bias", [9]),
        new("value_fc1.weight", [64, 128]),
        new("value_fc1.bias", [64]),
        new("value_fc2.weight", [1, 64]),
        new("value_fc2.bias", [1]),
    ];

    public static WeightSet Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var data = File.ReadAllBytes(path);
        if (data.Length < 28 + SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("Weight export is too short.");
        }

        var payload = data.AsSpan(0, data.Length - SHA256.HashSizeInBytes);
        var storedChecksum = data.AsSpan(payload.Length, SHA256.HashSizeInBytes);
        var calculatedChecksum = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(storedChecksum, calculatedChecksum))
        {
            throw new InvalidDataException("Weight export SHA-256 checksum mismatch.");
        }

        var reader = new SpanReader(payload);
        if (!reader.ReadBytes(Magic.Length, "magic").SequenceEqual(Magic))
        {
            throw new InvalidDataException("Weight export magic mismatch.");
        }

        var schema = reader.ReadUInt32("schema version");
        if (schema != SchemaVersion)
        {
            throw new InvalidDataException($"Unsupported weight schema version {schema}.");
        }

        var metadataLength = reader.ReadUInt32("metadata length");
        var tensorCount = reader.ReadUInt32("tensor count");
        var declaredWeightBytes = reader.ReadUInt64("weight byte count");
        if (metadataLength != CanonicalMetadataBytes.Length)
        {
            throw new InvalidDataException("Architecture metadata length is not canonical.");
        }

        if (tensorCount != ExpectedTensors.Length)
        {
            throw new InvalidDataException("Tensor count does not match the expected architecture.");
        }

        if (declaredWeightBytes != ExpectedWeightBytes)
        {
            throw new InvalidDataException("Weight byte count does not match the expected architecture.");
        }

        if (!reader.ReadBytes((int)metadataLength, "architecture metadata")
            .SequenceEqual(CanonicalMetadataBytes))
        {
            throw new InvalidDataException("Architecture metadata does not match the expected model.");
        }

        var tensors = new Dictionary<string, float[]>(ExpectedTensors.Length, StringComparer.Ordinal);
        long countedWeightBytes = 0;
        for (var tensorIndex = 0; tensorIndex < ExpectedTensors.Length; tensorIndex++)
        {
            var nameLength = reader.ReadUInt16($"tensor {tensorIndex} name length");
            var rank = reader.ReadByte($"tensor {tensorIndex} rank");
            var reserved = reader.ReadByte($"tensor {tensorIndex} reserved byte");
            var elementCount = reader.ReadUInt64($"tensor {tensorIndex} element count");
            if (reserved != 0)
            {
                throw new InvalidDataException($"Tensor {tensorIndex} has non-zero reserved flags.");
            }

            if (rank is not (1 or 2))
            {
                throw new InvalidDataException($"Tensor {tensorIndex} has unsupported rank {rank}.");
            }

            string name;
            try
            {
                name = StrictUtf8.GetString(reader.ReadBytes(nameLength, $"tensor {tensorIndex} name"));
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"Tensor {tensorIndex} name is not valid UTF-8.", exception);
            }

            if (tensors.ContainsKey(name))
            {
                throw new InvalidDataException($"Duplicate tensor '{name}'.");
            }

            var expected = ExpectedTensors[tensorIndex];
            if (!string.Equals(name, expected.Name, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Tensor {tensorIndex} is '{name}', expected '{expected.Name}'.");
            }

            var shape = new int[rank];
            for (var dimension = 0; dimension < rank; dimension++)
            {
                var size = reader.ReadUInt32($"{name} dimension {dimension}");
                if (size > int.MaxValue)
                {
                    throw new InvalidDataException($"{name} has an oversized dimension.");
                }

                shape[dimension] = (int)size;
            }

            if (!shape.SequenceEqual(expected.Shape))
            {
                throw new InvalidDataException(
                    $"{name} has shape [{string.Join(',', shape)}], " +
                    $"expected [{string.Join(',', expected.Shape)}].");
            }

            if (elementCount != (ulong)expected.ElementCount)
            {
                throw new InvalidDataException($"{name} element count does not match its shape.");
            }

            var values = new float[expected.ElementCount];
            for (var index = 0; index < values.Length; index++)
            {
                var bits = reader.ReadUInt32($"{name} data");
                var value = BitConverter.Int32BitsToSingle(unchecked((int)bits));
                if (!float.IsFinite(value))
                {
                    throw new InvalidDataException($"{name} contains a non-finite FP32 value.");
                }

                values[index] = value;
            }

            tensors.Add(name, values);
            countedWeightBytes += values.Length * sizeof(float);
        }

        if (countedWeightBytes != (long)declaredWeightBytes)
        {
            throw new InvalidDataException("Parsed tensor bytes do not match the declared weight bytes.");
        }

        if (reader.Remaining != 0)
        {
            throw new InvalidDataException("Weight export contains trailing payload bytes.");
        }

        return new WeightSet(
            tensors,
            (int)metadataLength,
            countedWeightBytes,
            data.LongLength,
            Convert.ToHexString(storedChecksum).ToLowerInvariant());
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public int Remaining => _data.Length - _offset;

        public ReadOnlySpan<byte> ReadBytes(int count, string label)
        {
            if (count < 0 || count > Remaining)
            {
                throw new InvalidDataException($"Weight export ended while reading {label}.");
            }

            var value = _data.Slice(_offset, count);
            _offset += count;
            return value;
        }

        public byte ReadByte(string label) => ReadBytes(1, label)[0];

        public ushort ReadUInt16(string label) =>
            BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(sizeof(ushort), label));

        public uint ReadUInt32(string label) =>
            BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(sizeof(uint), label));

        public ulong ReadUInt64(string label) =>
            BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(sizeof(ulong), label));
    }
}
