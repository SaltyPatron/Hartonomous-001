# Testing Standards

## Every Interface Gets a Test

If it has an interface, it has a test that verifies the contract. Not the implementation — the contract. The test should pass for any correct implementation.

## Test Project Structure Mirrors Source

```
tests/
  Hartonomous.Core.Tests/
  Hartonomous.Decomposers.Tests/
  Hartonomous.Engine.Tests/
  Hartonomous.Api.Tests/
  Hartonomous.Native.Tests/
```

## Test Naming

`MethodName_Scenario_ExpectedResult`:

```csharp
[Fact]
public async Task UpsertEntityAsync_DuplicateHash_ReturnsExistingId()

[Fact]
public async Task DecomposeAsync_MissingSourceFile_ThrowsFileNotFoundException()

[Fact]
public void ComputeHash_EmptyInput_ReturnsKnownBlake3DigestOfEmpty()
```

## What Gets Mocked

Interfaces defined in Core. That's it. If you need to mock something that isn't an interface, the design is wrong — refactor until it is.

## What Gets Integration Tested

Database interactions. Extension function calls. End-to-end ingestion of a small known dataset. These use a real PostgreSQL instance (Testcontainers or Docker fixture), not mocked SQL.
