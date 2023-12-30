namespace Tarp.Types;

public class MarkerFunction
{
    public McDcNode Node { get; set; }
    public PathElem? Path { get; set; }
    public int TrueBit { get; set; }
    public int TrueMask { get; set; }
    public bool TrueDone { get; set; }
    public int FalseBit { get; set; }
    public int FalseMask { get; set; }
    public bool FalseDone { get; set; }
}
