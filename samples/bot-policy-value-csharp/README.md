# Policy/value C# bot

This offline-compatible `net8.0` sample runs the Python
`HierarchicalPolicyValueNet` directly in C# and uses its policy/value output in a
deterministic PUCT search. It depends only on the arena's Bot SDK, GameEngine,
and Contracts packages. `weights.bin` is the deterministic generation-0,
seed-0 model and is copied beside the built application.

## Build and test

From the repository root:

```powershell
.\scripts\pack-bot-sdk.ps1
dotnet build .\samples\bot-policy-value-csharp -c Release
dotnet run -c Release --project .\samples\bot-policy-value-csharp -- --self-test
dotnet run --project .\tools\UttArena.BotHarness -- `
  --conformance "dotnet samples/bot-policy-value-csharp/bin/Release/net8.0/bot-policy-value-csharp.dll"
```

The default search cap is 256 simulations. Set
`UTTARENA_PUCT_SIMULATIONS=128` (valid range 1–4096) to change it. Each move
uses at most 60% of the smaller of the protocol timeout and remaining deadline;
a tiny budget immediately returns the first authoritative legal move.

## Export weights

From `training/uttarena-ml`:

```powershell
# Reproduce the checked-in generation-0 file.
uv run uttarena-export ..\..\samples\bot-policy-value-csharp\weights.bin --seed 0

# Export a training checkpoint (full arena checkpoint or plain state_dict).
uv run uttarena-export .\candidate.bin --checkpoint .\data\checkpoints\generation-000015.pt
```

The exporter rejects missing/extra tensors, wrong names, shapes or dtypes, and
non-finite values. It writes to a temporary sibling, flushes it, and atomically
replaces the destination.

To export, build, self-test, and zip a My bots package without overwriting this
sample's `weights.bin`, use the repo-root deploy script (see
[`docs/ML_TRAINING.md`](../../docs/ML_TRAINING.md)):

```powershell
.\training\scripts\deploy-policy-value-bot.ps1 -Generation 5
```

## Binary weight format

Schema 1 is little-endian:

1. Eight-byte ASCII magic `UTTAPV01`.
2. `u32` schema version, `u32` architecture JSON length, `u32` tensor count,
   and `u64` raw weight byte count.
3. Sorted compact UTF-8 JSON containing the canonical architecture metadata.
4. For each tensor in `state_dict` order: a header containing `u16` name length,
   `u8` rank, a zero reserved byte, and `u64` element count; then the UTF-8 name,
   one `u32` per dimension, and row-major IEEE-754 FP32 data.
5. A 32-byte SHA-256 trailer over every preceding payload byte.

The C# loader checks the checksum before parsing, then requires the exact schema,
canonical metadata, 16 tensor names/order/shapes, 132,266 finite FP32 values,
529,064 weight bytes, zero reserved flags, no duplicate names, and no trailing
payload.

## Network diagnostic

`--network-eval [request.json]` reads one JSON object from the file or stdin:

```json
{"features":[567 flattened or nested FP32 values],"legalMask":[81 optional booleans]}
```

It writes one `{"logits":[...],"value":...}` object to stdout. With a mask,
illegal logits are the JSON strings `"-Infinity"` (System.Text.Json's explicit
named floating-point representation). This mode exists only for cross-language
golden tests; normal stdout is reserved exclusively for the bot protocol.
