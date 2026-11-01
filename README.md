# McDcTestCoverage

Tarp is a test coverage measurement tool for C#.

## MC/DC

Tarp measures masking MC/DC (modified condition/decision coverage):

```sh
tarp-buildtool mcdc-instrument -p MyProject/MyProject.csproj -o Instrumented
dotnet run --project Instrumented        # or run its tests; hits go to Instrumented/tarp-hits
tarp-buildtool mcdc-report -d Instrumented
```

1. `mcdc-instrument` rewrites every decision (`&&`/`||`/`!` expressions, and the conditions of
   `if`/`while`/`do`/`for`/`?:`) so that each evaluation records which conditions were evaluated,
   their values and the outcome (`Tarp/McDc/DecisionRewriter.cs`). It writes a copy of the project
   with the runtime (`Tarp/GenerationTemplates/McDcRuntime.cs`) and `tarp-mcdc-map.json`.
2. The instrumented program writes the distinct evaluations to `tarp-hits/*.hits` when it exits
   (set `TARP_HITS_DIR` to write them elsewhere).
3. `mcdc-report` replays the evaluations through the monitor algorithm (`McDcFunctions`) and lists,
   per decision, the evaluations seen, the conditions shown, and a test for every missing value.

Not instrumented (listed in the report with the reason): decisions that declare pattern variables
(`o is string s && ...`), assign variables in a condition after the first, contain `await`, are in
expression-tree lambdas or query expressions, are constant, or have more than 16 conditions.

The copy of the project only builds for SDK-style projects whose sources are under the project
directory: project references and linked files don't resolve from the copy yet.
