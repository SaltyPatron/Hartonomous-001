# C/C++ Standards

## API Surface

The shared library (`libhartonomous`) exposes a flat C API. No C++ classes, templates, or STL types in the public header. C linkage only.

```c
// hartonomous.h — public API
#ifdef _WIN32
  #define HTNS_API __declspec(dllexport)
#else
  #define HTNS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

HTNS_API int htns_blake3_hash(const uint8_t* input, size_t len, uint8_t output[32]);
HTNS_API int htns_blake3_hasher_init(htns_blake3_hasher** out);
HTNS_API int htns_blake3_hasher_update(htns_blake3_hasher* h, const uint8_t* data, size_t len);
HTNS_API int htns_blake3_hasher_finalize(htns_blake3_hasher* h, uint8_t output[32]);
HTNS_API void htns_blake3_hasher_free(htns_blake3_hasher* h);

#ifdef __cplusplus
}
#endif
```

## Memory Rules

- Caller allocates output buffers for fixed-size results (`uint8_t output[32]`).
- Library allocates for variable-size results; library provides a matching `_free` function.
- Every allocation path has a documented deallocation path.
- PG extension functions use `palloc`/`pfree` (PostgreSQL memory contexts). Never `malloc` inside PG extension code.

## Error Returns

All functions return `int`. `0` = success. Nonzero = error code from a documented enum. No exceptions cross the C boundary. No `errno` reliance.
