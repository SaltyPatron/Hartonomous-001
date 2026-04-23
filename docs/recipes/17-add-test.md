# Recipe 17: Add a Test

Intent: add a test of the right kind, at the right layer, with the right tools.

Three kinds of tests, three locations, three rule sets.

---

## Test taxonomy

| Kind | Project | Location | Dependencies | Speed |
|---|---|---|---|---|
| **Unit** | `tests/Hartonomous.{Project}.Tests` | One file per type-under-test | None — hand-written fakes only | Milliseconds |
| **Integration** | `tests/Hartonomous.Integration.Tests` | One file per scenario | Real PostgreSQL container (Testcontainers); native libs loaded | Seconds |
| **Contract** | `tests/Hartonomous.Contract.Tests` | One file per contract | The contract surface only (in-memory or real, depends on contract) | Seconds |
| **Native (C/C++)** | `ext/libhartonomous/tests/` | One file per module | Google Test | Milliseconds |
| **PG regression** | `ext/hartonomous_pg/test/sql/` | One SQL file per scenario | Real PostgreSQL with extension | Seconds |

---

## Unit test pattern

`tests/Hartonomous.Decomposers.Tests/Tatoeba/TatoebaDecomposerTests.cs`:

```csharp
namespace Hartonomous.Decomposers.Tests.Tatoeba;

public class TatoebaDecomposerTests
{
    [Fact]
    public async Task Decompose_OneSentence_SubmitsContentAndMetadata()
    {
        // Arrange
        var pipeline = new FakeIngestionPipeline();
        var reader = new FakeTatoebaReader([
            new TatoebaRecord(Id: 1, Text: "Hello.", LanguageId: 1, Translations: [], Audio: null)
        ]);
        var decomposer = new TatoebaDecomposer(pipeline, reader, NullLogger<TatoebaDecomposer>.Instance);

        // Act
        await decomposer.DecomposeAsync(NullProgressReporter.Instance, CancellationToken.None);

        // Assert
        pipeline.SubmittedContent.Should().HaveCount(1);
        pipeline.SubmittedContent[0].Modality.Should().Be(ModalityCode.Text);
        pipeline.SubmittedJunctions.Should().Contain(j =>
            j.JunctionCode == JunctionCode.EntityLanguage && j.ClassId == 1);
    }
}
```

### Hand-written fake (no Moq)

`tests/Hartonomous.Decomposers.Tests/Fakes/FakeIngestionPipeline.cs`:

```csharp
internal sealed class FakeIngestionPipeline : IIngestionPipeline
{
    public List<SubmittedContent> SubmittedContent { get; } = new();
    public List<EdgeSpec> SubmittedEdges { get; } = new();
    public List<JunctionSpec> SubmittedJunctions { get; } = new();
    public List<PhysicalitySpec> SubmittedPhysicality { get; } = new();
    public bool Flushed { get; private set; }

    private long _nextHash = 1;

    public Task<EntityHash> SubmitContentAsync(
        ReadOnlyMemory<byte> content, ModalityCode modality, ProvenanceCode provenance, CancellationToken ct)
    {
        var hash = new EntityHash(BitConverter.GetBytes(_nextHash++));
        SubmittedContent.Add(new SubmittedContent(content.ToArray(), modality, provenance, hash));
        return Task.FromResult(hash);
    }

    public Task SubmitEdgesAsync(IReadOnlyList<EdgeSpec> edges, CancellationToken ct)
    {
        SubmittedEdges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task SubmitJunctionsAsync(IReadOnlyList<JunctionSpec> junctions, CancellationToken ct)
    {
        SubmittedJunctions.AddRange(junctions);
        return Task.CompletedTask;
    }

    public Task SubmitPhysicalityAsync(IReadOnlyList<PhysicalitySpec> physicality, CancellationToken ct)
    {
        SubmittedPhysicality.AddRange(physicality);
        return Task.CompletedTask;
    }

    public Task FlushAsync(CancellationToken ct) { Flushed = true; return Task.CompletedTask; }
}

internal sealed record SubmittedContent(byte[] Bytes, ModalityCode Modality, ProvenanceCode Provenance, EntityHash AssignedHash);
```

Rules for unit tests:
- One assertion concept per test.
- Test name format: `Method_Scenario_ExpectedResult`.
- Use FluentAssertions (`result.Should().Be(...)`).
- No real DB, no real files, no network, no Moq.

---

## Integration test pattern

`tests/Hartonomous.Integration.Tests/TatoebaIntegrationTests.cs`:

```csharp
[Collection("Integration")]  // shares container fixture
public class TatoebaIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Ingest_Fixture_ProducesExpectedSubstrateState()
    {
        // Arrange
        var fixture = "tests/Hartonomous.Integration.Tests/Fixtures/tatoeba/small.tsv";

        // Act
        await RunCliAsync("ingest-tatoeba", "--path", fixture);

        // Assert via direct DB query.
        var sentenceCount = await Query<int>(
            "SELECT count(*) FROM substrate.entity_text WHERE entity_type_id = $1",
            (int)EntityTypeCode.TatoebaSentence);
        sentenceCount.Should().Be(50);  // fixture has 50 sentences

        // Assert idempotency — re-ingesting produces zero new rows.
        var initialCount = await Query<int>("SELECT count(*) FROM substrate.entity");
        await RunCliAsync("ingest-tatoeba", "--path", fixture);
        var finalCount = await Query<int>("SELECT count(*) FROM substrate.entity");
        finalCount.Should().Be(initialCount);
    }
}
```

