using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Tarp;

internal static class SyntaxHelpers
{
    public static bool IsLastStatementAReturn(BlockSyntax block)
    {
        if (block.Statements.Count == 0)
        {
            return false;
        }

        var last = block.Statements.Last();
        if (last is ReturnStatementSyntax rs)
        {
            return true;
        }
        else if (last is BlockSyntax subBlock)
        {
            return IsLastStatementAReturn(subBlock);
        }
        return false;
    }
}