#!/usr/bin/env python3
"""
Cross-checks Tarp's MC/DC verdicts against:
  - the worked examples of "A Practical Tutorial on Modified Condition/Decision Coverage"
    (Hayhurst, Veerhusen, Chilenski, Rierson; NASA/TM-2001-210876), where it states the answer;
  - clang's MC/DC (-fcoverage-mcdc, llvm-cov --show-mcdc), for decisions C can express (&& || !);
  - two reference checkers written here from the definitions, independent of Tarp's code:
      masking MC/DC: a condition shows a value in a test if flipping it alone can change the outcome
        (conditions the test didn't evaluate may take any value: short-circuit "don't care"); it is
        covered by two tests showing it true and false, with different outcomes;
      unique-cause MC/DC with short circuit: two tests where the condition differs, the outcome
        differs, and every other condition is equal or wasn't evaluated in one of them.

  crosscheck.py <tarp runner dir> <work dir>

The tools: CLANG, LLVM_PROFDATA and LLVM_COV (default: clang, and llvm-profdata / llvm-cov through xcrun
on macOS). Exits with 1 when anything disagrees.
"""
import itertools, json, os, random, re, shlex, subprocess, sys

DARWIN = sys.platform == "darwin"
CLANG = shlex.split(os.environ.get("CLANG", "clang"))
LLVM_PROFDATA = shlex.split(os.environ.get("LLVM_PROFDATA", "xcrun llvm-profdata" if DARWIN else "llvm-profdata"))
LLVM_COV = shlex.split(os.environ.get("LLVM_COV", "xcrun llvm-cov" if DARWIN else "llvm-cov"))

# --- a parser for decisions in C#/C syntax -----------------------------------------------------------

TOKEN = re.compile(r"\s*(\|\||&&|==|!=|<=|>=|[()!&|^<>?:]|[A-Za-z_]\w*|\d+)")

def parse(text):
    tokens = TOKEN.findall(text)
    assert "".join(tokens) == text.replace(" ", ""), text
    pos = [0]
    leaves = []
    def peek(): return tokens[pos[0]] if pos[0] < len(tokens) else None
    def take(t=None):
        tok = tokens[pos[0]]; assert t is None or tok == t, (text, t, tok); pos[0] += 1; return tok
    def binary(op, kind, sub):
        def p():
            node = sub()
            while peek() == op:
                take(); node = (kind, node, sub())
            return node
        return p
    def conditional():
        c = orr()
        if peek() == "?":
            take(); x = conditional(); take(":"); y = conditional()
            return ("cond", c, x, y)
        return c
    def unary():
        if peek() == "!":
            take(); return ("not", unary())
        if peek() == "(":
            take(); e = conditional(); take(")"); return e
        name = take()
        if peek() in ("<", ">", "<=", ">="):
            op = take(); number = int(take())
            leaves.append(f"{name} {op} {number}")
            return ("leaf", len(leaves) - 1, name, op, number)
        leaves.append(name)
        return ("leaf", len(leaves) - 1, name, None, None)
    eq = lambda: equality()
    def equality():
        node = unary()
        while peek() in ("==", "!="):
            op = take(); node = ("eq" if op == "==" else "ne", node, unary())
        return node
    band = binary("&", "band", equality)
    bxor = binary("^", "xor", band)
    bor = binary("|", "bor", bxor)
    andd = binary("&&", "and", bor)
    orr = binary("||", "or", andd)
    tree = conditional()
    assert pos[0] == len(tokens), text
    return tree, leaves