`IntegrationTestBase` provides:
- A shared Testcontainers PostgreSQL with PostGIS + hartonomous extension.
- Migration applied at fixture startup.
- `RunCliAsync(...)` — invokes the CLI command in-process.
- `Query<T>(...)` — runs a SQL query against the test DB.
- DB cleanup between tests (TRUNCATE substrate tables, leaving reference seed intact).

Rules for integration tests:
- Marked with `[Collection("Integration")]` to share the container.
- Always include an idempotency assertion (re-running produces zero new rows — Law #6).
- Fixtures live in `tests/Hartonomous.Integration.Tests/Fixtures/`.
- Slow tests (> 30s) marked `[Trait("Speed", "Slow")]` so they can be excluded in dev loops.

---

## Contract test pattern

Contract tests verify that any implementation of an interface obeys the contract. Used when multiple implementations exist (e.g., `IIngestionPipeline` has `NpgsqlIngestionPipeline` and a future `InMemoryIngestionPipeline`).

`tests/Hartonomous.Contract.Tests/Ingestion/IngestionPipelineContractTests.cs`:

```csharp
public abstract class IngestionPipelineContractTests
{
    protected abstract Task<IIngestionPipeline> CreateAsync(CancellationToken ct);

    [Fact]
    public async Task SubmitContent_SameBytesTwice_ReturnsSameHash()
    {
        var pipeline = await CreateAsync(CancellationToken.None);
        var bytes = "Hello."u8.ToArray();
        var first = await pipeline.SubmitContentAsync(bytes, ModalityCode.Text, ProvenanceCode.UserSession, CancellationToken.None);
        var second = await pipeline.SubmitContentAsync(bytes, ModalityCode.Text, ProvenanceCode.UserSession, CancellationToken.None);
        first.Should().Be(second);
    }

    // ... more contract assertions
}

public class NpgsqlIngestionPipelineContractTests : IngestionPipelineContractTests
{
    protected override async Task<IIngestionPipeline> CreateAsync(CancellationToken ct) =>
        await TestPipelineFactory.CreateNpgsqlAsync(ct);
}

public class InMemoryIngestionPipelineContractTests : IngestionPipelineContractTests
{
    protected override Task<IIngestionPipeline> CreateAsync(CancellationToken ct) =>
        Task.FromResult<IIngestionPipeline>(new InMemoryIngestionPipeline());
}
```

The base class holds the contract assertions; each implementation gets a derived class. New implementations get the contract test for free.

---

## Native test pattern

`ext/libhartonomous/tests/test_blake3.c`:

```c
#include "hartonomous/blake3.h"
#include <gtest/gtest.h>

TEST(Blake3Test, KnownVector_MatchesExpected) {
    const uint8_t input[] = "abc";
    uint8_t output[32];
    EXPECT_EQ(HTNS_OK, htns_blake3_hash(input, 3, output));
    // Compare to known BLAKE3 test vector for "abc"
    const uint8_t expected[32] = { 0x64, 0x37, 0xb3, 0xac, /* ... */ };
    EXPECT_EQ(0, memcmp(output, expected, 32));
}

TEST(Blake3Test, Determinism_ByteIdentical) {
    const uint8_t input[] = "Hello, World!";
    uint8_t a[32], b[32];
    htns_blake3_hash(input, 13, a);
    htns_blake3_hash(input, 13, b);
    EXPECT_EQ(0, memcmp(a, b, 32));
}
```

Register in `ext/libhartonomous/tests/CMakeLists.txt`:

```cmake
add_executable(test_blake3 test_blake3.c)
target_link_libraries(test_blake3 PRIVATE hartonomous gtest_main)
add_test(NAME test_blake3 COMMAND test_blake3)
```

---

## PG regression test pattern

`ext/hartonomous_pg/test/sql/blake3.sql`:

```sql
SELECT encode(hartonomous.blake3_hash('abc'), 'hex');
```

`ext/hartonomous_pg/test/expected/blake3.out`:

```
                              encode
------------------------------------------------------------------
 6437b3ac38465133ffb63b75273a8db78c54f1c75d7a2c1e8c54e3f1a87a8f...
(1 row)
```

Run with `pwsh scripts/test/PgRegress.ps1 -Filter blake3`.

---

## Anti-patterns

- **DON'T** use Moq. Hand-written fakes only. They're documented, debuggable, and don't fail in mysterious ways.
- **DON'T** depend on external files in unit tests. Inline test data; create temp files via `Path.GetTempFileName()` if necessary.
- **DON'T** depend on a real DB in unit tests. Use a fake or move the test to integration.
- **DON'T** sleep in tests. Await the actual signal; use `WaitAsync(TimeSpan)` for timeouts.
- **DON'T** assert without a clear message. FluentAssertions provides automatic messages — use it.
- **DON'T** test multiple unrelated behaviors in one test. One test, one assertion concept.
- **DON'T** skip the determinism assertion for compute primitives. Law #6 must be tested.
- **DON'T** skip the idempotency assertion for ingestion. Re-running produces zero new rows.

---

## Verification checklist

- [ ] Test class follows `{TypeUnderTest}Tests` naming
- [ ] Test method follows `Method_Scenario_ExpectedResult` naming
- [ ] No Moq references
- [ ] No external file/network/DB dependencies in unit tests
- [ ] Integration tests marked with `[Collection("Integration")]`
- [ ] Determinism / idempotency assertions present where applicable
- [ ] Test runs deterministically (passes consistently across runs)
- [ ] FluentAssertions used for assertions

---

## Related recipes

- `08-add-decomposer.md` — what a decomposer test looks like
- `10-add-recomposer.md` — round-trip integration tests
- `14-add-native-operator.md` — native test pattern
- `15-add-pinvoke-surface.md` — facade-level determinism tests
