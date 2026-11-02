# MC/DC over case labels: [#8](https://github.com/ashalkhakov/Tarp/issues/8)

Today each section of a `switch` statement is a branch (and "no case matched" when there's no
`default`). The case labels could be the conditions of a decision, "this section is taken":

```csharp
switch (E) {
case C1:
case C2:
    // E == C1 || E == C2: each label a condition
    S1;
    break;
case Cm:
    // E == Cm
    S2;
    break;
default:
    // none of the above
    break;
}
```

That is how MC/DC for case statements is treated in
http://www.ada-europe.org/conference2014/presentations/MCDC%20Case%20Statements.pdf: as the chain
`if (E == C1 || E == C2) S1 else if (E == Cm) S2 else ...`. Tarp needn't rewrite the switch (E is still
evaluated once): it only has to record which label matched.
