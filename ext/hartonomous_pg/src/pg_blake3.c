#include "postgres.h"
#include "fmgr.h"
#include "varatt.h"
#include "utils/builtins.h"

#include "hartonomous.h"

PG_FUNCTION_INFO_V1(pg_blake3_hash);
PG_FUNCTION_INFO_V1(pg_blake3_hash_text);

Datum
pg_blake3_hash(PG_FUNCTION_ARGS)
{
    bytea *input = PG_GETARG_BYTEA_PP(0);
    bytea *result = (bytea *)palloc(VARHDRSZ + HARTONOMOUS_HASH_LEN);
    SET_VARSIZE(result, VARHDRSZ + HARTONOMOUS_HASH_LEN);

    hartonomous_blake3(
        (const uint8_t *)VARDATA_ANY(input),
        VARSIZE_ANY_EXHDR(input),
        (uint8_t *)VARDATA(result)
    );

    PG_RETURN_BYTEA_P(result);
}

Datum
pg_blake3_hash_text(PG_FUNCTION_ARGS)
{
    text *input = PG_GETARG_TEXT_PP(0);
    bytea *result = (bytea *)palloc(VARHDRSZ + HARTONOMOUS_HASH_LEN);
    SET_VARSIZE(result, VARHDRSZ + HARTONOMOUS_HASH_LEN);

    hartonomous_blake3(
        (const uint8_t *)VARDATA_ANY(input),
        VARSIZE_ANY_EXHDR(input),
        (uint8_t *)VARDATA(result)
    );

    PG_RETURN_BYTEA_P(result);
}
