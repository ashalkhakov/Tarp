namespace Tarp.Types;

public enum SideOfParent { Left, Right, Unary };
public record PathElem(McDcNode Expr, SideOfParent Side, PathElem? Up);
