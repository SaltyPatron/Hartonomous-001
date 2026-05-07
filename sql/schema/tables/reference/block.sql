CREATE TABLE substrate.block (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(128) NOT NULL UNIQUE,
    range_start INT NOT NULL,
    range_end   INT NOT NULL
);

COMMENT ON TABLE substrate.block IS
    'Unicode Block ranges. 300+ blocks. range_start/range_end enable O(log n) block lookup by codepoint integer.';
