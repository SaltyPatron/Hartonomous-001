# API Layer

**Status**: ✅ Complete

HTTP API exposing entity queries, traversal, recomposition, and monitoring. ASP.NET Core minimal APIs. No controllers.

---

## Technology Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| API style | Minimal APIs | No ceremony. Map delegates directly. |
| Serialization | System.Text.Json (source-generated) | No reflection. AOT-compatible. |
| Authentication | None (Phase 1). API key header (Phase 2). | Localhost-only initially. |
| Rate limiting | None | Single-user research system. |
| Caching | None | PostgreSQL shared_buffers is the cache. |
| Streaming | `IAsyncEnumerable<T>` → NDJSON or chunked transfer | Large traversals and recompositions. |
| Error format | RFC 7807 Problem Details | Standard. Built into ASP.NET Core. |
| Pagination | Keyset (cursor-based) | Stable under concurrent writes. No OFFSET. |

---

## Endpoint Catalog

### Entity Endpoints

#### `GET /api/entities/{id}`

Fetch a single entity by ID.

**Response** `200 OK`:
```json
{
  "entityId": 42,
  "entityTypeId": 3,
  "entityTypeName": "word_form",
  "hash": "a3f2b8c1...",
  "isAtom": true,
  "createdAt": "2025-01-15T10:30:00Z"
}
```

**Response** `404 Not Found`: RFC 7807 with `type: "entity-not-found"`.

---

#### `GET /api/entities/by-hash/{hash}`

Fetch entity by BLAKE3 hash (hex-encoded, 64 characters).

Same response shape as `GET /api/entities/{id}`.

---

#### `GET /api/entities?typeId={typeId}&cursor={cursor}&limit={limit}`

List entities by type. Keyset pagination.

**Query parameters**:

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| `typeId` | int | required | Entity type ID |
| `cursor` | long? | null | Last entity_id from previous page |
| `limit` | int | 100 | Page size (max 1000) |

**Response** `200 OK`:
```json
{
  "items": [ /* entity objects */ ],
  "nextCursor": 1042,
  "hasMore": true
}
```

**SQL**: `SELECT * FROM substrate.entity WHERE entity_type_id = @typeId AND entity_id > @cursor ORDER BY entity_id LIMIT @limit + 1`. Fetch limit+1 to determine `hasMore`.

---

#### `GET /api/entities/{id}/classifications`

All junction table data for an entity.

**Response** `200 OK`:
```json
{
  "entityId": 42,
  "pos": ["noun", "verb"],
  "language": ["eng"],
  "senses": [{"senseId": 7, "mu": 0.85}],
  "morphFeatures": ["Number=Sing", "Case=Nom"]
}
```

**Implementation**: Parallel queries to all junction tables. Results merged into a single response object. Empty arrays for junctions with no matching rows.

---

#### `GET /api/entities/{id}/physicalities`

All physicality records for an entity across all tiers.

**Response** `200 OK`:
```json
{
  "entityId": 42,
  "physicalities": [
    {
      "physicalityId": 99,
      "tier": "spectral",
      "geom": "POINTZM(0.5 0.3 0.7 1.0)",
      "metadata": {}
    }
  ]
}
```

**Geometry serialization**: PostGIS geometries serialized as WKT strings. The client parses WKT or requests GeoJSON via `Accept: application/geo+json` header (alternative serialization, same data).

---

### Edge Endpoints

#### `GET /api/entities/{id}/edges?direction={direction}&edgeTypeId={edgeTypeId}&cursor={cursor}&limit={limit}`

Edges connected to an entity.

**Query parameters**:

| Param | Type | Default | Description |
|-------|------|---------|-------------|
| `direction` | string | `"both"` | `"outbound"`, `"inbound"`, or `"both"` |
| `edgeTypeId` | int? | null | Filter by edge type |
| `cursor` | long? | null | Keyset cursor |
| `limit` | int | 100 | Page size (max 1000) |

**Response** `200 OK`:
```json
{
  "items": [
    {
      "edgeId": 101,
      "edgeTypeId": 5,
      "edgeTypeName": "hypernym",
      "hash": "b4c3d2e1...",
      "members": [
        {"entityId": 42, "roleId": 1, "roleName": "source", "ordinal": 0},
        {"entityId": 88, "roleId": 2, "roleName": "target", "ordinal": 1}
      ],
      "geom": "LINESTRINGZM(...)"
    }
  ],
  "nextCursor": 201,
  "hasMore": false
}
```

**SQL**: Uses `neighbors(entity_id, edge_type_id, max_hops)` function for traversal-aware edge lookup. For simple edge listing, `max_hops = 1`.

---

#### `GET /api/edges/{id}`

Single edge by ID with full member list.

Same item shape as above.

---

### Traversal Endpoints

#### `POST /api/traversal`

Execute a graph traversal from a seed entity.