def evaluate(node, leaf_value, evaluated):
    """C#/C evaluation (short-circuit for && || ?:); leaf_value(i) gives leaf i's value"""
    kind = node[0]
    if kind == "leaf":
        evaluated.add(node[1]); return leaf_value(node[1])
    if kind == "not": return not evaluate(node[1], leaf_value, evaluated)
    if kind == "and": return evaluate(node[1], leaf_value, evaluated) and evaluate(node[2], leaf_value, evaluated)
    if kind == "or": return evaluate(node[1], leaf_value, evaluated) or evaluate(node[2], leaf_value, evaluated)
    if kind == "cond":
        return evaluate(node[2], leaf_value, evaluated) if evaluate(node[1], leaf_value, evaluated) else evaluate(node[3], leaf_value, evaluated)
    l = evaluate(node[1], leaf_value, evaluated); r = evaluate(node[2], leaf_value, evaluated)
    return {"band": l and r, "bor": l or r, "xor": l != r, "eq": l == r, "ne": l != r}[kind]

def leaf_values(tree, params, args):
    """the value of every leaf for a call"""
    env = dict(zip(params, args))
    values = {}
    def walk(n):
        if n[0] == "leaf":
            _, i, name, op, number = n
            v = env[name]
            values[i] = v if op is None else {"<": v < number, ">": v > number, "<=": v <= number, ">=": v >= number}[op]
        else:
            for c in n[1:]: walk(c)
    walk(tree)
    return values

def run_test(tree, params, args):
    values = leaf_values(tree, params, args)
    evaluated = set()
    outcome = evaluate(tree, lambda i: values[i], evaluated)
    return {i: values[i] for i in evaluated}, outcome

# --- the reference checkers ---------------------------------------------------------------------------

def masking_covered(tree, n, tests):
    """per condition: shown true and false (flipping it alone can change the outcome) with different outcomes"""
    def shows(test, c):
        known, _ = test
        if c not in known: return None
        free = [i for i in range(n) if i not in known]
        for completion in itertools.product([False, True], repeat=len(free)):
            values = dict(known); values.update(zip(free, completion))
            flipped = dict(values); flipped[c] = not values[c]
            if evaluate(tree, lambda i: values[i], set()) != evaluate(tree, lambda i: flipped[i], set()):
                return known[c]
        return None
    covered = []
    for c in range(n):
        trues = [t for t in tests if shows(t, c) is True]
        falses = [t for t in tests if shows(t, c) is False]
        covered.append(any(a[1] != b[1] for a in trues for b in falses))
    return covered

def unique_cause_covered(n, tests):
    covered = []
    for c in range(n):
        ok = False
        for (ka, oa), (kb, ob) in itertools.combinations(tests, 2):
            if c in ka and c in kb and ka[c] != kb[c] and oa != ob and \
               all(d == c or d not in ka or d not in kb or ka[d] == kb[d] for d in range(n)):
                ok = True
        covered.append(ok)
    return covered

# --- the cases ----------------------------------------------------------------------------------------

def bools(spec, n):
    """"TTF FTF" -> [[True, True, False], [False, True, False]]; X (don't care) is false"""
    out = []
    for word in spec.split():
        assert len(word) == n, spec
        out.append([ch == "T" for ch in word])
    return out

CASES = []
def case(name, source, expr, params, tests, expect=None, note=""):
    """expect: per condition True (covered) / False (not covered), "not all", or None (the tutorial doesn't say)"""
    CASES.append(dict(name=name, source=source, expr=expr, params=params, tests=tests, expect=expect, note=note))

