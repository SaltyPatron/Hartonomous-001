namespace Hartonomous.Decomposers.Tatoeba;

internal sealed record TatoebaAudioRow(
    int SentenceId,
    int AudioId,
    string Contributor,
    string License = "",
    string AttributionUrl = "");
