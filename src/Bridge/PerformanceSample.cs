namespace DeadlockTranslate;

// Rolling 30-second script wall time, NOT a measurement of game FPS or CPU-only time.
public sealed record PerformanceSample(double DiscoveryMaxMs=0,double ReadMaxMs=0,double RenderMaxMs=0,double BridgeMaxMs=0,double WindowMaxMs=0,int WindowSamples=0,int SlowCallbacks=0,int DeferredWork=0) {
    public PerformanceSample Clean() {
        static double Time(double n)=>double.IsFinite(n)?Math.Clamp(n,0,60000):0;
        return new(Time(DiscoveryMaxMs),Time(ReadMaxMs),Time(RenderMaxMs),Time(BridgeMaxMs),Time(WindowMaxMs),Math.Clamp(WindowSamples,0,2000000),Math.Clamp(SlowCallbacks,0,2000000),Math.Clamp(DeferredWork,0,2000000));
    }
}
