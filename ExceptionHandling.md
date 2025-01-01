> try { S }
> catch (T1 e) { S1 }
> ...
> catch (Tn e) { Sn }
> finally { Sf }
 
we probably need special edges for exceptions
basically:
execute the [try] as a new block. if everything is OK, goto [finally] or to the next statement after the [try-catch-finally].
if exception is thrown, then [catches] are tried in the order given, one by one. if you get inside the catch, you will then go to the [finally] or the next statement.
[finally] block is always the block after all the catches.

[throw] also needs to be taken into account.

----
a special kind of branch at the end of a basic block?
a multiway kind of 'catch'
----

we can just represent all 'catch'es as typical branching.
'throw' inside a 'try' does a 'goto' into the 'catch' block. the 'catch' block contains multiple sub-blocks with implicit branching.
all of the 'catch' blocks will 'goto' the 'finally' block (if present), or to AFTER the whole statement.
the 'finally' block performs some translation and control goes to wherever it went.

----
UPDATE 2024-01-21:
file:///C:/Users/User/Downloads/icsea_2013_2_20_10241.pdf
How Exception Handling Constructions are Tested: An Initial Investigation with Open Source Software
Auri Marcelo Rizzo Vincenzi et al

no, simple branching will not suffice. a 'try' block may have additional 'exceptional exits'.
- basically, any basic block may have additional 'exceptional edges' which represent control flow when an exception is raised!
  - this is a wholly new way to branch! it's ORTHOGONAL to regular exits
- some basic blocks can only be reached through the 'exceptional edges', which makes them 'exception handlers'
- and some basic blocks can be reached through the 'finally edges', which are exits from exception handlers OR from the 'try' blocks

also regarding [finally]. there's some syntax sugar involved.
- in CLR there is only 'try-catch' and 'try-finally'.
- 'try-catch-finally' is 'try-catch' nested in 'try-finally'
  try {
     try { S1; } // the exceptional control flow of S1 MUST go through the [catch] statements below
     catch (E1) { S2; leave L0; } // this use of "leave" means: just goto L0 ^_^
     catch (E2) { S3; leave L0; }
     L0: leave L1; // this use of "leave" means: go to the finally block, execute Sf; then goto L1
  }
  finally { Sf; }
  L1: ...
  
- the 'catch' blocks end with a 'leave' to the end of the try-catch

So, Roslyn does it this way. there are 'regions' on top of blocks. a method is a Try region.
there are also TryAndCatch and TryAndFinally regions (and a try-catch-finally is rewritten as above). regions may nest.
a TryAndCatch region has the following subregions:
- a Try region
- multiple Catch regions
a TryAndFinally region has the following subregions:
- a Try region
- a Finally region
I guess that regular control flow goes as follows between regions:
- Try is executed, then Finally may be executed, but then we go to the statement after the TryFinally
- Catches are never executed
but the exceptional control flow is meant to be quite convoluted...

what's the control flow between regions?

# more references

1. https://stackoverflow.com/questions/44907244/how-do-we-account-for-exception-throws-in-static-code-analysis

Answer by antimony:

> Extended Basic Blocks are similar to Basic Blocks, except that exception edges are represented semi-implicitly,
> resulting in a much smaller CFG. An EBB consists of straight line code with no entry points or exit points
> in the middle apart from exception edges, and where every instruction in the block is covered by the same
> set of exception handlers. That way, instead of having an exception edge per instruction, you have one per
> block, making things much more efficient.

2. https://sable.mcgill.ca/~hendren/621/LectureNotes/Control%20Flow%20Analysis%20Handout2015.pdf

page 28:

> Regions in CFG – Bigger Blocks?
> Extended basic block (EBB)
> EBB is a maximal set of nodes in a CFG that contains
no join nodes other than the entry node
> A single entry and possibly multiple exits
> Some optimizations like value numbering and instruction
scheduling are more effective if applied in EBBs

3. https://link.springer.com/chapter/10.1007/978-3-540-24707-4_14

Constructing Control Flow Graph for Java by Decoupling Exception Flow from Normal Flow (Jang Wu-Jo et al)

- separate exception flow from control flow (for Java)

https://typeset.io/pdf/constructing-control-flow-graph-for-java-by-decoupling-2u1kxqfb8x.pdf

4. https://essay.utwente.nl/56937/1/Scriptie_Smelik.pdf

5. https://llayman.github.io/seng-201/labs/cfg/

> For `try` nodes, you have a single incoming edge. You have one outgoing edge to the internal nodes of the `try`,
> and one outgoing edge to each `except` and `finally` block.

so, the Roslyn "region" idea means that all nodes in that region are implicitly covered by that region.
- all nodes in a TryAndCatch region will be implicitly going out of that region
- you don't need an explicit edge out of that region, right?
