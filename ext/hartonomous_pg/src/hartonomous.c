#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "utils/builtins.h"
#include "utils/guc.h"

#define _GNU_SOURCE
#include <execinfo.h>
#include <signal.h>
#include <string.h>
#include <unistd.h>
#include <ucontext.h>
#include <sys/ucontext.h>
#include <fcntl.h>

#include "hartonomous.h"

#ifdef PG_MODULE_MAGIC
PG_MODULE_MAGIC;
#endif

int hartonomous_max_traversal_results = 10000;
bool hartonomous_strict_determinism = true;
static int hartonomous_resolved_cbwr_branch = -1;

void _PG_init(void);

/*
 * Crash backtrace handler. Installed in _PG_init for SIGSEGV, SIGABRT,
 * SIGBUS, SIGFPE, SIGILL. When a backend dies hard inside C code, the
 * default Postgres behaviour is for the postmaster to log "client backend
 * terminated by signal N" with no stack — because the dead backend is gone
 * before postmaster can ask it for state. This handler runs in the dying
 * backend's own context and writes a backtrace to stderr (which docker
 * captures alongside the postmaster's "terminated" line) before re-raising
 * the signal so postmaster's recovery logic still fires.
 *
 * Async-signal-safe: backtrace_symbols_fd uses no malloc, no FILE*. write()
 * is the only stdio. Do not call elog/ereport from here — they allocate.
 */
static void
hartonomous_crash_backtrace_handler(int signo, siginfo_t *info, void *ucontext)
{
    char    header[512];
    int     hlen;
    pid_t   pid = getpid();
    void   *rip = NULL;
    void   *rbp = NULL;
    void   *rsp = NULL;

    /*
     * Pull the faulting instruction pointer + frame pointer + stack pointer
     * directly from the kernel-supplied ucontext. This is async-signal-safe
     * (just register reads) and survives stack corruption — unlike
     * backtrace(), which the previous version of this handler called and
     * which crashed re-entrantly when the original SIGSEGV was caused by a
     * trashed stack (the original crash got masked by an inner crash inside
     * the handler itself, producing a useless 3-frame trace pointing at
     * backtrace@plt).
     *
     * Glibc's REG_RIP / REG_RBP / REG_RSP indices live in <sys/ucontext.h>.
     * The ucontext_t cast is required because PG's signal handler signature
     * passes ucontext as void*.
     */
    if (ucontext != NULL)
    {
        ucontext_t *uc = (ucontext_t *) ucontext;
#ifdef REG_RIP
        rip = (void *) (uintptr_t) uc->uc_mcontext.gregs[REG_RIP];
        rbp = (void *) (uintptr_t) uc->uc_mcontext.gregs[REG_RBP];
        rsp = (void *) (uintptr_t) uc->uc_mcontext.gregs[REG_RSP];
#endif
    }

    hlen = snprintf(header, sizeof(header),
                    "\n=== hartonomous: backend pid=%d caught signal %d "
                    "(si_code=%d, si_addr=%p)\n"
                    "===   rip=%p rbp=%p rsp=%p\n"
                    "===   addr2line -fCe /opt/pg18/lib/postgresql/hartonomous.so %p\n"
                    "=== End hartonomous crash header ===\n",
                    (int) pid, signo,
                    info ? info->si_code : -1,
                    info ? info->si_addr : NULL,
                    rip, rbp, rsp,
                    rip);
    if (hlen > 0)
    {
        ssize_t w = write(STDERR_FILENO, header, (size_t) hlen);
        (void) w;
    }

    /*
     * Dump /proc/self/maps so we can identify which library/segment contains
     * the faulting rip. Without this, a rip in a runtime-allocated executable
     * region (LLVM JIT, mmap'd JIT cache, dynamically-loaded .so) is
     * unattributable. open/read/write are async-signal-safe.
     */
    {
        const char *maps_header = "=== /proc/self/maps (look for the segment containing rip) ===\n";
        ssize_t w = write(STDERR_FILENO, maps_header, strlen(maps_header));
        int fd = open("/proc/self/maps", O_RDONLY);
        if (fd >= 0)
        {
            char buf[4096];
            ssize_t n;
            while ((n = read(fd, buf, sizeof(buf))) > 0)
            {
                ssize_t off = 0;
                while (off < n)
                {
                    ssize_t wrote = write(STDERR_FILENO, buf + off, (size_t) (n - off));
                    if (wrote <= 0) break;
                    off += wrote;
                }
            }
            close(fd);
        }
        (void) w;
        {
            const char *maps_tail = "=== End /proc/self/maps ===\n";
            ssize_t w2 = write(STDERR_FILENO, maps_tail, strlen(maps_tail));
            (void) w2;
        }
    }

    /*
     * Re-raise so postmaster sees the same signal and runs its recovery
     * (kill all backends, reinitialize). Reset to default first so the
     * second delivery is the actual signal, not us looping.
     */
    {
        struct sigaction sa;
        memset(&sa, 0, sizeof(sa));
        sa.sa_handler = SIG_DFL;
        sigemptyset(&sa.sa_mask);
        sigaction(signo, &sa, NULL);
        raise(signo);
    }
}

