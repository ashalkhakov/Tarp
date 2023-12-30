namespace Tarp.Types;

public record MarkerFunction(McDcNode Node, PathElem? Path, int TrueBit, int TrueMask, bool TrueDone, int FalseBit, int FalseMask, bool FalseDone);
