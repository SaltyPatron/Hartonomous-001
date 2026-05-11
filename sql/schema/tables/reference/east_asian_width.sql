CREATE TABLE substrate.east_asian_width (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(2) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.east_asian_width IS
    'UAX #11 East Asian Width. Six values: N (Neutral), Na (Narrow), A (Ambiguous), W (Wide), F (Fullwidth), H (Halfwidth). Populated by UCD seed from EastAsianWidth.txt.';