def tutorial_cases():
    p3, p4 = ["a", "b", "c"], ["a", "b", "c", "d"]
    case("3-input and gate", "§3.1.1 Table 5", "a & b & c", p3, bools("TTT FTT TFT TTF", 3), [True] * 3)
    case("3-input or gate", "§3.1.2 Table 6", "a | b | c", p3, bools("FFF TFF FTF FFT", 3), [True] * 3)
    all4 = bools("TT TF FT FF", 2)
    for subset in itertools.combinations(range(4), 3):
        case(f"xor, tests {','.join(str(i + 1) for i in subset)}", "§3.1.3 Table 7", "a ^ b", ["a", "b"], [all4[i] for i in subset], [True, True])
    for subset in itertools.combinations(range(4), 2):
        case(f"xor, tests {','.join(str(i + 1) for i in subset)}", "§3.1.3 (any three)", "a ^ b", ["a", "b"], [all4[i] for i in subset], "not all",
             "two tests never suffice for xor")
    case("not gate", "§3.1.4", "!a", ["a"], bools("F T", 1), [True])

    ex1 = bools("TTF FTF FTT TTT TFF", 3)
    case("Example 1", "§3.2, Tables 11-16", "(b & c) | d", ["b", "c", "d"], ex1, [True] * 3)
    case("Example 1 without test 4", "§3.2.4 (1, 2, 3, 5 satisfy)", "(b & c) | d", ["b", "c", "d"], [ex1[i] for i in (0, 1, 2, 4)], [True] * 3)
    case("Example 1, short-circuit", "§3.2 with && ||", "(b && c) || d", ["b", "c", "d"], ex1, None)

    ex2 = bools("FFTF FTFT TFFF FTTF TFTF", 4)
    case("Example 2 (masking)", "§3.3 Tables 17-18", "(a | b) & (c | d)", p4, ex2, [True] * 4, "masking: yes; unique-cause: no for d (Table 20)")
    case("Example 2, short-circuit", "§3.3 with && ||", "(a || b) && (c || d)", p4, ex2, None, "unique-cause: d not shown (Table 20)")
    ex2a = bools("FFTF TFFT TFFF FTTF TFTF", 4)
    case("Example 2A", "§3.3 Tables 19-20", "(a | b) & (c | d)", p4, ex2a, [True] * 4, "both definitions")
    case("Example 2A, short-circuit", "§3.3 with && ||", "(a || b) && (c || d)", p4, ex2a, None)

    ex3 = bools("TTFF TFFF FTFF FTFT FTTF", 4)
    case("Example 3", "§3.3 Tables 21-22", "(a & !b) | (c ^ d)", p4, ex3, [False, True, True, True], "missing: a false with not b true, xor false")
    case("Example 3 + FFTT", "§3.3 step 5", "(a & !b) | (c ^ d)", p4, ex3 + bools("FFTT", 4), [True] * 4)
    case("Exercise 3.3a", "Appendix A, Solution 3.3a", "(a & !b) | (c | d)", p4, ex3, [False, True, True, True])
    # the solution's "plus (FFTT) provide MC/DC" is about example 3 (xor): with c | d, FFTT makes the right side true,
    # which masks a; a test with c | d false is needed instead (derived here, not stated by the tutorial)
    case("Exercise 3.3a + FFTT", "Appendix A, Solution 3.3a", "(a & !b) | (c | d)", p4, ex3 + bools("FFTT", 4), None, "FFTT is example 3's fix")
    case("Exercise 3.3a + FFFF", "derived", "(a & !b) | (c | d)", p4, ex3 + bools("FFFF", 4), None, "c | d false: a visible")

    gt = ["maintRqst", "maintValid", "wow", "engine1On", "engine2On"]
    gt_tests = bools("TTTFF FTTFF TFTFF TTTTT TTTTF TTFFF", 5)
    case("Exercise 3.3b, ground test", "Appendix A, Solution 3.3b", "maintRqst & maintValid & wow & !(engine1On | engine2On)", gt, gt_tests,
         [True, True, True, True, False], "or gate: FT missing")
    case("Exercise 3.3b, short-circuit", "with && ||", "maintRqst && maintValid && wow && !(engine1On || engine2On)", gt, gt_tests, None)

    wow = ["squatL", "squatR", "airspeed", "airspeedValid"]
    wow_tests = [[True, True, 35, True], [False, True, 35, True], [True, False, 45, True], [True, False, 35, False], [True, True, 45, False]]
    case("Exercise 3.3c, weight on wheels", "Appendix A, Solution 3.3c", "(squatL & squatR) | ((airspeed < 40) & airspeedValid)", wow, wow_tests,
         [False, True, True, True], "and1: FT missing")
    case("Exercise 3.3c, short-circuit", "with && ||", "(squatL && squatR) || ((airspeed < 40) && airspeedValid)", wow, wow_tests, None)

    gains = ["airDataValid", "inAir"]
    case("Exercise 3.3d, gains", "Appendix A, Solution 3.3d", "airDataValid & inAir", gains, bools("TT FT TF", 2), [True, True])
    case("Exercise 3.3d, first two tests", "Appendix A (not sufficient)", "airDataValid & inAir", gains, bools("TT FT", 2), "not all")
    case("Exercise 3.3d, short-circuit", "with &&", "airDataValid && inAir", gains, bools("TT FT TF", 2), None)

    t29 = bools("TTTT TTTF TTFT TFTT FTTT", 4)
    t31 = bools("TTTT TTTF TTFF TFFF FFFF", 4)
    case("Example 7, Table 29", "§3.6", "a && b && c && d", p4, t29, [True] * 4)
    case("Example 7, Table 31 (short-circuit)", "§3.6 follow-on", "a && b && c && d", p4, t31, [True] * 4, "valid because of short circuit")
    case("Example 7, Table 29, gates", "§3.6 (no difference)", "a & b & c & d", p4, t29, [True] * 4)
    case("Example 7, Table 31, gates", "§3.6 (only with short circuit)", "a & b & c & d", p4, t31, None)
    case("Table 33, or else", "§3.6 Table 33", "a || b || c || d", p4, bools("FFFF TFFF FTFF FFTF FFFT", 4), [True] * 4)

    latch = ["output", "set", "reset"]
    latch_tests = [[False, False, False], [False, True, False], [True, False, False], [True, True, True]]
    case("Exercise 3.5, latch (tests 2-5)", "Appendix A, Solution 3.5", "(output | set) & !reset", latch, latch_tests, [True] * 3,
         "Output: the previous test's output")
    case("Exercise 3.5, short-circuit", "with && ||", "(output || set) && !reset", latch, latch_tests, None)

