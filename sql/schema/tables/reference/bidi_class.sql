CREATE TABLE substrate.bidi_class (
    id          SERIAL PRIMARY KEY,
    code        VARCHAR(8) NOT NULL UNIQUE,
    description VARCHAR(64) NOT NULL
);

COMMENT ON TABLE substrate.bidi_class IS
    'UAX #9 Bidirectional Character Type. ~23 values (L, R, AL, EN, ES, ...). Populated by UCD seed from DerivedBidiClass.txt.';
