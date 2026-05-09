#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "access/htup_details.h"
#include "catalog/pg_type.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/guc.h"
#include <stdlib.h>
#include <signal.h>
#include <string.h>

#ifndef _WIN32
  #define _GNU_SOURCE
  #include <execinfo.h>
  #include <unistd.h>
  #include <ucontext.h>
  #include <sys/ucontext.h>
  #include <fcntl.h>
  #include <unwind.h>
  #include <stdio.h>
#endif

#include "hartonomous.h"

#ifdef PG_MODULE_MAGIC
PG_MODULE_MAGIC;
#endif

int hartonomous_max_traversal_results = 10000;
bool hartonomous_strict_determinism = true;
bool hartonomous_traversal_trace = false;
static int hartonomous_resolved_cbwr_branch = -1;

void _PG_init(void);

#ifndef _WIN32

/*
 * libgcc DWARF-based stack unwinder. Used by the crash handler instead of
 * the rbp chain walk so corrupted frame pointers don't truncate the trace
 * to a single frame. Walks .eh_frame CFI which lives in each shared library
 * and isn't affected by runtime stack corruption.
 *
 * Async-signal-safe: only write() and snprintf-on-stack-buffer. No malloc.
 *
 * Output format per frame:
 *   ===   frame[N] ip=<hex>   [<library>+<offset>]
 * Resolve to file:line via:
 *   addr2line -fCe <library> <offset>
 */
typedef struct HtnsUnwindCtx
{
    int depth;
    int max_depth;
} HtnsUnwindCtx;

static _Unwind_Reason_Code
hartonomous_unwind_callback(struct _Unwind_Context *ctx, void *arg)
{
    HtnsUnwindCtx *uc = (HtnsUnwindCtx *) arg;
    if (uc->depth >= uc->max_depth) { return _URC_END_OF_STACK; }

    uintptr_t ip = _Unwind_GetIP(ctx);

    /* Walk /proc/self/maps to find which library this ip falls in. We do
     * this per-frame, scanning the file linearly. The maps file is small
     * enough that this is async-signal-safe and fast in absolute terms. */
    char libname[256];
    uintptr_t lib_base = 0;
    bool found_lib = false;
    int mfd = open("/proc/self/maps", O_RDONLY);
    if (mfd >= 0)
    {
        char mbuf[8192];
        ssize_t n;
        char accum[16384];
        size_t alen = 0;
        while ((n = read(mfd, mbuf, sizeof(mbuf))) > 0 &&
               alen + (size_t) n < sizeof(accum))
        {
            memcpy(accum + alen, mbuf, (size_t) n);
            alen += (size_t) n;
        }
        close(mfd);

        /* Linear scan of accum for a line whose [start,end) range contains ip.
         * Format: "STARTHEX-ENDHEX PERMS OFFSET DEV INODE  PATH\n". */
        size_t i = 0;
        while (i < alen)
        {
            size_t line_start = i;
            while (i < alen && accum[i] != '\n') { i++; }
            size_t line_end = i;
            if (i < alen) { i++; }

            uintptr_t lo = 0, hi = 0;
            size_t p = line_start;
            while (p < line_end && accum[p] != '-')
            {
                char c = accum[p];
                int v = (c >= '0' && c <= '9') ? c - '0'
                      : (c >= 'a' && c <= 'f') ? c - 'a' + 10
                      : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1;
                if (v < 0) break;
                lo = (lo << 4) | (uintptr_t) v;
                p++;
            }
            if (p >= line_end || accum[p] != '-') { continue; }
            p++;
            while (p < line_end && accum[p] != ' ')
            {
                char c = accum[p];
                int v = (c >= '0' && c <= '9') ? c - '0'
                      : (c >= 'a' && c <= 'f') ? c - 'a' + 10
                      : (c >= 'A' && c <= 'F') ? c - 'A' + 10 : -1;
                if (v < 0) break;
                hi = (hi << 4) | (uintptr_t) v;
                p++;
            }

            if (ip < lo || ip >= hi) { continue; }

            /* Find the path: 5 space-separated fields after the start, then a path. */
            int spaces = 0;
            while (p < line_end && spaces < 4)
            {
                if (accum[p] == ' ') { spaces++; while (p < line_end && accum[p] == ' ') p++; }
                else { p++; }
            }
            /* Skip leading spaces of the path. */
            while (p < line_end && accum[p] == ' ') p++;

            size_t path_len = (line_end > p) ? (line_end - p) : 0;
            if (path_len == 0)
            {
                /* anonymous mapping (heap, stack, anon mmap). Note it. */
                snprintf(libname, sizeof(libname), "[anon]");
            }
            else
            {
                size_t copy = path_len < sizeof(libname) - 1 ? path_len : sizeof(libname) - 1;
                memcpy(libname, accum + p, copy);
                libname[copy] = '\0';
            }
            lib_base = lo;
            found_lib = true;
            break;
        }
    }
    if (!found_lib)
    {
        snprintf(libname, sizeof(libname), "?");
        lib_base = 0;
    }

    char fbuf[512];
    int flen;
    if (lib_base != 0)
    {
        flen = snprintf(fbuf, sizeof(fbuf),
                        "===   frame[%d] ip=%p   [%s+0x%lx]\n"
                        "===     addr2line -fCe %s 0x%lx\n",
                        uc->depth, (void *) ip, libname,
                        (unsigned long) (ip - lib_base),
                        libname,
                        (unsigned long) (ip - lib_base));
    }
    else
    {
        flen = snprintf(fbuf, sizeof(fbuf),
                        "===   frame[%d] ip=%p   [%s]\n",
                        uc->depth, (void *) ip, libname);
    }
    if (flen > 0) { (void) write(STDERR_FILENO, fbuf, (size_t) flen); }

    uc->depth++;
    return _URC_NO_REASON;
}