/*
 * Alternate signal stack. sigaltstack(2) gives signal handlers a separate
 * stack region so they survive corruption of the main stack. Without this,
 * a SIGSEGV from a trashed-stack condition (stack overflow, return-address
 * smash) recurses inside the handler itself when the handler's locals can't
 * be stored to the bad stack frame — which is exactly what produced the
 * useless 3-frame trace pointing at backtrace@plt that we saw on the first
 * crash class.
 *
 * Fixed compile-time size (256KB) — recent glibc made SIGSTKSZ a function
 * call returning a runtime value, which can't size a file-scope array.
 * 256KB is well above MINSIGSTKSZ (~2KB) and gives ucontext-RIP capture
 * plus header formatting plenty of room.
 */
#define HARTONOMOUS_ALT_STACK_SIZE (256 * 1024)
static char hartonomous_alt_stack[HARTONOMOUS_ALT_STACK_SIZE];

static void
hartonomous_install_crash_handlers(void)
{
    struct sigaction sa;
    stack_t           ss;
    int signos[] = { SIGSEGV, SIGABRT, SIGBUS, SIGFPE, SIGILL };
    size_t i;

    /*
     * Switch this backend onto the alternate signal stack BEFORE arming any
     * handlers. If sigaltstack fails we still install the handlers without
     * SA_ONSTACK — better to have a fragile handler than none at all — but
     * the warning gives us a paper trail.
     */
    memset(&ss, 0, sizeof(ss));
    ss.ss_sp    = hartonomous_alt_stack;
    ss.ss_size  = sizeof(hartonomous_alt_stack);
    ss.ss_flags = 0;

    if (sigaltstack(&ss, NULL) != 0)
    {
        ereport(WARNING,
                (errcode(ERRCODE_SYSTEM_ERROR),
                 errmsg("hartonomous: sigaltstack install failed; crash handler "
                        "may recurse on corrupted main stacks")));
    }

    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = hartonomous_crash_backtrace_handler;
    sigemptyset(&sa.sa_mask);
    sa.sa_flags = SA_SIGINFO | SA_RESETHAND | SA_NODEFER | SA_ONSTACK;

    for (i = 0; i < sizeof(signos) / sizeof(signos[0]); i++)
    {
        if (sigaction(signos[i], &sa, NULL) != 0)
        {
            ereport(WARNING,
                    (errcode(ERRCODE_SYSTEM_ERROR),
                     errmsg("hartonomous: could not install crash handler for signal %d",
                            signos[i])));
        }
    }
}

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

    /*
     * Install crash backtrace handlers AFTER GUCs are defined so any error
     * in our handler-install path is reportable. This is the only diagnostic
     * we get when a backend dies hard inside C code — without it, postmaster
     * just logs "terminated by signal N" with no stack.
     */
    hartonomous_install_crash_handlers();
    ereport(LOG,
            (errmsg("hartonomous: crash backtrace handlers installed (SIGSEGV, SIGABRT, SIGBUS, SIGFPE, SIGILL)")));
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
