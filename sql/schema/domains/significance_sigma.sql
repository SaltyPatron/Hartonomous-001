CREATE DOMAIN substrate.significance_sigma AS FLOAT8
    CONSTRAINT sigma_positive CHECK (VALUE > 0);
COMMENT ON DOMAIN substrate.significance_sigma IS
    'Glicko-2 rating uncertainty. Decreases as evidence accumulates. Strictly positive.';
