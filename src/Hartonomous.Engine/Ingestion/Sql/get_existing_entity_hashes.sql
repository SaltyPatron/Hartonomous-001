SELECT hash FROM substrate.entity WHERE hash = ANY($1::bytea[])
