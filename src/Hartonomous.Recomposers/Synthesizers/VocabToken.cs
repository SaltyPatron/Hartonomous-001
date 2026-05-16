namespace Hartonomous.Recomposers.Synthesizers;

/// <summary>
/// One row in the target model's vocabulary, sourced from the substrate.
/// <see cref="EntityHash"/> is the BLAKE3 of the word_form's content bytes
/// (canonical text decomposer output); <see cref="TokenText"/> is the
/// surface form for tokenizer.json; <see cref="EdgeCount"/> is the
/// substrate-measured prominence (used to rank for vocab selection); the
/// 4D centroid (<see cref="CentroidX"/>..<see cref="CentroidM"/>) is the
/// word_form's representative 4D position read out of the s3_position
/// physicality partition.
/// </summary>
public sealed record VocabToken(
    int Index,
    byte[] EntityHash,
    string TokenText,
    long EdgeCount,
    double CentroidX,
    double CentroidY,
    double CentroidZ,
    double CentroidM);
