COPY pg_temp.junction_inflight (table_name, entity_hash, ref_id, attestation_type_id, mu) FROM STDIN (FORMAT binary)
