CREATE TABLE substrate.general_category (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(4) NOT NULL UNIQUE,
    group_code  VARCHAR(1) NOT NULL,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.general_category IS
    'Unicode General Category property. 30 values in 7 groups (L, M, N, P, S, Z, C).';
