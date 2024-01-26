namespace Tarp.Types;

/*
 * some references
 * https://github.com/ashalkhakov/qvm2wasm/blob/master/ir.c
 * 
 * https://www.cs.auckland.ac.nz/~ewan/essays/nlip.html
 * https://www.eecs.yorku.ca/course_archive/2011-12/W/4313/slides/11-Paths.pdf
 * https://ethz.ch/content/dam/ethz/special-interest/infk/inst-cs/lst-dam/documents/Education/Classes/Spring2018/210_Compiler_Design/Slides/w10_02_dataflow-2_18.pdf
 * https://en.wikipedia.org/wiki/Decision-to-decision_path
 * https://en.wikipedia.org/wiki/Elementary_comparison_testing
 * http://www.semdesigns.com/Company/Publications/TestCoverage.pdf (follows a syntax-directed approach, we will be making a CFG first)
 * 
 * https://www.stridewiki.com/index.php/Source_Instrumentation_Overview
 * 
 * for doing edits to the input AST, use this:
 * https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.editing.syntaxeditor?view=roslyn-dotnet-4.7.0
 * - this is probably the easiest way to have handles into the tree that are preserved between edits
 */

public sealed class LinkedListNode<T>
{
    public LinkedListNode(T value)
    {
        _value = value;
    }
    private T _value;
    public LinkedListNode<T>? Next { get; set; }
    public LinkedListNode<T>? Prev { get; set; }
    public T Value { get => _value; set => _value = value; }
}
