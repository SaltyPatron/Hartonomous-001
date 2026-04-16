#include "postgres.h"
#include "fmgr.h"
#include "utils/builtins.h"
#include "utils/guc.h"

#include "hartonomous.h"

#ifdef PG_MODULE_MAGIC
PG_MODULE_MAGIC;
#endif

int hartonomous_max_traversal_results = 10000;

void _PG_init(void);

void
_PG_init(void)
{
    DefineCustomIntVariable(
        "hartonomous.max_traversal_results",
        "Maximum number of rows returned by traversal functions.",
        NULL,
        &hartonomous_max_traversal_results,
        10000,
        1,
        1000000,
        PGC_USERSET,
        0,
        NULL,
        NULL,
        NULL
    );
}

PG_FUNCTION_INFO_V1(pg_hartonomous_version);

Datum
pg_hartonomous_version(PG_FUNCTION_ARGS)
{
    const char *v = hartonomous_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}