**Request body**:
```json
{
  "seedEntityId": 42,
  "strategy": "a_star",
  "maxDepth": 5,
  "maxResults": 100,
  "edgeTypeFilter": [5, 7, 12],
  "entityTypeFilter": [3, 4],
  "minSignificance": 0.5,
  "arenaId": 1
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `seedEntityId` | long | yes | Starting entity |
| `strategy` | string | no | `"a_star"` (default), `"bfs"`, `"dfs"` |
| `maxDepth` | int | no | Maximum traversal hops (default 5) |
| `maxResults` | int | no | Maximum result entities (default 100) |
| `edgeTypeFilter` | int[]? | no | Only traverse these edge types |
| `entityTypeFilter` | int[]? | no | Only return these entity types |
| `minSignificance` | double? | no | Minimum Glicko-2 mu threshold |
| `arenaId` | int? | no | Significance arena for filtering |

**Response** `200 OK`:
```json
{
  "seed": {"entityId": 42, "entityTypeName": "word_form"},
  "paths": [
    {
      "target": {"entityId": 88, "entityTypeName": "synset"},
      "cost": 0.42,
      "steps": [
        {"entityId": 42, "edgeId": 101, "edgeTypeName": "hypernym"},
        {"entityId": 65, "edgeId": 103, "edgeTypeName": "hypernym"},
        {"entityId": 88, "edgeId": null, "edgeTypeName": null}
      ]
    }
  ],
  "stats": {
    "nodesVisited": 234,
    "edgesTraversed": 567,
    "durationMs": 12
  }
}
```

**Implementation**: Delegates to `ITraversal.TraverseAsync`. The A* heuristic uses Glicko-2 significance as edge weight (lower mu = higher cost). `O(K × B × log N)` complexity.

---

#### `GET /api/traversal/stream` (SSE)

Server-Sent Events stream for long-running traversals. Same query parameters as POST body (passed as query string).

Each event is a `TraversalStep` JSON object. Client receives steps as they are discovered. Stream ends with a `stats` event.

```
event: step
data: {"entityId": 65, "edgeId": 103, "edgeTypeName": "hypernym", "depth": 1}

event: step
data: {"entityId": 88, "edgeId": null, "edgeTypeName": null, "depth": 2}

