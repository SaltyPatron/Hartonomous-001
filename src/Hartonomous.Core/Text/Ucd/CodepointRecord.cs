using System.Collections.Generic;

namespace Hartonomous.Core.Text.Ucd;

/// <summary>
/// One codepoint's worth of UAX #44 properties, extracted from
/// ucd.all.flat.xml. The substrate cares about the semantic content,
/// not the XML structure — this record is the bridge between the
/// parser and IIngestionBatch.AddEntity/AddEdge emissions.
/// </summary>
public sealed record CodepointRecord(
    int Codepoint,
    bool Assigned,
    CodepointAttributes Attributes,
    IReadOnlyList<NameAlias>? NameAliases);
