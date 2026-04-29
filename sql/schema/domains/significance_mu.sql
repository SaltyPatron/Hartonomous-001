CREATE DOMAIN substrate.significance_mu AS FLOAT8;
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Wide-band: trust priors 20K (user_session) to 100K (authoritative_standard); arena-specific overrides via provenance_edge_authority can exceed source defaults. Values evolve via comparison events. The COALESCE prior formula in the edge_significance view computes effective μ from (provenance × modality × edge_type semantic_weight × lineage decay).';
