CREATE TYPE substrate.edge_ref AS (
    edge_type_id INT,
    edge_hash    substrate.hash_value
);
COMMENT ON TYPE substrate.edge_ref IS
    'Composite edge reference: identity surface for substrate.edge. Used in significance updates and traversal results.';
