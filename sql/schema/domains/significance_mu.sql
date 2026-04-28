CREATE DOMAIN substrate.significance_mu AS FLOAT8;
COMMENT ON DOMAIN substrate.significance_mu IS
    'Glicko-2 rating mean. Trust priors seed 1000-2000; values evolve with arena play.';