def random_cases(count, seed=1):
    rnd = random.Random(seed)
    def tree(names):
        if len(names) == 1:
            return names[0] if rnd.random() < 0.8 else f"!{names[0]}"
        k = rnd.randrange(1, len(names))
        text = f"({tree(names[:k])}) {rnd.choice(['&&', '||'])} ({tree(names[k:])})"
        return f"!({text})" if rnd.random() < 0.15 else text
    for i in range(count):
        n = rnd.randrange(2, 6)
        names = [f"x{j}" for j in range(n)]
        inputs = [list(v) for v in itertools.product([False, True], repeat=n)]
        tests = rnd.sample(inputs, rnd.randrange(2, min(len(inputs), 8) + 1))
        case(f"random {i + 1}", "random", tree(names), names, tests, None)

# --- running the tools --------------------------------------------------------------------------------

def is_c(expr): return not re.search(r"(?<![&|])[&|](?![&|])|\^|==|!=|\?", expr)

def run_tarp(runner, work):
    lines = ["public static class Cases", "{"]
    inputs, line_of = {}, {}
    for k, c in enumerate(CASES):
        params = ", ".join(("int " if p == "airspeed" else "bool ") + p for p in c["params"])
        line_of[k] = len(lines) + 1
        lines.append(f"    public static bool Case{k}({params}) {{ if ({c['expr']}) return true; return false; }}")
        inputs[f"Case{k}"] = c["tests"]
    lines.append("}")
    open(os.path.join(work, "Cases.cs"), "w").write("\n".join(lines) + "\n")
    json.dump(inputs, open(os.path.join(work, "inputs.json"), "w"))
    run = subprocess.run(["dotnet", "run", "--project", runner, "--", os.path.join(work, "Cases.cs"), os.path.join(work, "inputs.json"),
                          os.path.join(work, "tarp.json")], capture_output=True, text=True)
    if run.returncode != 0:
        sys.exit(f"the Tarp runner failed:\n{run.stdout}\n{run.stderr}")
    result = json.load(open(os.path.join(work, "tarp.json")))
    out = {}
    for k in range(len(CASES)):
        d = result["Decisions"].get(str(line_of[k]))
        out[k] = None if d is None else [c["Covered"] for c in d["Conditions"]]
        if d and d["Inconsistent"]:
            print(f"!! Tarp recorded inconsistent evaluations for case {k}")
    return out

