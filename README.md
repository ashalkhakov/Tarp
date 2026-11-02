# McDcTestCoverage

Tarp is a test coverage measurement tool for C#.

## What Tarp measures

From one run of your tests, in source terms (what you wrote, not the compiled IL):

- **Lines**: a probe runs right before each statement, and counts how many times; a statement's lines
  count as run once it has started, so a call that throws doesn't make the lines after it look run. A compound statement's
  probe covers its header (`if (...)`, `switch (...)`); its body has probes of its own. So do the
  condition of a `do ... while`, lambda bodies, switch expression arms, and field and property
  initializers: a line belongs to the innermost probe around it, so a lambda or an arm that never ran
  shows as not run even when the statement around it did.
- **Functions**: entered or not, and which exits were taken: `return` and `throw` statements, and the
  end of the body when it can be reached. With `--tarp-exception-exits` (or the MSBuild property
  `TarpExceptionExits=true`), also whether an exception left the function: each body gets an exception
  filter that records it and catches nothing, so exceptions and their stack traces are as they were.
  The reports count these apart, as "Exceptions" (functions an exception left, of those wrapped), and
  don't list a function that no exception left as a gap: in most tests, most functions return.
  It's opt-in because a method with a handler is often not inlined, which can slow hot code down, and
  a debugger may stop differently; iterators aren't wrapped (`yield return` can't be in such a try). Methods, constructors, accessors, operators, local
  functions and top-level statements count; lambdas don't (their statements are still lines).
- **Branches**: both outcomes of every decision (including `when` guards and `catch` filters), every
  `switch` section (including "no case matched" for a switch without a `default`, when some value can
  match none), every switch expression arm, both sides of `??`, null and not null for `?.` and `?[]`,
  whether a `foreach` iterated at all, and for a `try` with `catch` clauses: each catch clause, and the
  try block completing without an exception (where its end can be reached). For a `finally`, each
  way it can be entered: the try (or a catch clause) completing, leaving by `return`/`break`/
  `continue`/`goto`, or an exception (from the try not caught there, or from a catch clause). A local
  set along the way says which, so no handler is added (not in a try with `yield return`).
- **Conditions**: each condition of each decision seen both true and false. A decision is a boolean
  expression with `&&`, `||`, `!`, and on bools `&`, `|`, `^`, `==`, `!=` and `?:` (as DO-178C counts
  them), the primitive patterns of an `is` with `and`/`or`/`not` (`x is > 0 and < 10` has two
  conditions), and the case labels of a `switch` section with several (`case 1: case 2:` is a decision,
  "this section is taken", with two conditions; not in a switch with `goto case`). A side of `&&`
  that's always true, `x is var y` (or `var _`), isn't a condition: it can never be false. A decision
  that's only such sides isn't instrumented.
- **MC/DC**: each condition shown to independently affect its decision's outcome (masking MC/DC).

## MC/DC

Tarp measures masking MC/DC (modified condition/decision coverage) of the code your tests run.

### In an existing solution

Install the tool and run your tests through it:

```sh
dotnet tool install -g dotnet-tarp   # from nuget.org
dotnet tarp test                     # takes the same arguments as dotnet test
```

The tool runs on .NET 10 or newer. The projects it tests can target older frameworks: it
instruments their sources, and their own SDK builds them.

Without nuget.org, download `dotnet-tarp.<version>.nupkg` from a GitHub release into a directory
and add `--add-source <directory>` to the install command. `dotnet pack Tarp.BuildTool` makes the
package from a checkout.

No change to the projects is needed. `dotnet tarp test` runs `dotnet test` with
`Tarp.BuildTool/Tarp.targets` imported into every project of the build, then prints the report.
For each C# project that isn't a test project, right before it compiles:

1. Tarp builds the same compilation the compiler is about to build (sources, references,
   defines, language version, source generators), rewrites every decision (`&&`/`||`/`!`
   expressions, and the conditions of `if`/`while`/`do`/`for`/`?:`) so that each evaluation records
   which conditions were evaluated, their values and the outcome (`Tarp/McDc/DecisionRewriter.cs`),
   and checks that the result still compiles: a decision whose instrumentation would break the build
   is left alone and reported (`Tarp/McDc/McDcBuildInstrumenter.cs`).
