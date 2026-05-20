# Recipe 15: Add a P/Invoke Surface

Intent: expose a function from `libhartonomous` to C# via P/Invoke, routed through the compute facade.

P/Invoke declarations live in `Hartonomous.Core/Native/`. Higher-level callers go through the compute facade (`Hartonomous.Core.Compute.*`), never the P/Invoke directly.

---

## Prerequisites

- The native function exists in `libhartonomous` (recipe `14-add-native-operator.md`).
- The header at `ext/libhartonomous/include/hartonomous/{module}.h` declares the function.
- The function returns an `htns_error_t` and uses caller-allocated buffers (no hidden mallocs).

---

## Steps

### 1. Add (or extend) the P/Invoke class

`src/Hartonomous.Core/Native/{Module}Native.cs`:

```csharp
namespace Hartonomous.Core.Native;

internal static partial class {Module}Native
{
    private const string LibraryName = "hartonomous";

    [LibraryImport(LibraryName, EntryPoint = "htns_{module}_{verb}")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial HtnsError {Verb}(
        ReadOnlySpan<byte> arg1,
        int arg2,
        Span<byte> outBuffer);
}
```

Use `LibraryImportAttribute` (source-generated, .NET 7+) over `DllImport`. It's faster, marshalling-safer, and AOT-friendly.

### 2. Declare struct types if needed

For each C struct exchanged, declare a matching C# struct with explicit layout:

`src/Hartonomous.Core/Native/{Module}Structs.cs`:

```csharp
namespace Hartonomous.Core.Native;

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct {Pascal}Native
{
    public double X;
    public double Y;
    public double Z;
    public double M;
}
```

`Pack = 8` matches the typical alignment in the C struct. If the C side uses different packing, match it explicitly.

### 3. Add the error enum (if not present)

`src/Hartonomous.Core/Native/HtnsError.cs`:

```csharp
namespace Hartonomous.Core.Native;

internal enum HtnsError
{
    Ok = 0,
    NullArg = 1,
    InvalidArg = 2,
    OutOfMemory = 3,
    AllocationFailed = 4,
    ConvergenceFailed = 5,
    UnsupportedDtype = 6,
    // ... mirror htns_error_t from C
}
```

The values must match the C enum exactly. If they drift, undefined behavior.

### 4. Wrap in the compute facade

P/Invoke is internal. External callers go through the facade. Add a method to the appropriate facade class:

`src/Hartonomous.Core/Compute/{Layer}/{Pascal}.cs` where `{Layer}` is `Common`, `Ingestion`, or `Inference`:

```csharp
namespace Hartonomous.Core.Compute.{Layer};

public static class {Pascal}
{
    public static {Result} {Verb}({Type1} arg1, {Type2} arg2)
    {
        // Convert managed args to unmanaged form.
        Span<byte> output = stackalloc byte[{OutputSize}];

        var err = {Module}Native.{Verb}(arg1.AsSpan(), arg2, output);

        // Translate error codes into typed exceptions.
        if (err != HtnsError.Ok)
            throw HtnsErrorMapper.Map(err, $"{nameof({Verb})} failed");

        // Convert unmanaged output to managed form.
        return DeserializeResult(output);
    }
}
```

`HtnsErrorMapper` translates `HtnsError` codes into exception types (`ComputeArgumentException`, `ComputeAllocationException`, `ComputeConvergenceException`, etc.).

### 5. Register the native DLL

The build system already copies `libhartonomous.{so|dll|dylib}` into the bin output of any project that depends on `Hartonomous.Core` via the rules in `native-dll.targets`. If you've added a NEW native library (not extended the existing one), update `native-dll.targets`.

### 6. Add tests

`tests/Hartonomous.Core.Tests/Compute/{Layer}/{Pascal}Tests.cs`:

```csharp
public class {Pascal}Tests
{
    [Fact]
    public void {Verb}_KnownInput_ReturnsExpectedResult()
    {
        var result = {Pascal}.{Verb}(knownInput, 5);
        result.Should().Be(expected);
    }

    [Fact]
    public void {Verb}_NullArg_Throws()
    {
        var act = () => {Pascal}.{Verb}(default, 5);
        act.Should().Throw<ComputeArgumentException>();
    }

    [Fact]
    public void {Verb}_Determinism_RepeatRunsByteIdentical()
    {
        var first = {Pascal}.{Verb}(knownInput, 5);
        var second = {Pascal}.{Verb}(knownInput, 5);
        second.Should().Be(first);
    }
}
```

Determinism tests are mandatory for compute-facade methods. Law #6.

### 7. Document

`docs/specs/native/shared-library.md` — add the function to the P/Invoke surface inventory.
`docs/specs/csharp/compute-facade.md` — add the facade method to the inventory.

### 8. Build and run

```pwsh
pwsh scripts/build/Native.ps1
pwsh scripts/build/Dotnet.ps1
pwsh scripts/test/Dotnet.ps1 -Filter {Pascal}Tests
```

---

## Anti-patterns

- **DON'T** declare P/Invoke outside `Hartonomous.Core.Native`. Other projects must go through the compute facade.
- **DON'T** use `DllImportAttribute` for new code. Use `LibraryImportAttribute` (source-generated, AOT-friendly).
- **DON'T** assume default struct layout. Always specify `[StructLayout]` explicitly with the correct `Pack`.
- **DON'T** marshal strings with `string`. Use `ReadOnlySpan<byte>` (UTF-8) and let the caller encode/decode.
- **DON'T** use `IntPtr` parameters for buffers. Use `Span<T>` / `ReadOnlySpan<T>` — they work with `LibraryImport` and avoid pinning issues.
- **DON'T** swallow `HtnsError` codes. Translate them into typed exceptions via `HtnsErrorMapper`.
- **DON'T** call into the native library before `Hartonomous.Core.Compute.Initialize()` has run. The init verifies CBWR=AUTO,STRICT, sets seeds, and asserts ISA support.

---

## Verification checklist

- [ ] P/Invoke declared in `Hartonomous.Core/Native/{Module}Native.cs` using `LibraryImportAttribute`
- [ ] Struct layouts explicit, matching the C side
- [ ] Error codes mirror `htns_error_t`
- [ ] Facade method exposes the function with managed types
- [ ] Errors translated to typed exceptions
- [ ] Determinism test passes
- [ ] Native DLL is copied to test bin output
- [ ] Inventory updated in shared-library.md and compute-facade.md

---

## Related recipes

- `14-add-native-operator.md` — adding the underlying native function
