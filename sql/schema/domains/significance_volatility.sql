CREATE DOMAIN substrate.significance_volatility AS FLOAT8
    CONSTRAINT volatility_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_volatility IS
    'Glicko-2 meta-uncertainty (rate of mu change). Strictly positive.';
