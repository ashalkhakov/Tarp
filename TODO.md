# gcov

not sure about gcov, but https://github.com/choller/LLCov/
- block coverage instrumentation: call into a runtime library whenever a basic block is executed
- this just goes over all functions, then enumerates all basic blocks, then
  - Add function call: void func(const char* function, const char* filename, uint32_t line, uint32_t relblock);

https://census-labs.com/media/instrumentation-part1.pdf
from:
> mov %rsi,-0x10(%rbp)
> mov $0x400d78,%edi
> callq 400720 <puts@plt>
to:
> mov %rsi,-0x10(%rbp)
> # added
> mov 0x2043a3(%rip),%rax
> # 6053e0 <__gcov0.print_stat_info>
> add $0x1,%rax
> mov %rax,0x204398(%rip)
> # 6053e0 <__gcov0.print_stat_info>
> # end of addition
> mov $0x403868,%edi
> callq 400d40 <puts@plt>

Each instrumented point raises a counter and results are flushed to a data file at program exit (prog.gcda)

# any other source-level instrumentation programs?

https://covtool.sourceforge.net/theory_of_operation.html
https://www2.cs.uh.edu/~jhuang/JCH/PT/7instrument.pdf
https://www2.cs.uh.edu/~jhuang/BookT/tpp7instrument.pdf

# LLVM optimization

https://lists.llvm.org/pipermail/llvm-dev/2015-August/089044.html
they use MST aka Minimal Spanning Tree (which they get out of CFG), and insert probes at edges of MST.

what's MST?
- Knuth, "Optimal measurement points for program frequency counts"

Below is from: https://www.classes.cs.uchicago.edu/archive/2017/fall/32001-1/papers/ball-larus-profiling.pdf


All weighings W of a CFG G assign a nonnegative value
to every edge subject to Kirchoff’s law of conservation of
flow:
- for each vertex v, the sum of the weights of edges
  with target v (the incoming edges of v) must be equal to the
  sum of the weights of edges with source v (the outgoing
  edges of v)
- forall v:vertex. sum(weight({e:edge | target(e) = v})) = sum(weight({e:edge | source(e) = v}))

The weight of a vertex is simply the sum of
the weights of its incoming (or outgoing) edges. If W is a
weighting of CFG G, then for a set of edges and vertices pl
from CFG G, cost(G, pl, W) is the sum of the weights on
the edges and vertices in pl.

4. Program tracing

Just as a program can be instrumented to record basic block
execution frequency, it also can be instrumented to record
the sequence of basic blocks executed.

The *tracing problem* is to record enough information about
a program’s execution to be able to reproduce the entire execution.
A straightforward way to solve this problem is to instrument
each basic block so that it writes a unique mark (witness) to
a trace file whenever it executes. In this case, the trace file
need only be read to regenerate the execution. A more
efficient method is to write a witness only at basic blocks
that are targets of predicates [8].

4.1 Single-procedure tracing

- assume that basic blocks contain no calls
- the extra EXIT->root edge is not included in the CFG

epl: the set of instrumented edges of CFG
- whenever an edge e in epl is traversed, a "witness" to that edge's execution is written to a trace file
- No two edges in epl generate the same witness. 
- (AS: the trace file is just a sequence of edges in epl then, followed by EOF marker)

def: a path in CFG G is witness-free wrt to epl iff no edge in the path is in epl
def: given a CFG G, a set of edges epl, and edge p->q where p is a predicate, the witness set to vertex q for predicate p is

witness(G, epl, p, q) =
  { w | p->q in epl and writes witness w }
  U { w | x->y in epl and writes witness w and exists witness-free path p->q->...->x }
  U { EOF | exists witness-free path p->q->...->EXIT }

def: a set of edges epl solves the *tracing problem* for CFG G, denoted Trace(G,epl) iff for each predicate p in G
     with successors q1,...,qm, for all pairs (qi,qj) such that i!=j,

  witness(G,epl,p,qi) intersect witness(G,epl,p,qj) != {}

4.2 Multi-procedure tracing

def: the set epl has the blocking property for CFG G iff there is no predicate p in G
     such that there is a witness-free path from p to the EXIT vertex or a vertex containing a call
def: the set { epl[1], ..., epl[m] } solves the *tracing problem* for a set of CFGs { G[1], ..., G[m] } iff,
     for all i, epl[i] solves Trace(G[i], epl[i]) and epl[i] has the blocking property for G[i]

def: let v be a vertex in CFG G. the *blocking edges* of v are defined thus:

  blockers(G,v) = { p->x[0] | there is path p->x[0]->...->x[n] where p is a predicate, v = x[n], and for 0<=i<=n, x[i] is not a predicate }

# NOTES on source based coverage

https://clang.llvm.org/docs/SourceBasedCodeCoverage.html#mc-dc-instrumentation
- they put instrumentation code at start of basic blocks, since that is what they say about exceptions:

> Code coverage does not handle unpredictable changes in control flow or stack unwinding in the presence
> of exceptions precisely. Consider the following function:
>
> > int f() {
> >   may_throw();
> >   return 0;
> > }
>
> If the call to `may_throw()` propagates an exception into `f`, the code coverage tool may mark
> the return statement as executed even though it is not. A call to `longjmp()` can have similar effects.

- they don't instrument various boolean expressions for MC/DC:

> if a boolean expression is embedded in the nest of another boolean expression
> but separated by a non-logical operator, this is also not supported. For example,
> in `x = (a && b && c && func(d && f))`, the `d && f` case starts a new boolean
> expression that is separated from the other conditions by the operator `func()`.
> When this is encountered, a warning will be generated and the boolean expression
> will not be instrumented.

- they have a rule about `switch` body too: https://clang.llvm.org/docs/SourceBasedCodeCoverage.html#switch-statements
