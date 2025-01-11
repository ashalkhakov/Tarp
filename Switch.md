```csharp
switch (E) {
case C1:
case C2:
...
case Cn:
	// E == C1 || E == C2 || ... || E == Cn
	// entry into this block
	// NOTE: but also! E is only evaluated ONCE!
	// e.g. a temporary needs to be created. tmp = E; if (tmp == C1 || tmp == C2 || ... || tmp == Cn) S1 else if (tmp == Cm) ...
	S1;
	break;
case Cm:
	// E == Cm
	// entry into this block
	S2;
	break;
default:
	// negation of all of the above predicates...
	// entry into this block
	break;
}
```


see also: http://www.ada-europe.org/conference2014/presentations/MCDC%20Case%20Statements.pdf
they are handling it EXACTLY as expected. so, rewrite to consecutive if-then-else:

```csharp
if (E == C1 || E == C2 || ... || E == Cn) {S1;}
else if (E == Cm) {S2;}
else {}
```
 
also, don't care about multiple evaluations of E. the executable code will still be the same,
we just want to mark it for ourselves when a specific condition has been evaluated.
e.g. if we get into S1, then we know that tmp=E;tmp==C1||tmp==C2||tmp==C3 must have been evaluated.
 
OR, do care about multiple evaluations. then simply introduce fresh temporaries.
OR, just disallow for E to be anything but a variable in the source program. make programmers rewrite it. probably the best solution.
OR, let programmers know that if E contains side-effects, instrumentation WILL change the behavior of their programs. woops.

TODO: support default-less switch (in this case, instead of a default, you just go directly to [done]).