event: stats
data: {"nodesVisited": 234, "edgesTraversed": 567, "durationMs": 12}
```

---

### Recomposition Endpoints

#### `POST /api/recompose`

Recompose an entity into its target format.

**Request body**:
```json
{
  "entityId": 42,
  "format": "text",
  "maxDepth": null,
  "includeAnnotations": false
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `entityId` | long | yes | Root entity to recompose |
| `format` | string | yes | `"text"`, `"image"`, `"audio"`, `"video"`, `"safetensors"` |
| `maxDepth` | int? | no | Traversal depth limit (null = full) |
| `includeAnnotations` | bool | no | Text only: include edge annotations |

**Response for text** `200 OK` (`Content-Type: text/plain`):
```
The cat sat on the mat.
```

**Response for binary formats** `200 OK`:
- Image: `Content-Type: image/png` — PNG byte stream.
- Audio: `Content-Type: audio/wav` — WAV byte stream.
- Video: `Content-Type: video/x-yuv4mpeg2` — Y4M byte stream.
- SafeTensors: `Content-Type: application/octet-stream` — safetensors binary.

All binary responses use chunked transfer encoding via `RecomposeToStreamAsync`.

---

### Significance Endpoints

#### `GET /api/significance/{entityId1}/{entityId2}?arenaId={arenaId}`

Significance rating between two entities in a specific arena.

**Response** `200 OK`:
```json
{
  "entityId1": 42,
  "entityId2": 88,
  "arenaId": 1,
  "arenaName": "semantic_similarity",
  "mu": 1523.4,
  "phi": 45.2,
  "sigma": 0.06,
  "lastUpdated": "2025-01-15T10:30:00Z"
}
```

**Response** `404 Not Found`: No significance record exists for this pair in this arena.

---

#### `GET /api/significance/{entityId}/neighbors?arenaId={arenaId}&limit={limit}`

Top-N most significant neighbors of an entity in a given arena, ordered by mu descending.

**Response** `200 OK`:
```json
{
  "entityId": 42,
  "arenaId": 1,
  "neighbors": [
    {"entityId": 88, "mu": 1523.4, "phi": 45.2},
    {"entityId": 65, "mu": 1498.1, "phi": 52.3}
  ]
}
```

**SQL**: `SELECT * FROM substrate.significance WHERE entity_id_1 = @id AND arena_id = @arena ORDER BY mu DESC LIMIT @limit`.

---

### Monitoring Endpoints

#### `GET /api/monitor/health`

Substrate health check.

**Response** `200 OK`:
```json
{
  "status": "healthy",
  "database": "connected",
  "entityCount": 15234567,
  "edgeCount": 42345678,
  "schemaVersion": "0022",
  "uptime": "3d 14h 22m"
}
```

**Implementation**: Delegates to `IHealthCheck.GetHealthAsync`. Returns `503 Service Unavailable` if database is unreachable.

---

#### `GET /api/monitor/ingestion`

Current ingestion status across all phases.

**Response** `200 OK`:
```json
{
  "phases": [
    {
      "phaseCode": "ucd_uca",
      "status": "completed",
      "entitiesIngested": 150000,
      "edgesCreated": 150000,
      "startedAt": "2025-01-15T08:00:00Z",
      "completedAt": "2025-01-15T08:05:00Z",
      "errorMessage": null
    },
    {
      "phaseCode": "wordnet_omw",
      "status": "running",
      "entitiesIngested": 234567,
      "edgesCreated": 345678,
      "startedAt": "2025-01-15T08:05:00Z",
      "completedAt": null,
      "errorMessage": null
    }
  ]
}
```

**SQL**: `SELECT * FROM monitor.phase_status ORDER BY started_at`.

---

#### `GET /api/monitor/progress/{phaseCode}`

Detailed progress for a specific phase.

**Response** `200 OK`:
```json
{
  "phaseCode": "wordnet_omw",
  "decomposerCode": "wordnet",
  "status": "running",
  "batchNumber": 47,
  "entitiesIngested": 234567,
  "entitiesPerSecond": 12345.6,
  "startedAt": "2025-01-15T08:05:00Z",
  "lastBatchAt": "2025-01-15T08:07:23Z"
}
```

---

## Application Setup

### Program.cs (API)

```csharp
var builder = WebApplication.CreateBuilder(args);

// Database
var connString = builder.Configuration.GetConnectionString("Substrate");
var dataSource = new NpgsqlDataSourceBuilder(connString)
    .UseNetTopologySuite()
    .Build();
builder.Services.AddSingleton(dataSource);

// Services (same registrations as CLI, minus phase runner)
builder.Services.AddSingleton<IIngestionPipeline, IngestionPipeline>();
builder.Services.AddSingleton<ITraversal, AStarTraversal>();
builder.Services.AddSingleton<IHealthCheck, SubstrateHealthCheck>();
builder.Services.AddScoped<TextRecomposer>();
builder.Services.AddScoped<ImageRecomposer>();
builder.Services.AddScoped<AudioRecomposer>();
builder.Services.AddScoped<VideoRecomposer>();
builder.Services.AddScoped<SafetensorsRecomposer>();

// JSON
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// Map endpoints (see below)
app.MapEntityEndpoints();
app.MapEdgeEndpoints();
app.MapTraversalEndpoints();
app.MapRecompositionEndpoints();
app.MapSignificanceEndpoints();
app.MapMonitorEndpoints();

app.Run();
```

### Endpoint Registration

Each endpoint group is an extension method on `IEndpointRouteBuilder`:

```csharp
internal static class EntityEndpoints
{
    internal static void MapEntityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/entities/{id}", GetEntityById);
        app.MapGet("/api/entities/by-hash/{hash}", GetEntityByHash);
        app.MapGet("/api/entities", ListEntities);
        app.MapGet("/api/entities/{id}/classifications", GetClassifications);
        app.MapGet("/api/entities/{id}/physicalities", GetPhysicalities);
    }

    private static async Task<IResult> GetEntityById(
        long id,
        NpgsqlDataSource db,
        CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT e.entity_id, e.entity_type_id, et.code, e.hash, e.is_atom, e.created_at " +
            "FROM substrate.entity e JOIN substrate.entity_type et ON e.entity_type_id = et.entity_type_id " +
            "WHERE e.entity_id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return Results.Problem("Entity not found", statusCode: 404, type: "entity-not-found");
        return Results.Ok(new { /* map columns */ });
    }
}
```

### Connection Management

Same `NpgsqlDataSource` singleton as CLI. Pool size 10. No separate pool for API. If CLI and API run simultaneously (different processes), each gets its own pool — PostgreSQL handles concurrent connections.

### Error Responses

All errors use RFC 7807 Problem Details via `Results.Problem()`:

```json
{
  "type": "entity-not-found",
  "title": "Entity not found",
  "status": 404,
  "detail": "No entity with ID 42 exists.",
  "instance": "/api/entities/42"
}
```

Unhandled exceptions → `500` with generic problem details (no stack trace in response body). Stack trace goes to structured log only.

---

## Endpoint Index

| Method | Path | Description | Response Type |
|--------|------|------------|--------------|
| GET | `/api/entities/{id}` | Entity by ID | JSON |
| GET | `/api/entities/by-hash/{hash}` | Entity by hash | JSON |
| GET | `/api/entities` | List by type (paginated) | JSON |
| GET | `/api/entities/{id}/classifications` | Junction data | JSON |
| GET | `/api/entities/{id}/physicalities` | Physicality records | JSON |
| GET | `/api/entities/{id}/edges` | Connected edges | JSON |
| GET | `/api/edges/{id}` | Single edge | JSON |
| POST | `/api/traversal` | Execute traversal | JSON |
| GET | `/api/traversal/stream` | Streaming traversal | SSE |
| POST | `/api/recompose` | Recompose entity | Text/Binary |
| GET | `/api/significance/{id1}/{id2}` | Pairwise significance | JSON |
| GET | `/api/significance/{id}/neighbors` | Top-N neighbors | JSON |
| GET | `/api/monitor/health` | Health check | JSON |
| GET | `/api/monitor/ingestion` | Ingestion status | JSON |
| GET | `/api/monitor/progress/{phase}` | Phase progress | JSON |