2. The rewritten sources (in `obj/.../tarp/`) and the runtime
   (`Tarp/GenerationTemplates/McDcRuntime.cs`) replace the project's sources for the compiler only.
   `#line` directives keep errors, stack traces and the debugger on the original files. They're marked
   `// <auto-generated/>` (with the project's nullable context), so formatters and analyzers run over
   the folder, such as `dotnet format --folder`, skip them. A normal build afterwards compiles the
   original sources again.
3. The instrumented code writes the distinct evaluations to `TestResults/tarp/*.hits` when the test
   host exits, next to each project's map (`<assembly>.<target framework>.map.json`).
4. The report replays the evaluations through the monitor algorithm (`McDcFunctions`) and lists,
   per decision, the evaluations seen, the conditions shown, and a test for every missing value.
   `dotnet tarp mcdc-report` prints it again from `TestResults/tarp`.

Options: `--tarp-results <dir>` (instead of `TestResults/tarp`), `--tarp-exception-exits` (count
functions left by an exception, see above), the reports and the gate below. In a project:
`<TarpExclude>true</TarpExclude>` to leave it out; `[ExcludeFromCodeCoverage]` leaves out a member
or type.

### Reports

| Option | Format | For |
|---|---|---|
| `--tarp-report <file>` | text (the console without it) | reading |
| `--tarp-lcov <file>` | lcov tracefile (`coverage.info`) | genhtml, Codecov, Coveralls, editors' coverage gutters, other lcov tools |
| `--tarp-cobertura <file>` | Cobertura XML | Azure DevOps' Code Coverage tab, GitLab merge requests, Jenkins, Codecov, ReportGenerator |
| `--tarp-markdown <file>` | Markdown | a CI job summary (`$GITHUB_STEP_SUMMARY`) or a pull request comment |
| `--tarp-html <file>` | one self-contained HTML page | browsing the source with counts, light and dark |

Several can be given at once. `dotnet tarp mcdc-report -d TestResults/tarp` takes the same ones as
`--report`, `--lcov`, `--cobertura`, `--markdown` and `--html`.

The tracefile has lines with hit counts (`DA`), functions (`FN`, `FNDA`), branches (`BRDA`: each
decision's true and false outcome, and the branch points above) and MC/DC (`MCDC`, lcov 2.x: a
condition's sense is 1 when shown to affect the outcome), with full source paths. CI checks it with
lcov 2.6: `genhtml --branch-coverage --mcdc-coverage coverage.info -o html`.

The Cobertura report has lines with hit counts and, per line, its branches taken (`condition-coverage`,
counted as in the tracefile), with one package per assembly, one class per source file, and a method
per function with its own lines and branches (a local function's lines are its own, a lambda's belong
to the function around it). Cobertura has no MC/DC.

The Markdown and HTML reports give, per file, what wasn't covered, and MC/DC per decision as tables:
the evaluations recorded (`T`, `F`, `-` for not evaluated), then per condition whether it was shown
true and false, and a test that would show what's missing. "no pair" is a condition of `^`, `==`,
`!=` or `?:` shown both ways, but never in two evaluations with different outcomes.

### Coverage gate

`--tarp-fail-under-lines <percent>`, `--tarp-fail-under-branches <percent>` and
`--tarp-fail-under-mcdc <percent>` make `dotnet tarp test` fail (exit code 1, after writing the
reports) when the coverage is below the minimum; failing tests fail it as before. MC/DC is the share
of conditions shown, as in the summary. `--tarp-exclusions <file>` leaves known gaps out of the gate
(not out of the reports), one a line, with paths and conditions as the reports name them:

```
# generated, tested elsewhere
Shop.Core/Generated/**/*.cs
# a defensive check that can't fail: everything on line 68 (its lines, decisions, branch points)
Shop.Core/Orders.cs:68
# one condition of the decisions on line 18: the others there are still checked
Shop.Core/Orders.cs:18 id.Length < 20
# one branch: a decision's outcome on line 24, a switch's default on line 120
Shop.Core/Orders.cs:24 [false]
Shop.Core/Orders.cs:120 [default:]
```

`*` matches within a directory, `**` across directories. A condition entry leaves out MC/DC only, a
branch entry branches only. A branch is named as the text report names it after "not taken:", e.g.
`decision at line 24: outcomes not taken: false (...)` or `switch at line 120: sections not taken:
default:`; other branch points' ways are `[_]` (an arm), `[no iterations]`, `[catch (IOException)]`
and so on. So a baseline can be made from the text report: an entry per condition "NOT shown" and per
branch "not taken". The gate prints its numbers, and the exclusions
that leave out no gap of a checked metric: closed by a test since, or no code there.
`--tarp-strict-exclusions` makes those fail the gate, so that the list can only shrink: a ratchet.
`dotnet tarp mcdc-report` takes the same options without `tarp-`: `--fail-under-mcdc 95
--exclusions exclusions.txt --strict-exclusions`.

### In Azure DevOps

`dotnet tarp test` takes `dotnet test`'s arguments, so the test results can be published as well:

```yaml
steps:
- task: UseDotNet@2
  inputs:
    version: 10.0.x
- script: dotnet tool install -g dotnet-tarp
- script: >
    dotnet tarp test --logger trx --results-directory $(Agent.TempDirectory)/tests
    --tarp-cobertura $(Agent.TempDirectory)/coverage/cobertura.xml
    --tarp-markdown $(Agent.TempDirectory)/coverage/coverage.md
    --tarp-html $(Agent.TempDirectory)/coverage/index.html
    --tarp-fail-under-mcdc 100 --tarp-fail-under-branches 100
    --tarp-exclusions coverage-exclusions.txt --tarp-strict-exclusions
  displayName: Test with coverage
- task: PublishTestResults@2
  condition: succeededOrFailed()
  inputs:
    testResultsFormat: VSTest
    testResultsFiles: $(Agent.TempDirectory)/tests/*.trx
- task: PublishCodeCoverageResults@2
  condition: succeededOrFailed()
  inputs:
    summaryFileLocation: $(Agent.TempDirectory)/coverage/cobertura.xml
- script: echo "##vso[task.uploadsummary]$(Agent.TempDirectory)/coverage/coverage.md"
  condition: succeededOrFailed()
  displayName: Coverage and MC/DC summary
- publish: $(Agent.TempDirectory)/coverage
  artifact: coverage
  condition: succeededOrFailed()
```

Tarp makes the reports' directories; the gate fails the step, and the steps after it publish the
results anyway. On the pipeline run:

- **Tests** tab: the test results, from the `.trx` files.
- **Code Coverage** tab: lines and branches per folder and file, from the Cobertura report, down to
  a file's source where the agent's paths let Azure find it. Azure DevOps has no MC/DC view.
- **Extensions** tab: the Markdown report: the summary (MC/DC included), the files, and per file what
  wasn't covered, with each incomplete decision's MC/DC tables (the evaluations, then per condition
  what's missing and a test that would show it). Azure renders it with its own Markdown, not the wiki's.
- **Artifacts** (`coverage`): the HTML report, `index.html`; download it and open it for the source
  with counts and every decision's MC/DC tables.

### A single project, without a build

`dotnet tarp mcdc-instrument -p MyProject.csproj -o Instrumented` writes an instrumented copy of the
project; build and run it, then `dotnet tarp mcdc-report -d Instrumented`. The copy only builds for
SDK-style projects whose sources are under the project directory, without project references.

### Not instrumented

Listed in the report with the reason: decisions in expression-tree lambdas or in queries that
become expression trees (`IQueryable`), constant ones, ones with a condition that isn't a `bool` (e.g. `dynamic`) or with more than 16
conditions, ones in projects older than C# 7.3, and ones whose instrumentation breaks compilation.
Pattern variables (`o is string s && s.Length > 0`), assignments in conditions and `await` are fine:
each condition gets markers that keep what the compiler knows after it (see `DecisionRewriter`), and
the evaluation in progress is a local, so it survives an `await`. Files
that are generated (in `obj/`, `*.g.cs`, `*.designer.cs`, or marked `<auto-generated>`) aren't
instrumented.

## How the MC/DC verdicts are checked

`crosscheck/crosscheck.py` runs Tarp (through `crosscheck/tarp-runner`) on decisions with given tests,
and compares its verdict for every condition with:

- the worked examples of *A Practical Tutorial on Modified Condition/Decision Coverage* (Hayhurst,
  Veerhusen, Chilenski, Rierson; NASA/TM-2001-210876), where the tutorial states the answer: the
  building blocks (and, or, xor, not), examples 1-3 and 7, and the solutions to exercises 3.3a-d and 3.5;
- clang's MC/DC (`-fcoverage-mcdc`, `llvm-cov show --show-mcdc`), for decisions C can express (`&&`,
  `||`, `!`), including 300 random ones: clang computes unique-cause MC/DC, so it may show fewer
  conditions than Tarp's masking MC/DC, never more;
- two checkers written from the definitions, independent of Tarp's code: masking MC/DC (a condition
  shows a value if flipping it alone can change the outcome) and unique-cause MC/DC with short circuit.

```sh
python3 crosscheck/crosscheck.py crosscheck/tarp-runner /tmp/crosscheck
```

CI runs it on Linux with LLVM 18 (set `CLANG`, `LLVM_PROFDATA` and `LLVM_COV` to choose the tools).

## Building and releasing

CI (`.github/workflows/ci.yml`) builds and tests on Linux and Windows, and runs `samples/e2e.sh`:
it packs the tool, installs it into a copy of `samples/Shop`, runs `dotnet tarp test`, and checks the
report.

To release, push a tag: `git tag v0.1.0 && git push origin v0.1.0`. The release workflow
(`.github/workflows/release.yml`) takes the version from the tag, tests, packs, checks the package
with `samples/e2e.sh`, and creates a GitHub release with the package.

It also publishes the package to nuget.org, with [trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
(no API key to store), once that is set up:

1. On nuget.org, under your user name → **Trusted Publishing**, add a policy: repository owner
   `ashalkhakov`, repository `Tarp`, workflow file `release.yml`, no environment.
2. In the GitHub repository, add a `NUGET_USER` secret with your nuget.org profile name (not your email).

To publish a tag that was released before that, run the Release workflow by hand (**Actions →
Release → Run workflow**) with the tag: it checks the package attached to the tag's release and
pushes that one.
