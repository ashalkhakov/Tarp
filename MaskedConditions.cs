namespace TarpTests.TestSources;

public class MaskedConditions
{
    public static bool Example1(bool A, bool B, bool C, bool D)
    {
TARPGEN.C.Inc(87);{
#line(9,8)-(9,36)"C:\\Users\\User\\source\\projects\\McDcTestCoverage\\TarpTestApp\\MaskedConditions.cs"
        // if A=true,B=true,C=false,D=true:
        // - B is not executed due to A
        // - C is masked by D
        var __tmp_4=(A || B) && (C || D);TARPGEN.C.Inc(88);return __tmp_4;
}        /*
         * I((F(A) || G(B)) && (H(C) || J(D)))
         * 
         * just save the values of each condition? and the value of the decision?
         * - initially, all ivs for the decision are set to not_executed.
         * - F,G,H,J are identity functions, but they also RECORD the input value:
         *   F(x) { iv["A"] = x; return x; }
         *   G(x) { iv["B"] = x; return x; }
         *   ...
         * - I is an identity function, but it also records all "ivs" into a vector:
         *   I(x) { outcome[0] = x; condition_vector[0] = {iv["A"],iv["B"],iv["C"],iv["D"]} return x; }
         */

        /*
         * https://conservancy.umn.edu/bitstream/handle/11299/217382/monitorAlgorithm_0.pdf?sequence=1
         * 
         */
    }
}
