using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Tarp.Types;

namespace Tarp.Cfg;

public class CfgBasedMcDcInstrumenter
{
    public CfgBasedMcDcInstrumenter()
    {
        _numCounters = 0;
        _counters = new();
        _counterIncreaseMethodName = SyntaxFactory.IdentifierName("TARPGEN.C.Inc"); // TODO: ensure it's fresh
    }

    private int _numCounters;
    private List<CounterInformation> _counters;
    readonly IdentifierNameSyntax _counterIncreaseMethodName;

    enum CounterKind : int
    {
        MethodEntry = 0,
        MethodExit = 1,
        Line = 2,
        Branch = 3
    }

    class CounterInformation
    {
        public int Id { get; set; }
        public CounterKind Kind { get; set; }
        public int CfgBlockId { get; set; }
        public TextSpan SourceSpan { get; set; }
        public MethodDeclarationSyntax? Method { get; set; } // only for entry/exit counters
    }

    private ExpressionStatementSyntax IncrementCounter(CounterInformation c)
    {
        var id = SyntaxFactory.Literal(c.Id);

        var expr0 = SyntaxFactory.InvocationExpression(
            _counterIncreaseMethodName,
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SeparatedList(
                    new[] {
                    // NOTE: remove this. it's here to show what location this counter maps to.
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(c.SourceSpan.ToString()))
                    ),

                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, id)
                    )
                    }
                )
            )
        );
        var expr = SyntaxFactory.ExpressionStatement(expr0);

        return expr;
    }

    public async Task Instrument(MethodDeclarationSyntax method, DocumentEditor editor)
    {
        var builder = new CfgBuildingVisitor(method);
        builder.Visit(method);
        var cfg = builder.GraphBuilder;

        Dictionary<int, TextSpan> dict = new();
        foreach (var block in cfg.Blocks)
        {
            var beg = block.First;

            // find the extent of the block
            // - it's the largest extent of the code contained by the block
            // - entries and exits from the block need special annotations
            var instr = block.Middle;
            var spanStart = -1;
            var spanEnd = 0;
            SyntaxNode? firstExpression = instr?.Value;
            SyntaxNode? lastExpression = instr?.Value;
            while (instr != null)
            {
                if (spanStart == -1)
                {
                    spanStart = instr.Value.SpanStart;
                    spanEnd = instr.Value.Span.End;
                }
                else
                {
                    spanEnd = instr.Value.Span.End;
                }
                if (instr.Next == null)
                {
                    lastExpression = instr.Value;
                }
                instr = instr.Next;
            }

            // it's a method entry. put a probe BEFORE the first node in the middle.
            if (beg is CfgBlockEntry)
            {
                var entryCounter = new CounterInformation
                {
                    CfgBlockId = block.Id,
                    SourceSpan = firstExpression.Span,

                    Kind = CounterKind.MethodEntry,
                    Id = _numCounters++,
                    Method = method
                };
                _counters.Add(entryCounter);
                editor.InsertBefore(firstExpression, IncrementCounter(entryCounter));
            }

            var end = block.Last;
            if (end is CfgBlockExit)
            {

            }
            else if (end is CfgBlockReturn r)
            {
                Debug.Assert(r.Expression != null);
                lastExpression = r.Expression;
                spanEnd = r.Expression.Span.End;

                var exitCounter = new CounterInformation
                {
                    CfgBlockId = block.Id,
                    SourceSpan = r.Expression.Span,

                    Kind = CounterKind.MethodExit,
                    Id = _numCounters++,
                    Method = method
                };
                _counters.Add(exitCounter);
                editor.InsertBefore(firstExpression, IncrementCounter(exitCounter));
            }
            else if (end is CfgBlockBranch br)
            {
                Debug.Assert(br.Instruction != null);
                lastExpression = br.Instruction;
                spanEnd = br.Instruction.Span.End;
            }
            else if (end is CfgBlockConditionalBranch cbr)
            {
                // TODO: add a branch counter here. setup the decision & conditions
                Debug.Assert(cbr.Cond != null);
                lastExpression = cbr.Cond;
                spanEnd = cbr.Cond.Span.End;
            }

            // 1. build a block to region map (basically a code span)
            // 2. go over each block
            // - put a probe as the last instruction of the block. the probe will increment block's region.
            // 3. what's the block exit?
            // - return
            // - non-conditional branch
            // - conditional branch: this is the MC/DC point
            //  - but we also want all of the other MC/DC points too! how to deal about those? those would be inside expressions or statements...
            var realSpan = TextSpan.FromBounds(spanStart, spanEnd);

            dict.Add(block.Id, realSpan);

            // mark block exit
            var lineCounter = new CounterInformation
            {
                CfgBlockId = block.Id,
                SourceSpan = realSpan,
                Kind = CounterKind.Line,
                Id = _numCounters++
            };
            _counters.Add(lineCounter);
            editor.InsertBefore(lastExpression, IncrementCounter(lineCounter));

            // slightly modify the last instruction of the block with the probe
            // - if no instructions at all: synthesize one...
            // - if at least one instruction:
            //   - e.g. x++; --> x++; IncrementProbe(<realSpan>);
            //
            // we can add annotations to the instructions though. "insert probe X before" or "insert probe X after".
            // - we already have the machinery to make the annotations work.
            // we need to know that the 'block' can be modified freely though. e.g.
            // if (C) x=1; <- we clearly can't change x=1 at all. we need it to be:
            // if (C) {x=1;} so that the basic block inside the conditional is braced.
            // - add a preprocessing phase to fix this? DONE: see SourceToNf.
        }
    }
}
