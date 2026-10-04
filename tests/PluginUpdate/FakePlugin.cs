// Stand-in for DiyFfbPedal.dll: same assembly name, content differs between the "old" and "new" builds.
// csc /target:library /out:old\DiyFfbPedal.dll FakePlugin.cs
// csc /target:library /out:new\DiyFfbPedal.dll /define:NEW FakePlugin.cs
public static class FakePlugin
{
#if NEW
    public const string Version = "new";
#else
    public const string Version = "old";
#endif
}
