namespace Tarp.Types;

public enum SideOfParent { Left, Right, Unary };
public class PathElem
{
    public McDcNode Expr { get; set; }
    public SideOfParent Side { get; set; }
    public PathElem? Up { get; set; }
}
