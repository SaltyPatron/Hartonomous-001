-- Partition for unicode edge_types. IDs 32..34 are the original core UCD
-- edges; IDs 96..112 are appended structural Unicode surfaces so existing
-- model/semantic partitions keep stable IDs.
CREATE TABLE substrate.edge_unicode
    PARTITION OF substrate.edge FOR VALUES IN (
        32, 33, 34,
        96, 97, 98, 99, 100, 101, 102, 103, 104,
        105, 106, 107, 108, 109, 110, 111, 112
    );
