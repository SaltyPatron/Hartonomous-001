# Image Modality Decomposer Specification

## Identity

- **Decomposer class**: `ImageDecomposer` extends `BaseDecomposer`
- **Purpose**: Decompose any raster image into the substrate. Pixel values, spatial structure, color properties, and all analysis pass results stored as entities and edges.
- **Dependency**: UCD seed (codepoints for numeric value compositions). Type system (image-specific types registered).

## Decomposition Pipeline

### Level 0: Format Structure Decomposition

1. Detect image format (PNG, JPEG, WebP, BMP, TIFF, etc.) from header bytes.
2. **Structurally decompose the format itself** into typed entities:
   - **JPEG**: JFIF/EXIF headers, APP markers, quantization tables (DQT), Huffman tables (DHT), start-of-frame parameters (SOF), scan segments (SOS), restart markers, comment markers. Each is a typed entity. The JPEG file is a composition whose sequence references these structural entities in order.
   - **PNG**: signature, IHDR, PLTE, IDAT (compressed data chunks), tEXt/iTXt metadata, IEND. Each chunk is a typed entity.
   - **TIFF**: IFD entries, strip/tile offsets, compression parameters. Each IFD entry is an edge.
   - **BMP/WebP/etc.**: analogous structural decomposition per format.
3. Decode to raw pixel grid: width x height x channels. The pixel grid is derived from the structural entities (for JPEG, by decoding DCT coefficients from the scan segments using the quantization and Huffman tables).
4. Record format metadata: dimensions, color space (RGB, RGBA, grayscale, CMYK), bit depth, ICC profile if present.
5. Format metadata stored as edges on the image entity.

### Level 1: Pixel Values as Codepoint Compositions

Each pixel value is a number. Numbers decompose to codepoint compositions via the cascade compression mechanism.

- RGB pixel `(255, 128, 0)`: three number compositions `255`, `128`, `0` (each a composition of digit codepoints).
- `255` = `[("2", x1), ("5", x2)]`. Shared with every other `255` in the substrate.
- The RGB triple `(255, 128, 0)` is a composition of three number compositions. Shared with every pixel that has this exact color.

### Level 2: Spatial Composition

Pixels compose into spatial structures:

1. **Row composition**: each row is a sequence of pixel value compositions with RLE. A row of 100 identical blue pixels = one reference with count=100.
2. **Region composition**: contiguous areas of identical or similar color compose into region entities.
3. **Patch composition**: fixed-size patches (e.g., 8x8, 16x16) for frequency-domain analysis and model-compatible tiling.

### Level 3: Color Space Decomposition

Multiple color space representations stored as edges:

- RGB values as compositions (primary representation).
- HSV (hue, saturation, value) derived and stored as edges.
- Lab (perceptual color space) derived and stored.
- Each color space representation is a separate typed physicality on the pixel/region entity.

### Level 4: Ingestion-Time Analysis Passes

All pre-computed. All stored as edges. A query about any of these is a lookup, not a computation.

- `EdgeDetectionPass` (Sobel/Canny) -- edge pixels as typed entities with orientation and magnitude. Stored as spatial compositions.
- `TextureDescriptorPass` (LBP, Gabor) -- local texture patterns per region. Each unique texture pattern is an entity.
- `HOGPass` (Histogram of Oriented Gradients) -- per-patch gradient histograms. Each histogram is a composition.
- `DCTPass` (Discrete Cosine Transform) -- frequency-domain representation per patch. DCT coefficients as compositions.
- `ConnectedComponentPass` -- connected regions by color/threshold. Each component is a spatial composition entity.
- `ContourPass` -- contour hierarchies (outlines of objects/regions). Each contour is a LinestringZM entity (spatial curve).
- `ColorHistogramPass` -- global and per-region color distributions. Each histogram is a composition.
- `PerceptualHashPass` -- perceptual hash for near-duplicate detection across images. Hash stored as entity for O(1) lookup.

### Level 5: Physicality

- Pixel positions as PointZM: X=column, Y=row, Z=channel (or composite), M=significance.
- Patches as spatial compositions with centroid.
- Contours as LinestringZM (spatial curves through pixel space).
- Region boundaries as polygon geometries (if PostGIS polygon support is used).
- Image-level centroid derived from constituent spatial compositions.

### Level 6: Cross-Modal Relations

If the image has associated text (filename, caption, EXIF metadata, alt text):
- Text decomposed by `TextDecomposer`.
- Cross-modal edges created between text entities and image spatial entities.
- Model-derived object labels (from detection model edges in the substrate) relate to spatial regions.

## Cascade Compression in Practice

A photograph of a blue sky:
- Millions of pixels with near-identical sky-blue values.
- `(135, 206, 235)` -- one composition shared by every pixel that has this color.
- Rows of identical-color pixels compress via RLE: one reference with count = pixels in run.
- Regions of uniform sky compress into one region entity referencing one color composition.
- The storage for 1 million sky pixels is approximately: one color composition + one region entity with area metadata. NOT 1 million pixel records.

## Round-Trip

`ImageRecomposer` reconstructs the original image:
1. Read format metadata (dimensions, color space, bit depth, target format).
2. Walk spatial composition sequence to reconstruct pixel grid.
3. Decompress RLE sequences to fill pixel runs.
4. Encode pixel grid to target format.
5. Byte-compare against original.

For lossy formats (JPEG): the decomposer structurally decomposes the format itself — JFIF/EXIF headers, APP markers, quantization tables (DQT), Huffman tables (DHT), start-of-frame parameters (SOF), scan segments (SOS), and DCT coefficient blocks — into typed entities in the Merkle DAG. The pixel grid is derived from these structural entities. Round-trip means the recomposer walks the structural entity tree and reconstructs a valid JPEG: headers, tables, and encoded scan data, all from substrate entities. No binary blobs stored. No "original bytes on the side."
