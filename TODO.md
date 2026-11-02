# Open ideas

## Hit counts: [#7](https://github.com/ashalkhakov/Tarp/issues/7)

Tarp records whether each probe ran; gcov counts. LLCov (https://github.com/choller/LLCov/) calls into a
runtime library for every basic block executed: `void func(const char* function, const char* filename,
uint32_t line, uint32_t relblock)`. gcov adds 1 to a counter per instrumented point and writes the
counters out at program exit (`prog.gcda`), as in https://census-labs.com/media/instrumentation-part1.pdf.

## Probe placement: [#10](https://github.com/ashalkhakov/Tarp/issues/10)

Only if probes turn out to be too slow. LLVM puts counters on the edges not in a spanning tree of the
CFG and derives the others from flow conservation
(https://lists.llvm.org/pipermail/llvm-dev/2015-August/089044.html):

- Knuth, "Optimal measurement points for program frequency counts"
- Ball & Larus, "Optimally profiling and tracing programs"
  (https://www.classes.cs.uchicago.edu/archive/2017/fall/32001-1/papers/ball-larus-profiling.pdf):
  every weighting of a CFG obeys Kirchhoff's law (for each vertex, the weights of the incoming edges
  sum to those of the outgoing ones), so the counts on a minimal set of edges determine the rest.

# Compared to clang

https://clang.llvm.org/docs/SourceBasedCodeCoverage.html

- clang puts its counters at the start of basic blocks, so a statement after a call that throws can
  show as run. Tarp's probes run before each statement, so it doesn't.
- clang doesn't measure a boolean expression nested in another one behind a non-logical operator
  (`a && b && func(d && f)`), nor (clang 17-18) one with a `!` over a nested `&&` or `||`. Tarp measures
  both (`d && f` is a decision of its own).
- clang computes unique-cause MC/DC with short circuit, Tarp masking MC/DC: `crosscheck/` compares them.
- clang's rules for `switch`: https://clang.llvm.org/docs/SourceBasedCodeCoverage.html#switch-statements

# Other source-level instrumentation

- https://covtool.sourceforge.net/theory_of_operation.html
- https://www2.cs.uh.edu/~jhuang/JCH/PT/7instrument.pdf, https://www2.cs.uh.edu/~jhuang/BookT/tpp7instrument.pdf
