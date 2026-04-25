#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "utils/builtins.h"
#include "utils/guc.h"

#include "hartonomous.h"

#ifdef PG_MODULE_MAGIC
PG_MODULE_MAGIC;
#endif

int hartonomous_max_traversal_results = 10000;
bool hartonomous_strict_determinism = true;
static int hartonomous_resolved_cbwr_branch = -1;

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

    DefineCustomBoolVariable(
        "hartonomous.strict_determinism",
        "Enforce MKL CBWR=AUTO,STRICT at extension load (Law #6).",
        "When true, _PG_init pins MKL conditional-bitwise-reproducibility so "
        "that all compute issued by the substrate is byte-reproducible across "
        "runs within an ISA class. Disabling this voids the determinism "
        "contract; the substrate's correctness model assumes it is on.",
        &hartonomous_strict_determinism,
        true,
        PGC_BACKEND,
        0,
        NULL,
        NULL,
        NULL
    );

    /*
     * MKL initialization is now lazy — moved out of _PG_init and into every
     * MKL-using SQL function entry point via hartonomous_ensure_mkl_initialized().
     * Eager init in _PG_init forced every newly-forked postgres backend to pay
     * MKL's per-process pool-rebuild cost (~7s) on every fresh connection,
     * which broke the inference-engine latency target (microseconds-per-step,
     * milliseconds-per-walk, sub-second LLM-equivalent response). With lazy
     * init, graph-traversal-only backends pay zero MKL cost.
     */
    (void)hartonomous_strict_determinism;
}

PG_FUNCTION_INFO_V1(pg_hartonomous_version);

Datum
pg_hartonomous_version(PG_FUNCTION_ARGS)
{
    const char *v = hartonomous_version();
    PG_RETURN_TEXT_P(cstring_to_text(v));
}

PG_FUNCTION_INFO_V1(pg_hartonomous_runtime_info);

/*
 * Returns a record (mkl_version text, mkl_max_threads int, omp_max_threads int,
 * cbwr_branch int, strict_determinism bool). Lets SQL assert the determinism
 * contract is in force without parsing log output.
 */
Datum
pg_hartonomous_runtime_info(PG_FUNCTION_ARGS)
{
    hartonomous_runtime_info_t info;
    TupleDesc   tupdesc;
    Datum       values[5];
    bool        nulls[5] = {false, false, false, false, false};
    HeapTuple   tuple;

    hartonomous_runtime_info(&info);

    if (get_call_result_type(fcinfo, NULL, &tupdesc) != TYPEFUNC_COMPOSITE)
        ereport(ERROR,
                (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                 errmsg("function returning record called in context that "
                        "cannot accept type record")));

    tupdesc = BlessTupleDesc(tupdesc);

    values[0] = CStringGetTextDatum(info.mkl_version);
    values[1] = Int32GetDatum(info.mkl_max_threads);
    values[2] = Int32GetDatum(info.omp_max_threads);
    values[3] = Int32GetDatum(info.cbwr_branch);
    values[4] = BoolGetDatum(hartonomous_strict_determinism);

    tuple = heap_form_tuple(tupdesc, values, nulls);
    PG_RETURN_DATUM(HeapTupleGetDatum(tuple));
}