static void
hartonomous_unwind_backtrace(void *fault_rip)
{
    (void) fault_rip;  /* already printed in the crash header */
    HtnsUnwindCtx uc;
    uc.depth = 0;
    uc.max_depth = 32;
    _Unwind_Backtrace(hartonomous_unwind_callback, &uc);

    char tail[128];
    int tlen = snprintf(tail, sizeof(tail),
                        "=== End hartonomous stack unwind (%d frames) ===\n",
                        uc.depth);
    if (tlen > 0) { (void) write(STDERR_FILENO, tail, (size_t) tlen); }
}

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
 *
 * Linux-only — uses execinfo.h backtrace APIs and POSIX sigaction. On
 * Windows the install function is a no-op (PG handles SEH at the OS level
 * via vectored exception handlers; substrate-side hooking would need
 * StackWalk64 + SymFromAddr from DbgHelp).
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
     * Stack walk via libgcc's _Unwind_Backtrace. Uses DWARF .eh_frame tables
     * (emitted by every modern compiler regardless of -fomit-frame-pointer),
     * NOT the rbp chain. This survives corrupted rbp — exactly the failure
     * mode that produced single-frame traces under the previous rbp walk.
     *
     * _Unwind_Backtrace is documented async-signal-safe by libgcc as long as
     * we don't call malloc inside the trace callback. We don't — the
     * callback only does write()/snprintf-on-stack-buffer.
     *
     * On stack overflow / return-address smash, the FIRST frame may still be
     * garbage (RIP). Subsequent frames are recovered from .eh_frame CFI which
     * lives in each shared library's read-only data and isn't affected by
     * runtime stack corruption — so we get back to the C function that
     * issued the bad call even when rbp is trashed.
     */
    {
        const char *fhdr = "=== hartonomous: stack unwind (libgcc _Unwind_Backtrace, DWARF .eh_frame) ===\n";
        ssize_t w = write(STDERR_FILENO, fhdr, strlen(fhdr));
        (void) w;

        if (rsp != NULL)
        {
            char sbuf[256];
            int slen = snprintf(sbuf, sizeof(sbuf), "===   stack at rsp:");
            if (slen > 0) { (void) write(STDERR_FILENO, sbuf, (size_t) slen); }
            for (int k = 0; k < 8; k++)
            {
                uintptr_t *slot = (uintptr_t *) ((char *) rsp + k * 8);
                slen = snprintf(sbuf, sizeof(sbuf), " %p", (void *) *slot);
                if (slen > 0) { (void) write(STDERR_FILENO, sbuf, (size_t) slen); }
            }
            (void) write(STDERR_FILENO, "\n", 1);

            uintptr_t saved_ret_at_rsp = *((uintptr_t *) rsp);
            slen = snprintf(sbuf, sizeof(sbuf),
                            "===   *(rsp) = caller return PC = %p\n",
                            (void *) saved_ret_at_rsp);
            if (slen > 0) { (void) write(STDERR_FILENO, sbuf, (size_t) slen); }
        }

        hartonomous_unwind_backtrace(rip);

        /*
         * RBP-chain fallback. _Unwind_Backtrace stops at the heap-RIP frame
         * because heap pages have no .eh_frame entry, so a stack-smash crash
         * loses the real caller chain. When rbp is intact (the corruption
         * was on the return-address slot specifically, not the saved-rbp
         * slot below it) the rbp linked list still walks back through the
         * legitimate caller frames. We dump up to 16 levels — each frame is
         * (saved_rbp, saved_ret_pc) at *(rbp), *(rbp + 8). Stops on a
         * non-stack rbp (wraps off the stack range) or a nil saved_rbp.
         */
        if (rbp != NULL)
        {
            const char *rhdr = "=== hartonomous: rbp-chain fallback (DWARF unwind didn't reach caller) ===\n";
            (void) write(STDERR_FILENO, rhdr, strlen(rhdr));

            uintptr_t cur_rbp = (uintptr_t) rbp;
            for (int level = 0; level < 16; level++)
            {
                if (cur_rbp == 0) break;
                /* Stack-bounds heuristic: typical x86-64 user stack pointers
                 * have the top byte 0x7f. If we walk off into another region
                 * the chain is corrupted and dereferencing further is unsafe. */
                if ((cur_rbp >> 56) != 0x7f) break;

                uintptr_t saved_rbp = *((uintptr_t *) cur_rbp);
                uintptr_t saved_pc  = *((uintptr_t *) (cur_rbp + 8));

                char rbuf[160];
                int rlen = snprintf(rbuf, sizeof(rbuf),
                                    "===   rbp[%2d] frame_rbp=%p saved_rbp=%p ret_pc=%p\n",
                                    level, (void *) cur_rbp, (void *) saved_rbp, (void *) saved_pc);
                if (rlen > 0) { (void) write(STDERR_FILENO, rbuf, (size_t) rlen); }

                /* saved_rbp must monotonically increase up the stack. */
                if (saved_rbp <= cur_rbp) break;
                cur_rbp = saved_rbp;
            }
            const char *rtail = "=== End rbp-chain fallback ===\n";
            (void) write(STDERR_FILENO, rtail, strlen(rtail));
        }
    }

    /*
     * Dump /proc/self/maps so the caller_rip values from the frame chain
     * can be resolved to library + offset by hand or via addr2line.
     */
    {
        const char *maps_header = "=== /proc/self/maps (resolve frame caller_rip to library:offset) ===\n";
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
#else  /* _WIN32 */
/* Windows: PG already installs vectored exception handlers via the
 * postmaster; substrate-side backtrace capture would require DbgHelp
 * (StackWalk64 + SymFromAddr), which we haven't wired. No-op for now —
 * crashes still surface in PG's own logs, just without a substrate-side
 * stack dump. */
static void hartonomous_install_crash_handlers(void) { }
#endif  /* _WIN32 */

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
        "hartonomous.traversal_trace",
        "Emit a NOTICE at every entry/exit of pg_traverse_astar and pg_neighbors.",
        "Diagnostic-only. When true, traversal functions log seed hash, arena, "
        "step counts, and exit reason. Use with SET LOCAL hartonomous.traversal_trace = on; "
        "before running a query to debug crashes — the last NOTICE before the SEGV "
        "tells you which step died.",
        &hartonomous_traversal_trace,
        false,
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

    /*
     * Lazy-mmap UCD atoms blob. The blob lives next to the extension SQL
     * file at $libdir/../share/extension/hartonomous-ucd/ by default.
     * The directory can be overridden via GUC (hartonomous.ucd_blob_dir);
     * if the index file is missing the loader returns gracefully and
     * substrate.cp_hash() / cp_centroid() / cp_hilbert() return NULL.
     * Backends that never touch those functions never page in the blob.
     */
    {
        extern int huc_load_atoms_blob(const char* dir);
        char dir[1024];
        const char* env = getenv("HARTONOMOUS_UCD_BLOB_DIR");
        if (env && *env) {
            snprintf(dir, sizeof(dir), "%s", env);
        } else {
            /* Default: $share/extension/hartonomous-ucd/ */
            char share[MAXPGPATH];
            get_share_path(my_exec_path, share);
            snprintf(dir, sizeof(dir), "%s/extension/hartonomous-ucd", share);
        }
        if (huc_load_atoms_blob(dir) != 0) {
            ereport(WARNING,
                    (errmsg("hartonomous: UCD atoms blob not loaded from %s — "
                            "cp_hash/centroid/hilbert will return NULL", dir),
                     errhint("Install the blob via scripts/db/InstallUcdBlob.ps1 "
                             "or set HARTONOMOUS_UCD_BLOB_DIR env var.")));
        }
    }
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
