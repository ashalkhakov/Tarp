using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Tarp.Types;

namespace Tarp.Cfg
{
    public class CFGNode
    {
        public int Id { get; set; }

        public SyntaxNode Condition { get; set; }
        public SyntaxNode Label { get; set; }
        public List<SyntaxNode> Statements { get; set; } = new();
        public List<CFGNode> Successors { get; } = new List<CFGNode>();
        public CFGNodeKind Kind { get; set; }

        public enum CFGNodeKind
        {
            Entry,
            Exit,
            Block,
            Branch,
            Label
        }

        public CFGNode(int id, CFGNodeKind kind)
        {
            Id = id;
            Kind = kind;
        }

        public override string ToString()
        {
            var successors = string.Join(",", Successors.Select(s => s.Id.ToString()));
            if (Kind == CFGNodeKind.Branch)
            {
                return $"{Id}: {Kind} {Condition}. Successors {successors}";
            }
            return $"{Id}: {Kind} {string.Join("\r\n", Statements.Select(s => s.ToString()))}. Successors: {successors}";
        }
    }

    public class ControlFlowGraph
    {
        public CFGNode Entry { get; set; }
        public CFGNode Exit { get; set; }
        public List<CFGNode> Nodes { get; } = new List<CFGNode>();

        public CFGNode AddNewNode(CFGNode.CFGNodeKind kind)
        {
            var id = Nodes.Count;
            var node = new CFGNode(id, kind);
            Nodes.Add(node);
            return node;
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
                    writer.Write($"{j}:\t{EscapeGraphViz(mid.ToString())}");
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
            if (n.Kind == CFGNode.CFGNodeKind.Branch)
            {
                label = EscapeGraphViz(n.Condition.ToFullString());
            }
            else if (n.Kind == CFGNode.CFGNodeKind.Entry)
            {
                label = "entry";
            }
            else if (n.Kind == CFGNode.CFGNodeKind.Exit)
            {
                label = "exit";
            }

            return label;
        }
    }
}