def run_clang(work):
    lines = ["#include <stdbool.h>", "#include <stdio.h>"]
    line_of, calls = {}, []
    for k, c in enumerate(CASES):
        if not is_c(c["expr"]) or len(parse(c["expr"])[1]) < 2:
            continue
        params = ", ".join(("int " if p == "airspeed" else "bool ") + p for p in c["params"])
        line_of[len(lines) + 1] = k
        lines.append(f"int case{k}({params}) {{ if ({c['expr']}) return 1; return 0; }}")
        for t in c["tests"]:
            calls.append(f"    case{k}({', '.join(str(v).lower() if isinstance(v, bool) else str(v) for v in t)});")
    lines += ["int main(void) {"] + calls + ["    return 0;", "}"]
    src = os.path.join(work, "cases.c")
    open(src, "w").write("\n".join(lines) + "\n")
    exe = os.path.join(work, "cases")
    # clang warns about decisions it can't measure (! over && or ||): those have no MC/DC record
    subprocess.run(CLANG + ["-fprofile-instr-generate", "-fcoverage-mapping", "-fcoverage-mcdc", "-Wno-everything", src, "-o", exe], check=True)
    env = dict(os.environ, LLVM_PROFILE_FILE=os.path.join(work, "cases.profraw"))
    subprocess.run([exe], check=True, env=env)
    subprocess.run(LLVM_PROFDATA + ["merge", os.path.join(work, "cases.profraw"), "-o", os.path.join(work, "cases.profdata")], check=True)
    show = subprocess.run(LLVM_COV + ["show", exe, f"-instr-profile={os.path.join(work, 'cases.profdata')}", "--show-mcdc"],
                          check=True, capture_output=True, text=True).stdout
    open(os.path.join(work, "clang-mcdc.txt"), "w").write(show)
    out, current = {}, None
    for line in show.splitlines():
        m = re.search(r"MC/DC Decision Region \((\d+):", line)
        if m:
            current = line_of.get(int(m.group(1))); continue
        m = re.search(r"C(\d+)-Pair: (covered|not covered)", line)
        if m and current is not None:
            out.setdefault(current, {})[int(m.group(1)) - 1] = m.group(2) == "covered"
    return {k: [v[i] for i in sorted(v)] for k, v in out.items()}

# --- comparing ----------------------------------------------------------------------------------------

def fmt(v):
    if v is None: return "-"
    if isinstance(v, str): return v
    return "".join("Y" if x else "n" for x in v)

