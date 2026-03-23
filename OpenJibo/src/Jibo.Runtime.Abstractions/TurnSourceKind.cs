namespace Jibo.Runtime.Abstractions;

public enum TurnSourceKind
{
    NativeJibo = 0,
    Simulator = 1,
    TestHarness = 2,
    Api = 3
}