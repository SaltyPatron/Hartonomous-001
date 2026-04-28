CREATE TABLE substrate.edge_role (
    id   SERIAL PRIMARY KEY,
    code VARCHAR(32) NOT NULL UNIQUE
);
COMMENT ON TABLE substrate.edge_role IS
    'Participant roles in n-ary edges (source, target, context, mediator, evidence, head, dependent).';