def main():
    runner, work = sys.argv[1], sys.argv[2]
    os.makedirs(work, exist_ok=True)
    tutorial_cases()
    first_random = len(CASES)
    random_cases(300)

    tarp = run_tarp(runner, work)
    clang = run_clang(work)

    problems = []
    print("Tutorial cases (Y = condition covered, n = not; conditions left to right)\n")
    print(f"{'case':42} {'tutorial':9} {'Tarp':6} {'masking':8} {'clang':6} {'unique':7}  source / note")
    for k, c in enumerate(CASES[:first_random]):
        tree, leaves = parse(c["expr"])
        tests = [run_test(tree, c["params"], t) for t in c["tests"]]
        mask = masking_covered(tree, len(leaves), tests)
        uniq = unique_cause_covered(len(leaves), tests)
        expect = c["expect"]
        t = tarp[k]
        agrees = (expect is None or (expect == "not all" and t is not None and not all(t)) or expect == t)
        if not agrees: problems.append(f"{c['name']}: Tarp {fmt(t)}, the tutorial says {fmt(expect)}")
        if t != mask: problems.append(f"{c['name']}: Tarp {fmt(t)}, the masking checker {fmt(mask)}")
        if k in clang and clang[k] != uniq: problems.append(f"{c['name']}: clang {fmt(clang[k])}, the unique-cause checker {fmt(uniq)}")
        if k in clang and t is not None and any(cl and not tp for cl, tp in zip(clang[k], t)):
            problems.append(f"{c['name']}: clang shows a condition Tarp doesn't")
        print(f"{c['name']:42} {fmt(expect):9} {fmt(t):6} {fmt(mask):8} {fmt(clang.get(k)):6} {fmt(uniq):7}  {c['source']}{'; ' + c['note'] if c['note'] else ''}")

    stats = dict(cases=0, conditions=0, tarp_vs_masking=0, clang_vs_unique=0, clang_unsupported=0, clang_not_tarp=0, tarp_not_clang=0)
    examples = []
    for k, c in enumerate(CASES[first_random:], first_random):
        tree, leaves = parse(c["expr"])
        tests = [run_test(tree, c["params"], t) for t in c["tests"]]
        mask = masking_covered(tree, len(leaves), tests)
        uniq = unique_cause_covered(len(leaves), tests)
        t, cl = tarp[k], clang.get(k)
        stats["cases"] += 1
        stats["conditions"] += len(leaves)
        if t != mask:
            stats["tarp_vs_masking"] += 1; problems.append(f"{c['name']} {c['expr']}: Tarp {fmt(t)}, masking checker {fmt(mask)}")
        if cl is None:
            stats["clang_unsupported"] += 1
        elif cl != uniq:
            stats["clang_vs_unique"] += 1; problems.append(f"{c['name']} {c['expr']}: clang {fmt(cl)}, unique-cause checker {fmt(uniq)}")
        for a, b in zip(cl or [], t or []):
            if a and not b:
                stats["clang_not_tarp"] += 1; problems.append(f"{c['name']} {c['expr']}: clang shows a condition Tarp doesn't")
            if b and not a:
                stats["tarp_not_clang"] += 1
                if len(examples) < 3: examples.append((c["expr"], c["tests"], t, cl))

    print(f"\nRandom && || ! decisions: {stats['cases']} decisions, {stats['conditions']} conditions")
    print(f"  Tarp differs from the masking checker:            {stats['tarp_vs_masking']} decisions")
    print(f"  clang doesn't support (! over && or ||, it warns): {stats['clang_unsupported']} decisions")
    print(f"  clang differs from the unique-cause checker:      {stats['clang_vs_unique']} decisions (of the others)")
    print(f"  conditions clang shows and Tarp doesn't:          {stats['clang_not_tarp']}")
    print(f"  conditions Tarp shows and clang doesn't:          {stats['tarp_not_clang']}  (masking accepts more pairs than unique-cause)")
    for expr, tests, t, cl in examples:
        print(f"    e.g. {expr}  tests {[''.join('T' if v else 'F' for v in x) for x in tests]}  Tarp {fmt(t)}  clang {fmt(cl)}")

    # clang measures most of them: if it measured (almost) none, the tools or their output changed
    if stats["cases"] - stats["clang_unsupported"] < stats["cases"] // 2:
        problems.append(f"clang measured only {stats['cases'] - stats['clang_unsupported']} of {stats['cases']} random decisions")

    print("\n" + ("Problems:\n  " + "\n  ".join(problems) if problems else "No problems found."))
    return 1 if problems else 0

if __name__ == "__main__":
    sys.exit(main())
