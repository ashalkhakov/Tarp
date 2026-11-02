# Exception paths: [#9](https://github.com/ashalkhakov/Tarp/issues/9)

Tarp shows whether `catch` and `finally` bodies ran (their statements have probes), but has no coverage
items for the paths exceptions take. Candidates:

- the try block was exited via normal flow;
- a catch clause was entered because of an exception in its try block;
- a finally clause was entered from the try block (normal entry) or from a catch block (exceptional entry);
- a function was left by an exception it didn't catch (JaCoCo's "exexit" edge).

In the CLR, a try-catch-finally is a try-catch nested in a try-finally:

```
try {
   try { S1; }                 // exceptions in S1 go to the catches below
   catch (E1) { S2; leave L0; }
   catch (E2) { S3; leave L0; }
   L0: leave L1;               // runs the finally, then goes to L1
}
finally { Sf; }
L1: ...
```

# References

- Vincenzi et al., "How Exception Handling Constructions are Tested: An Initial Investigation with Open
  Source Software" (ICSEA 2013): a try block may have additional "exceptional exits"; some blocks are
  reachable only through them (the handlers).
- https://stackoverflow.com/questions/44907244/how-do-we-account-for-exception-throws-in-static-code-analysis
  (extended basic blocks: one exception edge per block)
- https://sable.mcgill.ca/~hendren/621/LectureNotes/Control%20Flow%20Analysis%20Handout2015.pdf (p. 28)
- "Constructing Control Flow Graph for Java by Decoupling Exception Flow from Normal Flow" (Jang Wu-Jo
  et al.): https://link.springer.com/chapter/10.1007/978-3-540-24707-4_14,
  https://typeset.io/pdf/constructing-control-flow-graph-for-java-by-decoupling-2u1kxqfb8x.pdf
- https://essay.utwente.nl/56937/1/Scriptie_Smelik.pdf
- https://llayman.github.io/seng-201/labs/cfg/
- JaCoCo's control flow analysis (exception handler and unhandled exception edges):
  https://www.jacoco.org/jacoco/trunk/doc/flow.html
- GCC's exception handling edges: http://www.ucw.cz/~hubicka/papers/proj/node18.html
