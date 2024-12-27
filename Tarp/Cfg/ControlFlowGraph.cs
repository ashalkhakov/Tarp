using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Tarp.Cfg;

public class ControlFlowGraph
{
    private readonly List<CFGNode> nodes = new();

    public ControlFlowGraph()
    {
        EntryNode = AddNewNode(CFGNodeKind.Entry);
        ExitNode = AddNewNode(CFGNodeKind.Exit);
    }

    public int EntryId { get => EntryNode.Id; }

    public int ExitId { get => ExitNode.Id; }

    public CFGNode EntryNode { get; private set; }

    public CFGNode ExitNode { get; private set; }

    public IReadOnlyCollection<CFGNode> Nodes { get => nodes; }

    public CFGNode AddNewNode(CFGNodeKind kind)
    {
        var id = Nodes.Count;
        var node = new CFGNode(id, kind);
        nodes.Add(node);
        return node;
    }

    public void SanityCheck()
    {
        Debug.Assert(nodes.Count >= 2 && nodes.All(n => nodes[n.Id] == n));
        Debug.Assert(nodes[0].Kind == CFGNodeKind.Entry);
        Debug.Assert(nodes[1].Kind == CFGNodeKind.Exit);

        foreach (var node in nodes)
        {
            node.SanityCheck();
        }

        // TODO: check that all labelled nodes are pairwise distinct
    }

    private static string EscapeGraphViz(string s)
    {
        var output = new StringBuilder(s.Length);

        foreach (var c in s.AsSpan())
        {
            if (c == '\'')
            {
                output.Append("\\\'");
            }
            else if (c == '"')
            {
                output.Append("\\\"");
            }
            else if (c == '\n')
            {
                output.Append("\\n");
            }
            else if (c == '\f')
            {
                output.Append("\\f");
            }
            else if (c == '\r')
            {
                output.Append("\\r");
            }
            else if (c == '\t')
            {
                output.Append("\\t");
            }
            else if (c == '\v')
            {
                output.Append("\\v");
            }
            else if (c == '\b')
            {
                output.Append("\\b");
            }
            else
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }

    public string EmitCFG()
    {
        using var writer = new StringWriter();

        writer.Write("digraph G\n{\n");

        foreach (var n in Nodes)
        {
            var id = n.Id;

            string? name;
            name = $"n{id}";

            var label = NodeToLabel(n, name);

            writer.Write($"    {name} [ shape = \"box\"\n" +
                        $"             fontname = \"Monospace\"\n" +
                        $"             label = \"<{label}>\\l");

            var j = 0;
            foreach (var mid in n.Statements)
            {
                writer.Write($"{j++}:\t{EscapeGraphViz(mid.ToString())}");
                writer.Write("\\l");
            }
            writer.Write("\" ];\n");

            foreach (var succ in n.Successors)
            {
                writer.Write($"      {name} -> n{succ.Id};\n");
            }
        }
        writer.Write("}\n");

        return writer.ToString();
    }

    private static string NodeToLabel(CFGNode n, string? name)
    {
        var label = name;
        if (n.Kind == CFGNodeKind.Branch)
        {
            label = EscapeGraphViz(n.Condition.ToFullString());
        }
        else if (n.Kind == CFGNodeKind.Entry)
        {
            label = "entry";
        }
        else if (n.Kind == CFGNodeKind.Exit)
        {
            label = "exit";
        }
        else if (n.Kind == CFGNodeKind.Label)
        {
            label = EscapeGraphViz(n.Label.ToString());
        }

        return label;
    }
}
