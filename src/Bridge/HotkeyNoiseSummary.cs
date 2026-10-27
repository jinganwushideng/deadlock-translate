namespace DeadlockTranslate;

// Called only by the existing hook thread and its timer. No additional thread or disk I/O.
public sealed class HotkeyNoiseSummary {
    int mismatches;long lastSample;bool initialized;
    public static bool IsNoise(LogReason reason,KeyModifiers held,bool chatOpen) => reason==LogReason.ModifiersMismatch && (!chatOpen || held==0);
    public void Observe(LogReason reason,long now) { if(reason!=LogReason.ModifiersMismatch)return;if(mismatches<int.MaxValue)mismatches++;if(!initialized){lastSample=now;initialized=true;} }
    public int Drain(long now,bool force=false) {
        if(mismatches==0||(!force&&now-lastSample<30000))return 0;
        var count=mismatches;mismatches=0;lastSample=now;return count;
    }
}
